using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using OpsForge.Contracts;
using OpsForge.Server;

var builder = WebApplication.CreateBuilder(args);
var mtlsConfigured = IsTruthy(Environment.GetEnvironmentVariable("OPSFORGE_AGENT_MTLS"));
if (mtlsConfigured)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ConfigureHttpsDefaults(https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.AllowAnyClientCertificate();
        });
    });
}

builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<SqliteRepository>();
builder.Services.AddSingleton<SecuritySecrets>();
builder.Services.AddSingleton<OperatorIdentity>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddSingleton<ReliabilityService>();

var app = builder.Build();
var secrets = app.Services.GetRequiredService<SecuritySecrets>();
var identity = app.Services.GetRequiredService<OperatorIdentity>();
Console.WriteLine($"OpsForge v0.7.2 security bootstrap ready. Enrollment token: {secrets.EnrollmentTokenPath}");
Console.WriteLine(File.Exists(secrets.BootstrapAdminPath)
    ? $"Initial administrator credentials (first run only): {secrets.BootstrapAdminPath}"
    : "Initial administrator bootstrap credentials have already been consumed.");
Console.WriteLine($"Agent mTLS: {(secrets.AgentMtlsEnabled ? "enabled for HTTPS agent endpoints" : "optional / disabled")}");

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (context.Request.IsHttps) context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", (SqliteRepository repository) => Results.Ok(new
{
    ok = true,
    service = "OpsForge Server",
    version = "0.7.2",
    persistence = repository.DatabaseLabel,
    correlationEngine = "deterministic-v2",
    topologyEngine = "dynamic-multinode-v1",
    reliabilityEngine = "historical-sla-v1",
    securityModel = "rbac-sessions-agent-mtls-v1",
    schemaVersion = repository.SchemaVersion,
    timeUtc = DateTimeOffset.UtcNow
}));

app.MapGet("/api/security/status", (HttpContext context, SecuritySecrets security) => Results.Ok(security.GetStatus(context.Request.IsHttps)));

app.MapPost("/api/auth/login", (HttpContext context, LoginRequest request, OperatorIdentity auth) =>
{
    var remoteIp = RemoteIp(context);
    var (principal, sessionToken, csrfToken, error) = auth.Login(request.Username, request.Password, remoteIp);
    if (principal is null || sessionToken is null || csrfToken is null) return Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);
    context.Response.Cookies.Append(OperatorIdentity.SessionCookieName, sessionToken, SessionCookie(context.Request.IsHttps));
    return Results.Ok(auth.ToSessionDto(principal, csrfToken));
});

app.MapGet("/api/auth/me", (HttpContext context, OperatorIdentity auth, SqliteRepository repository) =>
{
    var principal = CurrentPrincipal(context, auth);
    if (principal is null) return Results.Unauthorized();
    return Results.Ok(new AuthSessionDto
    {
        Authenticated = true,
        User = repository.GetOperatorUser(principal.UserId),
        CsrfToken = string.Empty,
        ExpiresUtc = principal.ExpiresUtc
    });
});

app.MapPost("/api/auth/logout", (HttpContext context, OperatorIdentity auth) =>
{
    var principal = CurrentPrincipal(context, auth);
    if (principal is null) return Results.Unauthorized();
    if (!auth.ValidateCsrf(principal, Header(context, "X-OpsForge-CSRF"))) return Results.Json(new { error = "CSRF validation failed." }, statusCode: StatusCodes.Status403Forbidden);
    auth.Logout(principal, RemoteIp(context));
    context.Response.Cookies.Delete(OperatorIdentity.SessionCookieName, new CookieOptions { Path = "/" });
    return Results.Ok(new { loggedOut = true });
});

