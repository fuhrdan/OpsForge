using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;

namespace OpsForge.Server;

public sealed class NotificationDispatcher : BackgroundService
{
    private readonly NotificationQueue _queue;
    private readonly NotificationConfiguration _configuration;
    private readonly NotificationStore _store;
    private readonly ILogger<NotificationDispatcher> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public NotificationDispatcher(
        NotificationQueue queue,
        NotificationConfiguration configuration,
        NotificationStore store,
        ILogger<NotificationDispatcher> logger)
    {
        _queue = queue;
        _configuration = configuration;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in _queue.ReadAllAsync(stoppingToken))
        {
            if (!_configuration.Enabled)
                continue;

            foreach (var route in _configuration.Routes.Where(route => route.Matches(envelope)))
            {
                try
                {
                    await DeliverRouteAsync(route, envelope, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Notification route {RouteId} failed for incident {IncidentId}.",
                        route.Id, envelope.IncidentId);
                }
            }
        }
    }

    private async Task DeliverRouteAsync(
        NotificationRoute route,
        NotificationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var cooldown = TimeSpan.FromSeconds(route.CooldownSeconds ?? _configuration.DefaultCooldownSeconds);
        if (cooldown > TimeSpan.Zero &&
            _store.HasRecentSuccessfulDelivery(
                route.Id, envelope.EventType, envelope.IncidentId, DateTimeOffset.UtcNow - cooldown))
            return;

        var deliveryId = _store.BeginDelivery(
            route.Id,
            route.ChannelType,
            envelope,
            DestinationLabel(route),
            1,
            DateTimeOffset.UtcNow);

        try
        {
            if (route.ChannelType == "webhook")
                await SendWebhookAsync(route, envelope, cancellationToken);
            else
                await SendEmailAsync(route, envelope);

            _store.CompleteDelivery(deliveryId, "sent", "Delivery completed.", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _store.CompleteDelivery(
                deliveryId,
                "failed",
                $"{ex.GetType().Name}: {ex.Message}",
                DateTimeOffset.UtcNow);
            throw;
        }
    }

    private async Task SendWebhookAsync(
        NotificationRoute route,
        NotificationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var target = Environment.GetEnvironmentVariable(route.TargetEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException(
                $"Environment variable {route.TargetEnvironmentVariable} is not configured.");

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Webhook destination must be an absolute HTTP(S) URL.");

        object payload = route.Format switch
        {
            "slack" => new { text = FormatPlainText(envelope) },
            "teams" => new
            {
                type = "message",
                attachments = new[]
                {
                    new
                    {
                        contentType = "application/vnd.microsoft.card.adaptive",
                        contentUrl = (string?)null,
                        content = new
                        {
                            type = "AdaptiveCard",
                            version = "1.5",
                            body = new object[]
                            {
                                new { type = "TextBlock", weight = "Bolder", size = "Medium",
                                    text = $"{envelope.EventType}: {envelope.Title}" },
                                new { type = "TextBlock", wrap = true, text = envelope.Summary },
                                new { type = "FactSet", facts = Facts(envelope) }
                            }
                        }
                    }
                }
            },
            _ => new
            {
                source = "OpsForge",
                eventType = envelope.EventType,
                incidentId = envelope.IncidentId,
                correlationKey = envelope.CorrelationKey,
                severity = envelope.Severity,
                title = envelope.Title,
                summary = envelope.Summary,
                probableRootCause = envelope.ProbableRootCause,
                blastRadius = envelope.BlastRadius,
                agentId = envelope.AgentId,
                fleetServiceId = envelope.FleetServiceId,
                traceId = envelope.TraceId,
                actor = envelope.Actor,
                ownerUsername = envelope.OwnerUsername,
                occurredUtc = envelope.OccurredUtc
            }
        };

        using var response = await _http.PostAsJsonAsync(uri, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Webhook returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}: " +
                detail[..Math.Min(detail.Length, 300)]);
        }
    }

    private async Task SendEmailAsync(NotificationRoute route, NotificationEnvelope envelope)
    {
        var smtp = _configuration.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.Host) || string.IsNullOrWhiteSpace(smtp.FromAddress))
            throw new InvalidOperationException("SMTP host and fromAddress must be configured.");

        var password = Environment.GetEnvironmentVariable(smtp.PasswordEnvironmentVariable) ?? string.Empty;

        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = smtp.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrWhiteSpace(smtp.Username))
            client.Credentials = new NetworkCredential(smtp.Username, password);

        using var message = new MailMessage
        {
            From = new MailAddress(smtp.FromAddress, smtp.FromDisplayName),
            Subject = $"[OpsForge {envelope.Severity.ToUpperInvariant()}] {envelope.EventType}: {envelope.Title}",
            Body = FormatPlainText(envelope)
        };

        foreach (var recipient in route.Recipients)
            message.To.Add(recipient);

        await client.SendMailAsync(message);
    }

    private static string DestinationLabel(NotificationRoute route) =>
        route.ChannelType == "email"
            ? string.Join(",", route.Recipients)
            : $"{route.Format}:{route.TargetEnvironmentVariable}";

    private static string FormatPlainText(NotificationEnvelope envelope)
    {
        var lines = new List<string>
        {
            $"OpsForge {envelope.EventType}: {envelope.Title}",
            $"Severity: {envelope.Severity}",
            $"Incident: {envelope.IncidentId}",
            $"Agent: {envelope.AgentId}"
        };

        if (!string.IsNullOrWhiteSpace(envelope.FleetServiceId))
            lines.Add($"Fleet service: {envelope.FleetServiceId}");
        if (!string.IsNullOrWhiteSpace(envelope.ProbableRootCause))
            lines.Add($"Probable root cause: {envelope.ProbableRootCause}");
        if (!string.IsNullOrWhiteSpace(envelope.Summary))
            lines.Add($"Summary: {envelope.Summary}");
        if (!string.IsNullOrWhiteSpace(envelope.BlastRadius))
            lines.Add($"Blast radius: {envelope.BlastRadius}");
        if (!string.IsNullOrWhiteSpace(envelope.Actor))
            lines.Add($"Actor: {envelope.Actor}");
        if (!string.IsNullOrWhiteSpace(envelope.OwnerUsername))
            lines.Add($"Owner: {envelope.OwnerUsername}");
        if (!string.IsNullOrWhiteSpace(envelope.TraceId))
            lines.Add($"Trace ID: {envelope.TraceId}");
        lines.Add($"Time: {envelope.OccurredUtc:O}");

        return string.Join(Environment.NewLine, lines);
    }

    private static object[] Facts(NotificationEnvelope envelope)
    {
        var facts = new List<object>
        {
            new { title = "Severity", value = envelope.Severity },
            new { title = "Incident", value = envelope.IncidentId },
            new { title = "Agent", value = envelope.AgentId }
        };

        if (!string.IsNullOrWhiteSpace(envelope.ProbableRootCause))
            facts.Add(new { title = "Root cause", value = envelope.ProbableRootCause });
        if (!string.IsNullOrWhiteSpace(envelope.OwnerUsername))
            facts.Add(new { title = "Owner", value = envelope.OwnerUsername });

        return facts.ToArray();
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