app.MapPost("/api/auth/change-password", (HttpContext context, ChangePasswordRequest request, OperatorIdentity auth) =>
{
    var principal = CurrentPrincipal(context, auth);
    if (principal is null) return Results.Unauthorized();
    if (!auth.ValidateCsrf(principal, Header(context, "X-OpsForge-CSRF"))) return Results.Json(new { error = "CSRF validation failed." }, statusCode: StatusCodes.Status403Forbidden);
    var (success, error) = auth.ChangePassword(principal, request.CurrentPassword, request.NewPassword, RemoteIp(context));
    return success ? Results.Ok(new { changed = true }) : Results.BadRequest(new { error });
});

app.MapGet("/api/auth/users", (HttpContext context, OperatorIdentity auth) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Administrator, requireCsrf: false);
    return access.Failure ?? Results.Ok(auth.GetUsers());
});

app.MapPost("/api/auth/users", (HttpContext context, CreateOperatorUserRequest request, OperatorIdentity auth) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Administrator, requireCsrf: true);
    if (access.Failure is not null) return access.Failure;
    var (user, temp, error) = auth.CreateUser(request, access.Principal!, RemoteIp(context));
    return user is null ? Results.BadRequest(new { error }) : Results.Ok(new { user, temporaryPassword = temp, note = "Temporary password is shown once. The user must change it at first login." });
});

app.MapPost("/api/auth/users/{userId:long}/enabled", (HttpContext context, long userId, SetUserEnabledRequest request, OperatorIdentity auth) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Administrator, requireCsrf: true);
    if (access.Failure is not null) return access.Failure;
    var (success, error) = auth.SetEnabled(userId, request.Enabled, access.Principal!, RemoteIp(context));
    return success ? Results.Ok(new { enabled = request.Enabled }) : Results.BadRequest(new { error });
});

app.MapPost("/api/auth/users/{userId:long}/reset-password", (HttpContext context, long userId, OperatorIdentity auth) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Administrator, requireCsrf: true);
    if (access.Failure is not null) return access.Failure;
    var (temp, error) = auth.ResetPassword(userId, access.Principal!, RemoteIp(context));
    return temp is null ? Results.BadRequest(new { error }) : Results.Ok(new ResetPasswordResponse { Username = auth.GetUsers().First(u => u.UserId == userId).Username, TemporaryPassword = temp, Note = "Temporary password is shown once. Existing sessions were revoked." });
});

app.MapGet("/api/audit", (HttpContext context, OperatorIdentity auth, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Administrator, requireCsrf: false);
    return access.Failure ?? Results.Ok(repository.GetAuditEvents());
});

// Agent bootstrap enrollment remains non-interactive and is protected by the independent enrollment token.
app.MapPost("/api/enrollment/agents", (HttpContext context, AgentEnrollmentRequest request, SecuritySecrets security, AgentRegistry registry, SqliteRepository repository) =>
{
    if (RejectInsecureRemoteAgentRequest(context, security)) return Results.Json(new { error = "Agent mTLS mode rejects non-loopback HTTP. Use HTTPS for remote enrollment." }, statusCode: StatusCodes.Status426UpgradeRequired);
    if (!security.ValidateEnrollmentToken(Header(context, "X-OpsForge-Enrollment-Token"))) return Results.Unauthorized();
    var requireCert = RequireAgentCertificate(context, security);
    var connectionThumbprint = ClientCertificateThumbprint(context);
    if (requireCert && string.IsNullOrWhiteSpace(connectionThumbprint)) return Results.Json(new { error = "mTLS is enabled and this HTTPS enrollment did not present a client certificate." }, statusCode: StatusCodes.Status401Unauthorized);
    var (response, error) = registry.Enroll(request, connectionThumbprint);
    if (response is null) return Results.Conflict(new { error });
    repository.AddAuditEvent("agent-bootstrap", "Agent enrollment", "system", "agent.enroll", request.AgentId, "Agent enrolled using the independent bootstrap token.", "success", RemoteIp(context), DateTimeOffset.UtcNow);
    return Results.Ok(response);
});

app.MapPost("/api/admin/enrollment/agents", (HttpContext context, AgentEnrollmentRequest request, OperatorIdentity auth, AgentRegistry registry, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Administrator, requireCsrf: true);
    if (access.Failure is not null) return access.Failure;
    var (response, error) = registry.Enroll(request, request.ClientCertificateThumbprint);
    if (response is null) return Results.Conflict(new { error });
    Audit(repository, access.Principal!, "agent.enroll", request.AgentId, "Administrator enrolled agent and issued a one-time API key.", "success", context);
    return Results.Ok(response);
});

app.MapGet("/api/agent-inventory", (HttpContext context, OperatorIdentity auth, AgentRegistry registry) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Viewer, false);
    return access.Failure ?? Results.Ok(registry.GetInventory());
});

app.MapGet("/api/agent-inventory/{agentId}/history", (HttpContext context, string agentId, OperatorIdentity auth, AgentRegistry registry) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Viewer, false);
    return access.Failure ?? Results.Ok(registry.GetHistory(agentId));
});

app.MapPost("/api/agent-inventory/{agentId}/rotate-key", (HttpContext context, string agentId, OperatorIdentity auth, AgentRegistry registry, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Administrator, true);
    if (access.Failure is not null) return access.Failure;
    var (response, error) = registry.RotateCredential(agentId);
    Audit(repository, access.Principal!, "agent.credential.rotate", agentId, response is null ? error ?? "Rotation failed." : $"Agent API key rotated; fingerprint {response.CredentialFingerprint}.", response is null ? "failed" : "success", context);
    return response is null ? Results.BadRequest(new { error }) : Results.Ok(response);
});

app.MapPost("/api/agent-inventory/{agentId}/bind-certificate", (HttpContext context, string agentId, BindAgentCertificateRequest request, OperatorIdentity auth, AgentRegistry registry, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Administrator, true);
    if (access.Failure is not null) return access.Failure;
    var (success, error) = registry.BindCertificate(agentId, request.Thumbprint);
    Audit(repository, access.Principal!, "agent.certificate.bind", agentId, success ? $"Bound mTLS certificate {request.Thumbprint}." : error ?? "Certificate binding failed.", success ? "success" : "failed", context);
    return success ? Results.Ok(new { bound = true }) : Results.BadRequest(new { error });
});

app.MapPost("/api/agent-inventory/{agentId}/revoke", (HttpContext context, string agentId, OperatorIdentity auth, AgentRegistry registry, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Administrator, true);
    if (access.Failure is not null) return access.Failure;
    var revoked = registry.Revoke(agentId);
    Audit(repository, access.Principal!, "agent.revoke", agentId, revoked ? "Agent credential revoked." : "Agent not found.", revoked ? "success" : "failed", context);
    return revoked ? Results.Ok(new { revoked = true }) : Results.NotFound(new { error = "Agent not found." });
});

app.MapPost("/api/agents/heartbeat", (HttpContext context, AgentHeartbeatRequest heartbeat, SecuritySecrets security, AgentRegistry registry, TelemetryStore store) =>
{
    if (string.IsNullOrWhiteSpace(heartbeat.AgentId) || string.IsNullOrWhiteSpace(heartbeat.MachineName)) return Results.BadRequest(new { error = "AgentId and MachineName are required." });
    if (!AgentAuthorized(context, heartbeat.AgentId, security, registry)) return Results.Unauthorized();
    registry.RecordHeartbeat(heartbeat, RemoteIp(context));
    store.RecordHeartbeat(heartbeat);
    return Results.Ok(new { accepted = true, serverTimeUtc = DateTimeOffset.UtcNow });
});

app.MapGet("/api/agents", (HttpContext context, OperatorIdentity auth, TelemetryStore store) => ReadAccess(context, auth, () => Results.Ok(store.GetAgents())));
app.MapGet("/api/primary-incidents", (HttpContext context, OperatorIdentity auth, TelemetryStore store) => ReadAccess(context, auth, () => Results.Ok(store.GetPrimaryIncidents())));
app.MapGet("/api/incidents", (HttpContext context, OperatorIdentity auth, TelemetryStore store) => ReadAccess(context, auth, () => Results.Ok(store.GetIncidents())));
app.MapGet("/api/timeline", (HttpContext context, OperatorIdentity auth, TelemetryStore store) => ReadAccess(context, auth, () => Results.Ok(store.GetTimelineEvents())));
app.MapGet("/api/commands", (HttpContext context, OperatorIdentity auth, TelemetryStore store) => ReadAccess(context, auth, () => Results.Ok(store.GetCommands())));
app.MapGet("/api/topology", (HttpContext context, OperatorIdentity auth, TelemetryStore store) => ReadAccess(context, auth, () => Results.Ok(store.GetTopology())));
app.MapGet("/api/operator-summary", (HttpContext context, OperatorIdentity auth, TelemetryStore store) => ReadAccess(context, auth, () => Results.Ok(store.GetOperatorSummary())));

app.MapGet("/api/reliability", (HttpContext context, int? hours, double? slaTarget, OperatorIdentity auth, ReliabilityService reliability) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Viewer, false);
    return access.Failure ?? Results.Ok(reliability.GetDashboard(hours ?? 24, slaTarget ?? 99.9));
});

app.MapGet("/api/agents/{agentId}/history", (HttpContext context, string agentId, int? hours, OperatorIdentity auth, ReliabilityService reliability, AgentRegistry registry) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Viewer, false);
    if (access.Failure is not null) return access.Failure;
    if (!registry.GetInventory().Any(a => string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase))) return Results.NotFound(new { error = "Agent not found." });
    return Results.Ok(reliability.GetAgentHistory(agentId, hours ?? 24));
});

app.MapGet("/api/maintenance", (HttpContext context, OperatorIdentity auth, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Viewer, false);
    return access.Failure ?? Results.Ok(repository.GetMaintenanceWindows(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(90)));
});

app.MapPost("/api/maintenance", (HttpContext context, MaintenanceWindowCreateRequest request, OperatorIdentity auth, SqliteRepository repository, AgentRegistry registry) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Operator, true);
    if (access.Failure is not null) return access.Failure;
    var name = request.Name?.Trim() ?? string.Empty;
    var reason = request.Reason?.Trim() ?? string.Empty;
    var agentId = string.IsNullOrWhiteSpace(request.AgentId) ? "*" : request.AgentId.Trim();
    if (name.Length < 3 || name.Length > 100) return Results.BadRequest(new { error = "Maintenance name must be 3-100 characters." });
    if (reason.Length > 500) return Results.BadRequest(new { error = "Maintenance reason may not exceed 500 characters." });
    if (request.StartUtc == default || request.EndUtc == default || request.EndUtc <= request.StartUtc) return Results.BadRequest(new { error = "Maintenance end must be after its start." });
    if (request.StartUtc < DateTimeOffset.UtcNow.AddMinutes(-5)) return Results.BadRequest(new { error = "Maintenance cannot be backdated more than five minutes; reliability history must remain auditable." });
    if (request.EndUtc - request.StartUtc > TimeSpan.FromDays(30)) return Results.BadRequest(new { error = "A maintenance window may not exceed 30 days." });
    if (agentId != "*" && !registry.GetInventory().Any(a => !a.Revoked && string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase))) return Results.BadRequest(new { error = "Target agent is not enrolled." });
    request.Name = name;
    request.Reason = reason;
    request.AgentId = agentId;
    var item = repository.CreateMaintenanceWindow(request, access.Principal!.Username, DateTimeOffset.UtcNow);
    repository.AddTimelineEvent(agentId, "maintenance", item.MaintenanceId, "Scheduled", $"Maintenance scheduled: {item.Name}", $"{access.Principal.Username} scheduled {item.StartUtc:O} through {item.EndUtc:O}. {item.Reason}", DateTimeOffset.UtcNow);
    Audit(repository, access.Principal!, "maintenance.create", agentId, $"Scheduled '{item.Name}' from {item.StartUtc:O} to {item.EndUtc:O}. {item.Reason}", "success", context);
    return Results.Ok(item);
});

app.MapPost("/api/maintenance/{maintenanceId}/cancel", (HttpContext context, string maintenanceId, OperatorIdentity auth, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Operator, true);
    if (access.Failure is not null) return access.Failure;
    var existing = repository.GetMaintenanceWindows().FirstOrDefault(w => string.Equals(w.MaintenanceId, maintenanceId, StringComparison.OrdinalIgnoreCase));
    if (existing is null) return Results.NotFound(new { error = "Maintenance window not found." });
    var now = DateTimeOffset.UtcNow;
    var cancelled = repository.CancelMaintenanceWindow(maintenanceId, access.Principal!.Username, now);
    if (!cancelled) return Results.BadRequest(new { error = "Maintenance window is already cancelled." });
    repository.AddTimelineEvent(existing.AgentId, "maintenance", existing.MaintenanceId, "Cancelled", $"Maintenance cancelled: {existing.Name}", $"Cancelled by {access.Principal.Username}.", now);
    Audit(repository, access.Principal!, "maintenance.cancel", existing.AgentId, $"Cancelled '{existing.Name}'.", "success", context);
    return Results.Ok(new { cancelled = true });
});

app.MapPost("/api/primary-incidents/{incidentId}/acknowledge", (HttpContext context, string incidentId, IncidentAcknowledgeRequest request, OperatorIdentity auth, TelemetryStore store, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Operator, true);
    if (access.Failure is not null) return access.Failure;
    var incident = store.AcknowledgePrimaryIncident(incidentId, access.Principal!.Username, request.Note ?? string.Empty);
    Audit(repository, access.Principal!, "incident.acknowledge", incidentId, incident is null ? "Incident was not active or was not found." : (request.Note ?? string.Empty), incident is null ? "failed" : "success", context);
    return incident is null ? Results.NotFound(new { error = "Active primary incident not found." }) : Results.Ok(incident);
});

app.MapPost("/api/primary-incidents/{incidentId}/assign", (HttpContext context, string incidentId, IncidentAssignmentRequest request, OperatorIdentity auth, TelemetryStore store, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Operator, true);
    if (access.Failure is not null) return access.Failure;
    var owner = auth.GetUsers().FirstOrDefault(u => u.Enabled && string.Equals(u.Username, request.OwnerUsername?.Trim(), StringComparison.OrdinalIgnoreCase));
    if (owner is null || !OpsForgeRoles.AtLeast(owner.Role, OpsForgeRoles.Operator)) return Results.BadRequest(new { error = "Incident owner must be an enabled Operator or Administrator." });
    var incident = store.AssignPrimaryIncident(incidentId, owner.Username, owner.DisplayName, access.Principal!.Username, request.Note ?? string.Empty);
    Audit(repository, access.Principal!, "incident.assign", incidentId, incident is null ? "Incident was not active or was not found." : $"Assigned to {owner.Username}. {request.Note}", incident is null ? "failed" : "success", context);
    return incident is null ? Results.NotFound(new { error = "Active primary incident not found." }) : Results.Ok(incident);
});

app.MapPost("/api/primary-incidents/{incidentId}/unassign", (HttpContext context, string incidentId, IncidentAcknowledgeRequest request, OperatorIdentity auth, TelemetryStore store, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Operator, true);
    if (access.Failure is not null) return access.Failure;
    var incident = store.UnassignPrimaryIncident(incidentId, access.Principal!.Username, request.Note ?? string.Empty);
    Audit(repository, access.Principal!, "incident.unassign", incidentId, incident is null ? "Incident was not active or was not found." : (request.Note ?? string.Empty), incident is null ? "failed" : "success", context);
    return incident is null ? Results.NotFound(new { error = "Active primary incident not found." }) : Results.Ok(incident);
});

app.MapPost("/api/agents/{agentId}/chaos/kill-demo", (HttpContext context, string agentId, OperatorIdentity auth, TelemetryStore store, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Operator, true);
    if (access.Failure is not null) return access.Failure;
    var command = store.QueueChaosKill(agentId, access.Principal!.Username);
    Audit(repository, access.Principal!, "chaos.kill-demo", agentId, command is null ? "Failure injection could not be queued." : $"Queued KillProcess command {command.CommandId}.", command is null ? "failed" : "success", context);
    return command is null ? Results.BadRequest(new { error = "Agent not found or demo service is not registered on that agent." }) : Results.Accepted(value: command);
});

app.MapPost("/api/agents/{agentId}/remediations/restart-demo/preview", (HttpContext context, string agentId, OperatorIdentity auth, TelemetryStore store, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Operator, true);
    if (access.Failure is not null) return access.Failure;
    var preview = store.CreateRestartDemoPreview(agentId, access.Principal!.Username);
    Audit(repository, access.Principal!, "remediation.preview", agentId, preview is null ? "Restart preview could not be created." : $"Created restart preview {preview.PreviewToken}.", preview is null ? "failed" : "success", context);
    return preview is null ? Results.BadRequest(new { error = "Agent not found or demo service is not registered on that agent." }) : Results.Ok(preview);
});

app.MapPost("/api/remediations/{previewToken:guid}/execute", (HttpContext context, Guid previewToken, OperatorIdentity auth, TelemetryStore store, SqliteRepository repository) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Operator, true);
    if (access.Failure is not null) return access.Failure;
    var command = store.ExecutePreview(previewToken, access.Principal!.Username);
    Audit(repository, access.Principal!, "remediation.execute", previewToken.ToString("D"), command is null ? "Preview was invalid, expired, or already used." : $"Queued {command.Type} command {command.CommandId} for {command.AgentId}.", command is null ? "failed" : "success", context);
    return command is null ? Results.BadRequest(new { error = "Preview token is missing, expired, already used, or no longer valid." }) : Results.Accepted(value: command);
});

app.MapGet("/api/agents/{agentId}/commands/next", (HttpContext context, string agentId, SecuritySecrets security, AgentRegistry registry, TelemetryStore store) =>
{
    if (!AgentAuthorized(context, agentId, security, registry)) return Results.Unauthorized();
    var command = store.GetNextCommand(agentId);
    return command is null ? Results.NoContent() : Results.Ok(command);
});

app.MapPost("/api/agents/{agentId}/commands/{commandId:guid}/result", (HttpContext context, string agentId, Guid commandId, CommandResultRequest result, SecuritySecrets security, AgentRegistry registry, TelemetryStore store) =>
{
    if (!AgentAuthorized(context, agentId, security, registry)) return Results.Unauthorized();
    return store.CompleteCommand(agentId, commandId, result) ? Results.Ok(new { accepted = true }) : Results.NotFound(new { error = "Command not found." });
});

app.MapGet("/api/primary-incidents/{incidentId}/report", (HttpContext context, string incidentId, OperatorIdentity auth, TelemetryStore store) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Viewer, false);
    if (access.Failure is not null) return access.Failure;
    var incident = store.GetPrimaryIncidents().FirstOrDefault(i => string.Equals(i.Id, incidentId, StringComparison.OrdinalIgnoreCase));
    if (incident is null) return Results.NotFound(new { error = "Primary incident not found." });
    var events = store.GetTimelineEvents().Where(e => string.Equals(e.SourceType, "primary-incident", StringComparison.OrdinalIgnoreCase) && string.Equals(e.SourceId, incidentId, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.TimestampUtc).ToList();
    var report = new StringBuilder();
    report.AppendLine($"# OpsForge Primary Incident Report — {incident.Title}");
    report.AppendLine(); report.AppendLine($"- Incident ID: `{incident.Id}`"); report.AppendLine($"- Agent: `{incident.AgentId}`");
    report.AppendLine($"- Severity: **{incident.Severity.ToUpperInvariant()}**"); report.AppendLine($"- Confidence: **{incident.Confidence} ({incident.ConfidenceScore:P0})**");
    report.AppendLine($"- Opened: {incident.FirstSeenUtc:O}"); report.AppendLine($"- Resolved: {(incident.ResolvedUtc.HasValue ? incident.ResolvedUtc.Value.ToString("O") : "ACTIVE")}");
    report.AppendLine($"- Correlated MTTR / elapsed: {FormatDuration(incident.DurationSeconds)}");
    report.AppendLine($"- Acknowledged: {(incident.Acknowledged ? $"yes · {incident.AcknowledgedBy} · {(incident.AcknowledgedUtc.HasValue ? incident.AcknowledgedUtc.Value.ToString("O") : "time unavailable")}" : "no")}");
    report.AppendLine($"- Owner: {(string.IsNullOrWhiteSpace(incident.OwnerUsername) ? "unassigned" : $"{incident.OwnerDisplayName} ({incident.OwnerUsername})")}");
    report.AppendLine($"- Maintenance: {(incident.MaintenanceSuppressed ? $"muted · {incident.MaintenanceWindowName}" : "no active maintenance suppression")}");
    report.AppendLine(); report.AppendLine("## Summary"); report.AppendLine(incident.Summary);
    report.AppendLine(); report.AppendLine("## Probable root cause"); report.AppendLine(incident.ProbableRootCause); report.AppendLine(); report.AppendLine("## Blast radius"); report.AppendLine(incident.BlastRadius);
    report.AppendLine(); report.AppendLine("## Correlated signals"); foreach (var signal in incident.Signals) report.AppendLine($"- **{signal.SignalType} · {signal.Target}** — {signal.Role} — {signal.Evidence}");
    report.AppendLine(); report.AppendLine("## Correlation timeline"); foreach (var item in events) report.AppendLine($"- {item.TimestampUtc:O} — **{item.EventType}** — {item.Detail}");
    return Results.Text(report.ToString(), "text/markdown; charset=utf-8");
});

app.MapGet("/api/incidents/{incidentId}/report", (HttpContext context, string incidentId, OperatorIdentity auth, TelemetryStore store) =>
{
    var access = Authorize(context, auth, OpsForgeRoles.Viewer, false);
    if (access.Failure is not null) return access.Failure;
    var incident = store.GetIncidents().FirstOrDefault(i => string.Equals(i.Id, incidentId, StringComparison.OrdinalIgnoreCase));
    if (incident is null) return Results.NotFound(new { error = "Incident not found." });
    var events = store.GetTimelineEvents().Where(e => string.Equals(e.SourceType, "incident", StringComparison.OrdinalIgnoreCase) && string.Equals(e.SourceId, incidentId, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.TimestampUtc).ToList();
    var report = new StringBuilder(); report.AppendLine($"# OpsForge Diagnostic Signal Report — {incident.Title}"); report.AppendLine(); report.AppendLine($"- Incident ID: `{incident.Id}`"); report.AppendLine($"- Agent: `{incident.AgentId}`");
    report.AppendLine($"- Severity: **{incident.Severity.ToUpperInvariant()}**"); report.AppendLine($"- Category: {incident.Category}"); report.AppendLine($"- Opened: {incident.FirstSeenUtc:O}"); report.AppendLine($"- Resolved: {(incident.ResolvedUtc.HasValue ? incident.ResolvedUtc.Value.ToString("O") : "ACTIVE")}");
    report.AppendLine($"- MTTR / elapsed: {FormatDuration(incident.DurationSeconds)}"); report.AppendLine($"- Alert state: {(incident.Suppressed ? "SUPPRESSED" : "ACTIONABLE")}"); if (incident.Suppressed) report.AppendLine($"- Suppression: {incident.SuppressionReason}");
    if (incident.MaintenanceSuppressed) report.AppendLine($"- Maintenance window: {incident.MaintenanceWindowName}");
    report.AppendLine(); report.AppendLine("## Evidence"); report.AppendLine(incident.Evidence); report.AppendLine(); report.AppendLine("## Recommendation"); report.AppendLine(incident.Recommendation); report.AppendLine(); report.AppendLine("## Timeline");
    foreach (var item in events) report.AppendLine($"- {item.TimestampUtc:O} — **{item.EventType}** — {item.Detail}");
    return Results.Text(report.ToString(), "text/markdown; charset=utf-8");
});

var listenUrl = Environment.GetEnvironmentVariable("OPSFORGE_LISTEN_URL") ?? "http://127.0.0.1:5080";
app.Run(listenUrl);

static string? Header(HttpContext context, string name) => context.Request.Headers[name].FirstOrDefault();
static string? SessionToken(HttpContext context) => context.Request.Cookies.TryGetValue(OperatorIdentity.SessionCookieName, out var token) ? token : null;
static AuthPrincipal? CurrentPrincipal(HttpContext context, OperatorIdentity identity) => identity.Authenticate(SessionToken(context));
static string RemoteIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
static string? ClientCertificateThumbprint(HttpContext context) => context.Connection.ClientCertificate?.Thumbprint;
static bool RequireAgentCertificate(HttpContext context, SecuritySecrets security) => security.AgentMtlsEnabled && context.Request.IsHttps;
static bool AgentAuthorized(HttpContext context, string agentId, SecuritySecrets security, AgentRegistry registry) =>
    !RejectInsecureRemoteAgentRequest(context, security) &&
    registry.Authenticate(agentId, Header(context, "X-OpsForge-Agent-Key"), ClientCertificateThumbprint(context), RequireAgentCertificate(context, security));

static bool RejectInsecureRemoteAgentRequest(HttpContext context, SecuritySecrets security)
{
    if (!security.AgentMtlsEnabled || context.Request.IsHttps) return false;
    var address = context.Connection.RemoteIpAddress;
    return address is null || !System.Net.IPAddress.IsLoopback(address);
}

static (AuthPrincipal? Principal, IResult? Failure) Authorize(HttpContext context, OperatorIdentity identity, string requiredRole, bool requireCsrf)
{
    var principal = CurrentPrincipal(context, identity);
    if (principal is null) return (null, Results.Unauthorized());
    if (principal.MustChangePassword) return (principal, Results.Json(new { error = "Password change required before accessing OpsForge operations." }, statusCode: StatusCodes.Status403Forbidden));
    if (!OpsForgeRoles.AtLeast(principal.Role, requiredRole)) return (principal, Results.Json(new { error = $"Role '{requiredRole}' or higher is required." }, statusCode: StatusCodes.Status403Forbidden));
    if (requireCsrf && !identity.ValidateCsrf(principal, Header(context, "X-OpsForge-CSRF"))) return (principal, Results.Json(new { error = "CSRF validation failed. Sign in again if this browser session lost its CSRF token." }, statusCode: StatusCodes.Status403Forbidden));
    return (principal, null);
}

static IResult ReadAccess(HttpContext context, OperatorIdentity identity, Func<IResult> success)
{
    var access = Authorize(context, identity, OpsForgeRoles.Viewer, false);
    return access.Failure ?? success();
}

static void Audit(SqliteRepository repository, AuthPrincipal actor, string action, string target, string detail, string outcome, HttpContext context) =>
    repository.AddAuditEvent(actor.Username, actor.DisplayName, actor.Role, action, target, detail, outcome, RemoteIp(context), DateTimeOffset.UtcNow);

static CookieOptions SessionCookie(bool isHttps) => new()
{
    HttpOnly = true,
    Secure = isHttps,
    SameSite = SameSiteMode.Strict,
    Path = "/",
    MaxAge = TimeSpan.FromHours(8),
    IsEssential = true
};

static bool IsTruthy(string? value) => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
static string FormatDuration(long seconds) => seconds < 60 ? $"{seconds}s" : seconds < 3600 ? $"{seconds / 60}m {seconds % 60}s" : $"{seconds / 3600}h {(seconds % 3600) / 60}m";
