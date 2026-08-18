using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpsForge.Contracts;

namespace OpsForge.Server;

public sealed class SqliteRepository
{
    private readonly string _connectionString;
    private readonly object _gate = new();
    private DateTimeOffset _lastTelemetryPruneUtc = DateTimeOffset.MinValue;

    public SqliteRepository(IWebHostEnvironment environment)
    {
        var root = Environment.GetEnvironmentVariable("OPSFORGE_ROOT");
        var dataDirectory = !string.IsNullOrWhiteSpace(root)
            ? Path.Combine(root, "data")
            : Path.Combine(environment.ContentRootPath, "data");

        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "opsforge.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Initialize();
    }

    public string DatabaseLabel => "data/opsforge.db";
    public string SchemaVersion => "7.0";

    public IncidentDto? GetActiveIncident(string ruleKey)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, rule_key, agent_id, severity, category, title, evidence, recommendation,
                       first_seen_utc, last_seen_utc, resolved_utc, active
                FROM incidents
                WHERE rule_key = $ruleKey AND active = 1
                ORDER BY first_seen_utc DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$ruleKey", ruleKey);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadIncident(reader) : null;
        }
    }

    public void InsertIncident(IncidentDto incident)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO incidents
                    (id, rule_key, agent_id, severity, category, title, evidence, recommendation,
                     first_seen_utc, last_seen_utc, resolved_utc, active)
                VALUES
                    ($id, $ruleKey, $agentId, $severity, $category, $title, $evidence, $recommendation,
                     $firstSeen, $lastSeen, NULL, 1);
                """;
            BindIncident(command, incident);
            command.ExecuteNonQuery();
        }
    }

    public void TouchIncident(IncidentDto incident)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE incidents
                SET severity = $severity,
                    category = $category,
                    title = $title,
                    evidence = $evidence,
                    recommendation = $recommendation,
                    last_seen_utc = $lastSeen
                WHERE id = $id AND active = 1;
                """;
            BindIncident(command, incident);
            command.ExecuteNonQuery();
        }
    }

    public void ResolveIncident(string incidentId, DateTimeOffset resolvedUtc, string evidence)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE incidents
                SET active = 0,
                    evidence = $evidence,
                    last_seen_utc = $resolved,
                    resolved_utc = $resolved
                WHERE id = $id AND active = 1;
                """;
            command.Parameters.AddWithValue("$id", incidentId);
            command.Parameters.AddWithValue("$evidence", evidence);
            command.Parameters.AddWithValue("$resolved", ToDb(resolvedUtc));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<IncidentDto> GetIncidents(int limit = 200)
    {
        lock (_gate)
        {
            var results = new List<IncidentDto>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, rule_key, agent_id, severity, category, title, evidence, recommendation,
                       first_seen_utc, last_seen_utc, resolved_utc, active
                FROM incidents
                ORDER BY active DESC, first_seen_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadIncident(reader));
            }
            return results;
        }
    }

    public void AddTimelineEvent(
        string agentId,
        string sourceType,
        string sourceId,
        string eventType,
        string title,
        string detail,
        DateTimeOffset timestampUtc)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO timeline_events
                    (agent_id, source_type, source_id, event_type, title, detail, timestamp_utc)
                VALUES
                    ($agentId, $sourceType, $sourceId, $eventType, $title, $detail, $timestamp);
                """;
            command.Parameters.AddWithValue("$agentId", agentId);
            command.Parameters.AddWithValue("$sourceType", sourceType);
            command.Parameters.AddWithValue("$sourceId", sourceId);
            command.Parameters.AddWithValue("$eventType", eventType);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$detail", detail);
            command.Parameters.AddWithValue("$timestamp", ToDb(timestampUtc));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<TimelineEventDto> GetTimelineEvents(int limit = 150)
    {
        lock (_gate)
        {
            var results = new List<TimelineEventDto>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_id, agent_id, source_type, source_id, event_type, title, detail, timestamp_utc
                FROM timeline_events
                ORDER BY event_id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new TimelineEventDto
                {
                    EventId = reader.GetInt64(0),
                    AgentId = reader.GetString(1),
                    SourceType = reader.GetString(2),
                    SourceId = reader.GetString(3),
                    EventType = reader.GetString(4),
                    Title = reader.GetString(5),
                    Detail = reader.GetString(6),
                    TimestampUtc = FromDb(reader.GetString(7))
                });
            }
            return results;
        }
    }

    public void SaveCommand(CommandStatusDto commandStatus)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO commands
                    (command_id, agent_id, type, target, created_utc, delivered_utc, completed_utc,
                     success, result_message, verification_status, verified_utc, verification_message, requested_by)
                VALUES
                    ($commandId, $agentId, $type, $target, $created, $delivered, $completed,
                     $success, $result, $verificationStatus, $verified, $verificationMessage, $requestedBy)
                ON CONFLICT(command_id) DO UPDATE SET
                    delivered_utc = excluded.delivered_utc,
                    completed_utc = excluded.completed_utc,
                    success = excluded.success,
                    result_message = excluded.result_message,
                    verification_status = excluded.verification_status,
                    verified_utc = excluded.verified_utc,
                    verification_message = excluded.verification_message,
                    requested_by = excluded.requested_by;
                """;
            command.Parameters.AddWithValue("$commandId", commandStatus.CommandId.ToString("D"));
            command.Parameters.AddWithValue("$agentId", commandStatus.AgentId);
            command.Parameters.AddWithValue("$type", commandStatus.Type);
            command.Parameters.AddWithValue("$target", commandStatus.Target);
            command.Parameters.AddWithValue("$created", ToDb(commandStatus.CreatedUtc));
            command.Parameters.AddWithValue("$delivered", DbValue(commandStatus.DeliveredUtc));
            command.Parameters.AddWithValue("$completed", DbValue(commandStatus.CompletedUtc));
            command.Parameters.AddWithValue("$success", commandStatus.Success.HasValue ? (object)(commandStatus.Success.Value ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("$result", (object?)commandStatus.ResultMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$verificationStatus", commandStatus.VerificationStatus);
            command.Parameters.AddWithValue("$verified", DbValue(commandStatus.VerifiedUtc));
            command.Parameters.AddWithValue("$verificationMessage", (object?)commandStatus.VerificationMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$requestedBy", commandStatus.RequestedBy ?? string.Empty);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<CommandStatusDto> GetCommands(int limit = 50)
    {
        lock (_gate)
        {
            var results = new List<CommandStatusDto>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT command_id, agent_id, type, target, created_utc, delivered_utc, completed_utc,
                       success, result_message, verification_status, verified_utc, verification_message, requested_by
                FROM commands
                ORDER BY created_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new CommandStatusDto
                {
                    CommandId = Guid.Parse(reader.GetString(0)),
                    AgentId = reader.GetString(1),
                    Type = reader.GetString(2),
                    Target = reader.GetString(3),
                    CreatedUtc = FromDb(reader.GetString(4)),
                    DeliveredUtc = ReadNullableDate(reader, 5),
                    CompletedUtc = ReadNullableDate(reader, 6),
                    Success = reader.IsDBNull(7) ? null : reader.GetInt32(7) == 1,
                    ResultMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                    VerificationStatus = reader.GetString(9),
                    VerifiedUtc = ReadNullableDate(reader, 10),
                    VerificationMessage = reader.IsDBNull(11) ? null : reader.GetString(11),
                    RequestedBy = reader.IsDBNull(12) ? string.Empty : reader.GetString(12)
                });
            }
            return results;
        }
    }


    public PrimaryIncidentDto? GetActivePrimaryIncident(string correlationKey)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, correlation_key, agent_id, severity, title, summary, probable_root_cause,
                       blast_radius, confidence, confidence_score, signals_json,
                       first_seen_utc, last_seen_utc, resolved_utc, active
                FROM primary_incidents
                WHERE correlation_key = $correlationKey AND active = 1
                ORDER BY first_seen_utc DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$correlationKey", correlationKey);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPrimaryIncident(reader) : null;
        }
    }

    public void InsertPrimaryIncident(PrimaryIncidentDto incident)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO primary_incidents
                    (id, correlation_key, agent_id, severity, title, summary, probable_root_cause,
                     blast_radius, confidence, confidence_score, signals_json,
                     first_seen_utc, last_seen_utc, resolved_utc, active)
                VALUES
                    ($id, $correlationKey, $agentId, $severity, $title, $summary, $rootCause,
                     $blastRadius, $confidence, $confidenceScore, $signals,
                     $firstSeen, $lastSeen, NULL, 1);
                """;
            BindPrimaryIncident(command, incident);
            command.ExecuteNonQuery();
        }
    }

    public void TouchPrimaryIncident(PrimaryIncidentDto incident)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE primary_incidents
                SET severity = $severity,
                    title = $title,
                    summary = $summary,
                    probable_root_cause = $rootCause,
                    blast_radius = $blastRadius,
                    confidence = $confidence,
                    confidence_score = $confidenceScore,
                    signals_json = $signals,
                    last_seen_utc = $lastSeen
                WHERE id = $id AND active = 1;
                """;
            BindPrimaryIncident(command, incident);
            command.ExecuteNonQuery();
        }
    }

    public void ResolvePrimaryIncident(string incidentId, DateTimeOffset resolvedUtc, string summary)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE primary_incidents
                SET active = 0,
                    summary = $summary,
                    last_seen_utc = $resolved,
                    resolved_utc = $resolved
                WHERE id = $id AND active = 1;
                """;
            command.Parameters.AddWithValue("$id", incidentId);
            command.Parameters.AddWithValue("$summary", summary);
            command.Parameters.AddWithValue("$resolved", ToDb(resolvedUtc));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<PrimaryIncidentDto> GetPrimaryIncidents(int limit = 100)
    {
        lock (_gate)
        {
            var results = new List<PrimaryIncidentDto>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, correlation_key, agent_id, severity, title, summary, probable_root_cause,
                       blast_radius, confidence, confidence_score, signals_json,
                       first_seen_utc, last_seen_utc, resolved_utc, active
                FROM primary_incidents
                ORDER BY active DESC, first_seen_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadPrimaryIncident(reader));
            }
            return results;
        }
    }



    public bool AgentExists(string agentId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM agent_registry WHERE agent_id = $agentId LIMIT 1;";
            command.Parameters.AddWithValue("$agentId", agentId);
            return command.ExecuteScalar() is not null;
        }
    }

    public (string Hash, bool Revoked, string ClientCertificateThumbprint)? GetAgentCredential(string agentId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT api_key_hash, revoked, client_certificate_thumbprint FROM agent_registry WHERE agent_id = $agentId LIMIT 1;";
            command.Parameters.AddWithValue("$agentId", agentId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.GetInt32(1) == 1, reader.IsDBNull(2) ? string.Empty : reader.GetString(2)) : null;
        }
    }

    public void EnrollAgent(AgentEnrollmentRequest request, string apiKeyHash, string fingerprint, string clientCertificateThumbprint, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agent_registry
                    (agent_id, display_name, site, environment_name, machine_name, operating_system,
                     agent_version, api_key_hash, credential_fingerprint, enrolled_utc,
                     first_seen_utc, last_seen_utc, last_ip_address, status, revoked, revoked_utc, client_certificate_thumbprint)
                VALUES
                    ($agentId, $displayName, $site, $environmentName, '', '', '', $keyHash, $fingerprint,
                     $enrolled, NULL, NULL, '', 'enrolled', 0, NULL, $clientCertificateThumbprint);
                """;
            command.Parameters.AddWithValue("$agentId", request.AgentId);
            command.Parameters.AddWithValue("$displayName", request.DisplayName ?? string.Empty);
            command.Parameters.AddWithValue("$site", request.Site ?? string.Empty);
            command.Parameters.AddWithValue("$environmentName", request.EnvironmentName ?? string.Empty);
            command.Parameters.AddWithValue("$keyHash", apiKeyHash);
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$enrolled", ToDb(now));
            command.Parameters.AddWithValue("$clientCertificateThumbprint", clientCertificateThumbprint ?? string.Empty);
            command.ExecuteNonQuery();
            AddAgentStatusHistory(connection, request.AgentId, "enrolled", "Agent credential issued; awaiting first authenticated heartbeat.", now);
        }
    }

    public void RecordAgentHeartbeat(AgentHeartbeatRequest heartbeat, string remoteIpAddress, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            string previousStatus = string.Empty;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT status FROM agent_registry WHERE agent_id = $agentId LIMIT 1;";
                read.Parameters.AddWithValue("$agentId", heartbeat.AgentId);
                previousStatus = read.ExecuteScalar() as string ?? string.Empty;
            }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE agent_registry
                    SET display_name = $displayName,
                        site = $site,
                        environment_name = $environmentName,
                        machine_name = $machineName,
                        operating_system = $operatingSystem,
                        agent_version = $agentVersion,
                        first_seen_utc = COALESCE(first_seen_utc, $now),
                        last_seen_utc = $now,
                        last_ip_address = $ip,
                        status = 'online'
                    WHERE agent_id = $agentId AND revoked = 0;
                    """;
                update.Parameters.AddWithValue("$agentId", heartbeat.AgentId);
                update.Parameters.AddWithValue("$displayName", heartbeat.DisplayName ?? string.Empty);
                update.Parameters.AddWithValue("$site", heartbeat.Site ?? string.Empty);
                update.Parameters.AddWithValue("$environmentName", heartbeat.EnvironmentName ?? string.Empty);
                update.Parameters.AddWithValue("$machineName", heartbeat.MachineName ?? string.Empty);
                update.Parameters.AddWithValue("$operatingSystem", heartbeat.OperatingSystem ?? string.Empty);
                update.Parameters.AddWithValue("$agentVersion", heartbeat.AgentVersion ?? string.Empty);
                update.Parameters.AddWithValue("$now", ToDb(now));
                update.Parameters.AddWithValue("$ip", remoteIpAddress ?? string.Empty);
                update.ExecuteNonQuery();
            }
            if (!string.Equals(previousStatus, "online", StringComparison.OrdinalIgnoreCase))
            {
                AddAgentStatusHistory(connection, heartbeat.AgentId, "online",
                    $"Authenticated heartbeat accepted from {heartbeat.MachineName} ({remoteIpAddress}).", now, transaction);
            }
            transaction.Commit();
        }
    }

    public IReadOnlyList<AgentInventoryDto> GetAgentInventory()
    {
        lock (_gate)
        {
            var results = new List<AgentInventoryDto>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT agent_id, display_name, site, environment_name, machine_name, operating_system,
                       agent_version, credential_fingerprint, enrolled_utc, first_seen_utc, last_seen_utc,
                       last_ip_address, status, revoked, revoked_utc, client_certificate_thumbprint
                FROM agent_registry
                ORDER BY revoked ASC, display_name COLLATE NOCASE, agent_id COLLATE NOCASE;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadAgentInventory(reader));
            }
            return results;
        }
    }

    public AgentInventoryDto? GetAgentInventory(string agentId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT agent_id, display_name, site, environment_name, machine_name, operating_system,
                       agent_version, credential_fingerprint, enrolled_utc, first_seen_utc, last_seen_utc,
                       last_ip_address, status, revoked, revoked_utc, client_certificate_thumbprint
                FROM agent_registry WHERE agent_id = $agentId LIMIT 1;
                """;
            command.Parameters.AddWithValue("$agentId", agentId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAgentInventory(reader) : null;
        }
    }

    public IReadOnlyList<AgentStatusHistoryDto> GetAgentStatusHistory(string agentId, int limit = 100)
    {
        lock (_gate)
        {
            var results = new List<AgentStatusHistoryDto>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_id, agent_id, status, detail, timestamp_utc
                FROM agent_status_history
                WHERE agent_id = $agentId
                ORDER BY event_id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$agentId", agentId);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new AgentStatusHistoryDto
                {
                    EventId = reader.GetInt64(0),
                    AgentId = reader.GetString(1),
                    Status = reader.GetString(2),
                    Detail = reader.GetString(3),
                    TimestampUtc = FromDb(reader.GetString(4))
                });
            }
            return results;
        }
    }

    public void MarkAgentOffline(string agentId, DateTimeOffset now, string detail)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE agent_registry SET status = 'offline' WHERE agent_id = $agentId AND status = 'online' AND revoked = 0;";
            update.Parameters.AddWithValue("$agentId", agentId);
            var changed = update.ExecuteNonQuery();
            if (changed > 0)
            {
                AddAgentStatusHistory(connection, agentId, "offline", detail, now, transaction);
            }
            transaction.Commit();
        }
    }

    public void RotateAgentCredential(string agentId, string apiKeyHash, string fingerprint, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE agent_registry
                SET api_key_hash = $keyHash, credential_fingerprint = $fingerprint
                WHERE agent_id = $agentId AND revoked = 0;
                """;
            update.Parameters.AddWithValue("$agentId", agentId);
            update.Parameters.AddWithValue("$keyHash", apiKeyHash);
            update.Parameters.AddWithValue("$fingerprint", fingerprint);
            update.ExecuteNonQuery();
            AddAgentStatusHistory(connection, agentId, "credential-rotated",
                $"Agent API credential rotated. New fingerprint {fingerprint}.", now, transaction);
            transaction.Commit();
        }
    }

    public void RevokeAgent(string agentId, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE agent_registry
                SET revoked = 1, revoked_utc = $now, status = 'revoked'
                WHERE agent_id = $agentId AND revoked = 0;
                """;
            update.Parameters.AddWithValue("$agentId", agentId);
            update.Parameters.AddWithValue("$now", ToDb(now));
            var changed = update.ExecuteNonQuery();
            if (changed > 0)
            {
                AddAgentStatusHistory(connection, agentId, "revoked", "Agent credential revoked by an authenticated operator.", now, transaction);
            }
            transaction.Commit();
        }
    }

    public void RecordTelemetrySample(AgentHeartbeatRequest heartbeat, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using (var recent = connection.CreateCommand())
            {
                recent.CommandText = "SELECT timestamp_utc FROM telemetry_samples WHERE agent_id = $agentId ORDER BY sample_id DESC LIMIT 1;";
                recent.Parameters.AddWithValue("$agentId", heartbeat.AgentId);
                var value = recent.ExecuteScalar();
                if (value is string timestamp && now - FromDb(timestamp) < TimeSpan.FromSeconds(15)) return;
            }

            var probes = heartbeat.Probes ?? new List<ProbeMetric>();
            var probeTotal = probes.Count;
            var probeFailed = probes.Count(p => !p.Success);
            var probeAverageLatency = probeTotal == 0 ? 0.0 : probes.Average(p => (double)p.LatencyMs);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO telemetry_samples
                        (agent_id, timestamp_utc, cpu_percent, memory_used_percent, probe_total, probe_failed, probe_average_latency_ms)
                    VALUES
                        ($agentId, $timestamp, $cpu, $memory, $probeTotal, $probeFailed, $probeLatency);
                    """;
                command.Parameters.AddWithValue("$agentId", heartbeat.AgentId);
                command.Parameters.AddWithValue("$timestamp", ToDb(now));
                command.Parameters.AddWithValue("$cpu", heartbeat.CpuPercent);
                command.Parameters.AddWithValue("$memory", heartbeat.MemoryUsedPercent);
                command.Parameters.AddWithValue("$probeTotal", probeTotal);
                command.Parameters.AddWithValue("$probeFailed", probeFailed);
                command.Parameters.AddWithValue("$probeLatency", probeAverageLatency);
                command.ExecuteNonQuery();
            }

            if (now - _lastTelemetryPruneUtc >= TimeSpan.FromHours(6))
            {
                using var prune = connection.CreateCommand();
                prune.CommandText = "DELETE FROM telemetry_samples WHERE timestamp_utc < $cutoff;";
                prune.Parameters.AddWithValue("$cutoff", ToDb(now.AddDays(-30)));
                prune.ExecuteNonQuery();
                _lastTelemetryPruneUtc = now;
            }
        }
    }

    public IReadOnlyList<TelemetrySampleRecord> GetTelemetrySamples(DateTimeOffset startUtc, DateTimeOffset endUtc, string? agentId = null, int limit = 500000)
    {
        lock (_gate)
        {
            var results = new List<TelemetrySampleRecord>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = string.IsNullOrWhiteSpace(agentId)
                ? """
                    SELECT sample_id, agent_id, timestamp_utc, cpu_percent, memory_used_percent, probe_total, probe_failed, probe_average_latency_ms
                    FROM telemetry_samples
                    WHERE timestamp_utc >= $start AND timestamp_utc <= $end
                    ORDER BY timestamp_utc ASC
                    LIMIT $limit;
                    """
                : """
                    SELECT sample_id, agent_id, timestamp_utc, cpu_percent, memory_used_percent, probe_total, probe_failed, probe_average_latency_ms
                    FROM telemetry_samples
                    WHERE agent_id = $agentId COLLATE NOCASE AND timestamp_utc >= $start AND timestamp_utc <= $end
                    ORDER BY timestamp_utc ASC
                    LIMIT $limit;
                    """;
            command.Parameters.AddWithValue("$start", ToDb(startUtc));
            command.Parameters.AddWithValue("$end", ToDb(endUtc));
            command.Parameters.AddWithValue("$limit", limit);
            if (!string.IsNullOrWhiteSpace(agentId)) command.Parameters.AddWithValue("$agentId", agentId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new TelemetrySampleRecord
                {
                    SampleId = reader.GetInt64(0),
                    AgentId = reader.GetString(1),
                    TimestampUtc = FromDb(reader.GetString(2)),
                    CpuPercent = reader.GetDouble(3),
                    MemoryUsedPercent = reader.GetDouble(4),
                    ProbeTotal = reader.GetInt32(5),
                    ProbeFailed = reader.GetInt32(6),
                    ProbeAverageLatencyMs = reader.GetDouble(7)
                });
            }
            return results;
        }
    }

    public IReadOnlyList<PrimaryIncidentDto> GetPrimaryIncidentsSince(DateTimeOffset sinceUtc, int limit = 5000)
    {
        lock (_gate)
        {
            var results = new List<PrimaryIncidentDto>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, correlation_key, agent_id, severity, title, summary, probable_root_cause,
                       blast_radius, confidence, confidence_score, signals_json,
                       first_seen_utc, last_seen_utc, resolved_utc, active
                FROM primary_incidents
                WHERE first_seen_utc >= $since OR resolved_utc >= $since OR active = 1
                ORDER BY first_seen_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$since", ToDb(sinceUtc));
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read()) results.Add(ReadPrimaryIncident(reader));
            return results;
        }
    }

    public MaintenanceWindowDto CreateMaintenanceWindow(MaintenanceWindowCreateRequest request, string createdBy, DateTimeOffset now)
    {
        var item = new MaintenanceWindowDto
        {
            MaintenanceId = Guid.NewGuid().ToString("N"),
            Name = request.Name.Trim(),
            AgentId = string.IsNullOrWhiteSpace(request.AgentId) ? "*" : request.AgentId.Trim(),
            Reason = request.Reason.Trim(),
            StartUtc = request.StartUtc,
            EndUtc = request.EndUtc,
            CreatedBy = createdBy,
            CreatedUtc = now
        };
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO maintenance_windows
                    (maintenance_id, name, agent_id, reason, start_utc, end_utc, created_by, created_utc, cancelled, cancelled_by, cancelled_utc)
                VALUES
                    ($id, $name, $agentId, $reason, $start, $end, $createdBy, $created, 0, '', NULL);
                """;
            command.Parameters.AddWithValue("$id", item.MaintenanceId);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$agentId", item.AgentId);
            command.Parameters.AddWithValue("$reason", item.Reason);
            command.Parameters.AddWithValue("$start", ToDb(item.StartUtc));
            command.Parameters.AddWithValue("$end", ToDb(item.EndUtc));
            command.Parameters.AddWithValue("$createdBy", item.CreatedBy);
            command.Parameters.AddWithValue("$created", ToDb(item.CreatedUtc));
            command.ExecuteNonQuery();
        }
        item.ActiveNow = item.StartUtc <= now && item.EndUtc > now;
        return item;
    }

    public IReadOnlyList<MaintenanceWindowDto> GetMaintenanceWindows(DateTimeOffset? rangeStartUtc = null, DateTimeOffset? rangeEndUtc = null, bool includeCancelled = true, int limit = 500)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var results = new List<MaintenanceWindowDto>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            var filters = new List<string>();
            if (!includeCancelled) filters.Add("cancelled = 0");
            if (rangeStartUtc.HasValue) filters.Add("end_utc > $rangeStart");
            if (rangeEndUtc.HasValue) filters.Add("start_utc < $rangeEnd");
            command.CommandText = $"""
                SELECT maintenance_id, name, agent_id, reason, start_utc, end_utc, created_by, created_utc,
                       cancelled, cancelled_by, cancelled_utc
                FROM maintenance_windows
                {(filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters))}
                ORDER BY cancelled ASC, start_utc DESC
                LIMIT $limit;
                """;
            if (rangeStartUtc.HasValue) command.Parameters.AddWithValue("$rangeStart", ToDb(rangeStartUtc.Value));
            if (rangeEndUtc.HasValue) command.Parameters.AddWithValue("$rangeEnd", ToDb(rangeEndUtc.Value));
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = new MaintenanceWindowDto
                {
                    MaintenanceId = reader.GetString(0),
                    Name = reader.GetString(1),
                    AgentId = reader.GetString(2),
                    Reason = reader.GetString(3),
                    StartUtc = FromDb(reader.GetString(4)),
                    EndUtc = FromDb(reader.GetString(5)),
                    CreatedBy = reader.GetString(6),
                    CreatedUtc = FromDb(reader.GetString(7)),
                    Cancelled = reader.GetInt32(8) == 1,
                    CancelledBy = reader.GetString(9),
                    CancelledUtc = ReadNullableDate(reader, 10)
                };
                item.ActiveNow = !item.Cancelled && item.StartUtc <= now && item.EndUtc > now;
                results.Add(item);
            }
            return results;
        }
    }

    public MaintenanceWindowDto? GetActiveMaintenanceWindow(string agentId, DateTimeOffset now)
    {
        return GetMaintenanceWindows(now.AddDays(-1), now.AddDays(1), includeCancelled: false)
            .Where(w => w.StartUtc <= now && w.EndUtc > now)
            .Where(w => w.AgentId == "*" || string.Equals(w.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => string.Equals(w.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(w => w.EndUtc)
            .FirstOrDefault();
    }

    public bool CancelMaintenanceWindow(string maintenanceId, string cancelledBy, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE maintenance_windows
                SET cancelled = 1, cancelled_by = $cancelledBy, cancelled_utc = $now
                WHERE maintenance_id = $id AND cancelled = 0;
                """;
            command.Parameters.AddWithValue("$id", maintenanceId);
            command.Parameters.AddWithValue("$cancelledBy", cancelledBy);
            command.Parameters.AddWithValue("$now", ToDb(now));
            return command.ExecuteNonQuery() > 0;
        }
    }

    public IncidentWorkflowRecord? GetIncidentWorkflow(string incidentId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT incident_id, acknowledged, acknowledged_by, acknowledged_utc,
                       owner_username, owner_display_name, assigned_utc, last_note, updated_utc
                FROM incident_workflow WHERE incident_id = $id LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", incidentId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new IncidentWorkflowRecord
            {
                IncidentId = reader.GetString(0),
                Acknowledged = reader.GetInt32(1) == 1,
                AcknowledgedBy = reader.GetString(2),
                AcknowledgedUtc = ReadNullableDate(reader, 3),
                OwnerUsername = reader.GetString(4),
                OwnerDisplayName = reader.GetString(5),
                AssignedUtc = ReadNullableDate(reader, 6),
                LastNote = reader.GetString(7),
                UpdatedUtc = FromDb(reader.GetString(8))
            };
        }
    }

    public void AcknowledgeIncident(string incidentId, string username, string note, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO incident_workflow
                    (incident_id, acknowledged, acknowledged_by, acknowledged_utc, owner_username, owner_display_name, assigned_utc, last_note, updated_utc)
                VALUES
                    ($id, 1, $username, $now, '', '', NULL, $note, $now)
                ON CONFLICT(incident_id) DO UPDATE SET
                    acknowledged = 1,
                    acknowledged_by = excluded.acknowledged_by,
                    acknowledged_utc = COALESCE(incident_workflow.acknowledged_utc, excluded.acknowledged_utc),
                    last_note = excluded.last_note,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$id", incidentId);
            command.Parameters.AddWithValue("$username", username);
            command.Parameters.AddWithValue("$note", note ?? string.Empty);
            command.Parameters.AddWithValue("$now", ToDb(now));
            command.ExecuteNonQuery();
        }
    }

    public void AssignIncident(string incidentId, string ownerUsername, string ownerDisplayName, string note, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO incident_workflow
                    (incident_id, acknowledged, acknowledged_by, acknowledged_utc, owner_username, owner_display_name, assigned_utc, last_note, updated_utc)
                VALUES
                    ($id, 0, '', NULL, $ownerUsername, $ownerDisplayName, $now, $note, $now)
                ON CONFLICT(incident_id) DO UPDATE SET
                    owner_username = excluded.owner_username,
                    owner_display_name = excluded.owner_display_name,
                    assigned_utc = excluded.assigned_utc,
                    last_note = excluded.last_note,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$id", incidentId);
            command.Parameters.AddWithValue("$ownerUsername", ownerUsername);
            command.Parameters.AddWithValue("$ownerDisplayName", ownerDisplayName);
            command.Parameters.AddWithValue("$note", note ?? string.Empty);
            command.Parameters.AddWithValue("$now", ToDb(now));
            command.ExecuteNonQuery();
        }
    }

    public void UnassignIncident(string incidentId, string note, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO incident_workflow
                    (incident_id, acknowledged, acknowledged_by, acknowledged_utc, owner_username, owner_display_name, assigned_utc, last_note, updated_utc)
                VALUES
                    ($id, 0, '', NULL, '', '', NULL, $note, $now)
                ON CONFLICT(incident_id) DO UPDATE SET
                    owner_username = '', owner_display_name = '', assigned_utc = NULL,
                    last_note = excluded.last_note, updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$id", incidentId);
            command.Parameters.AddWithValue("$note", note ?? string.Empty);
            command.Parameters.AddWithValue("$now", ToDb(now));
            command.ExecuteNonQuery();
        }
    }

    public int CountOperatorUsers()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM operator_users;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    public OperatorUserDto CreateOperatorUser(string username, string displayName, string role, string passwordSalt,
        string passwordHash, int passwordIterations, bool mustChangePassword, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO operator_users
                    (username, display_name, role, password_salt, password_hash, password_iterations,
                     enabled, must_change_password, created_utc, last_login_utc, password_changed_utc)
                VALUES
                    ($username, $displayName, $role, $salt, $hash, $iterations,
                     1, $mustChange, $created, NULL, NULL)
                RETURNING user_id;
                """;
            command.Parameters.AddWithValue("$username", username);
            command.Parameters.AddWithValue("$displayName", displayName);
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$salt", passwordSalt);
            command.Parameters.AddWithValue("$hash", passwordHash);
            command.Parameters.AddWithValue("$iterations", passwordIterations);
            command.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
            command.Parameters.AddWithValue("$created", ToDb(now));
            var userId = Convert.ToInt64(command.ExecuteScalar());
            return GetOperatorUser(userId)!;
        }
    }

    public OperatorUserAuthRecord? GetOperatorUserAuth(string username)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT user_id, username, display_name, role, enabled, must_change_password,
                       password_salt, password_hash, password_iterations, created_utc, last_login_utc, password_changed_utc
                FROM operator_users WHERE username = $username COLLATE NOCASE LIMIT 1;
                """;
            command.Parameters.AddWithValue("$username", username);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new OperatorUserAuthRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4) == 1,
                reader.GetInt32(5) == 1, reader.GetString(6), reader.GetString(7), reader.GetInt32(8), FromDb(reader.GetString(9)),
                ReadNullableDate(reader, 10), ReadNullableDate(reader, 11));
        }
    }

    public OperatorUserDto? GetOperatorUser(long userId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT user_id, username, display_name, role, enabled, must_change_password,
                       created_utc, last_login_utc, password_changed_utc
                FROM operator_users WHERE user_id = $userId LIMIT 1;
                """;
            command.Parameters.AddWithValue("$userId", userId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadOperatorUser(reader) : null;
        }
    }

    public IReadOnlyList<OperatorUserDto> GetOperatorUsers()
    {
        lock (_gate)
        {
            var results = new List<OperatorUserDto>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT user_id, username, display_name, role, enabled, must_change_password,
                       created_utc, last_login_utc, password_changed_utc
                FROM operator_users ORDER BY username COLLATE NOCASE;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) results.Add(ReadOperatorUser(reader));
            return results;
        }
    }

    public void UpdateOperatorLastLogin(long userId, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE operator_users SET last_login_utc = $now WHERE user_id = $userId;";
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$now", ToDb(now));
            command.ExecuteNonQuery();
        }
    }

    public void SetOperatorPassword(long userId, string salt, string hash, int iterations, bool mustChangePassword, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE operator_users
                SET password_salt = $salt, password_hash = $hash, password_iterations = $iterations,
                    must_change_password = $mustChange, password_changed_utc = $now
                WHERE user_id = $userId;
                """;
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$salt", salt);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$iterations", iterations);
            command.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
            command.Parameters.AddWithValue("$now", ToDb(now));
            command.ExecuteNonQuery();
        }
    }

    public void SetOperatorUserEnabled(long userId, bool enabled)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE operator_users SET enabled = $enabled WHERE user_id = $userId;";
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    public void CreateOperatorSession(long userId, string tokenHash, string csrfHash, DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc, string remoteIpAddress)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO operator_sessions(session_token_hash, user_id, csrf_token_hash, created_utc, expires_utc, remote_ip_address)
                VALUES ($token, $userId, $csrf, $created, $expires, $ip);
                """;
            command.Parameters.AddWithValue("$token", tokenHash);
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$csrf", csrfHash);
            command.Parameters.AddWithValue("$created", ToDb(createdUtc));
            command.Parameters.AddWithValue("$expires", ToDb(expiresUtc));
            command.Parameters.AddWithValue("$ip", remoteIpAddress ?? string.Empty);
            command.ExecuteNonQuery();
        }
    }

    public AuthPrincipal? GetOperatorSession(string tokenHash, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT u.user_id, u.username, u.display_name, u.role, u.enabled, u.must_change_password, s.expires_utc
                FROM operator_sessions s
                INNER JOIN operator_users u ON u.user_id = s.user_id
                WHERE s.session_token_hash = $token AND s.expires_utc > $now AND u.enabled = 1
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$token", tokenHash);
            command.Parameters.AddWithValue("$now", ToDb(now));
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new AuthPrincipal(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4) == 1, reader.GetInt32(5) == 1, FromDb(reader.GetString(6)), tokenHash)
                : null;
        }
    }

    public bool ValidateSessionCsrf(string tokenHash, string csrfHash, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT csrf_token_hash FROM operator_sessions WHERE session_token_hash = $token AND expires_utc > $now LIMIT 1;";
            command.Parameters.AddWithValue("$token", tokenHash);
            command.Parameters.AddWithValue("$now", ToDb(now));
            var expected = command.ExecuteScalar() as string;
            return expected is not null && SecuritySecrets.FixedEquals(expected, csrfHash);
        }
    }

    public void DeleteOperatorSession(string tokenHash)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM operator_sessions WHERE session_token_hash = $token;";
            command.Parameters.AddWithValue("$token", tokenHash);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteSessionsForUser(long userId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM operator_sessions WHERE user_id = $userId;";
            command.Parameters.AddWithValue("$userId", userId);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteOtherSessionsForUser(long userId, string keepTokenHash)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM operator_sessions WHERE user_id = $userId AND session_token_hash <> $keep;";
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$keep", keepTokenHash);
            command.ExecuteNonQuery();
        }
    }

    public void AddAuditEvent(string actorUsername, string actorDisplayName, string actorRole, string action, string target,
        string detail, string outcome, string remoteIpAddress, DateTimeOffset timestampUtc)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_log(actor_username, actor_display_name, actor_role, action, target, detail, outcome, remote_ip_address, timestamp_utc)
                VALUES ($username, $displayName, $role, $action, $target, $detail, $outcome, $ip, $timestamp);
                """;
            command.Parameters.AddWithValue("$username", actorUsername ?? string.Empty);
            command.Parameters.AddWithValue("$displayName", actorDisplayName ?? string.Empty);
            command.Parameters.AddWithValue("$role", actorRole ?? string.Empty);
            command.Parameters.AddWithValue("$action", action ?? string.Empty);
            command.Parameters.AddWithValue("$target", target ?? string.Empty);
            command.Parameters.AddWithValue("$detail", detail ?? string.Empty);
            command.Parameters.AddWithValue("$outcome", outcome ?? string.Empty);
            command.Parameters.AddWithValue("$ip", remoteIpAddress ?? string.Empty);
            command.Parameters.AddWithValue("$timestamp", ToDb(timestampUtc));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AuditEventDto> GetAuditEvents(int limit = 250)
    {
        lock (_gate)
        {
            var results = new List<AuditEventDto>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT audit_id, actor_username, actor_display_name, actor_role, action, target, detail, outcome, remote_ip_address, timestamp_utc
                FROM audit_log ORDER BY audit_id DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new AuditEventDto
                {
                    AuditId = reader.GetInt64(0), ActorUsername = reader.GetString(1), ActorDisplayName = reader.GetString(2),
                    ActorRole = reader.GetString(3), Action = reader.GetString(4), Target = reader.GetString(5), Detail = reader.GetString(6),
                    Outcome = reader.GetString(7), RemoteIpAddress = reader.GetString(8), TimestampUtc = FromDb(reader.GetString(9))
                });
            }
            return results;
        }
    }

    public void BindAgentCertificate(string agentId, string thumbprint, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE agent_registry SET client_certificate_thumbprint = $thumbprint WHERE agent_id = $agentId AND revoked = 0;";
            command.Parameters.AddWithValue("$agentId", agentId);
            command.Parameters.AddWithValue("$thumbprint", thumbprint ?? string.Empty);
            var changed = command.ExecuteNonQuery();
            if (changed > 0) AddAgentStatusHistory(connection, agentId, "certificate-bound", $"mTLS client certificate bound: {thumbprint}.", now, transaction);
            transaction.Commit();
        }
    }

    private static OperatorUserDto ReadOperatorUser(SqliteDataReader reader) => new()
    {
        UserId = reader.GetInt64(0),
        Username = reader.GetString(1),
        DisplayName = reader.GetString(2),
        Role = reader.GetString(3),
        Enabled = reader.GetInt32(4) == 1,
        MustChangePassword = reader.GetInt32(5) == 1,
        CreatedUtc = FromDb(reader.GetString(6)),
        LastLoginUtc = ReadNullableDate(reader, 7),
        PasswordChangedUtc = ReadNullableDate(reader, 8)
    };

    private static AgentInventoryDto ReadAgentInventory(SqliteDataReader reader)
    {
        var status = reader.GetString(12);
        return new AgentInventoryDto
        {
            AgentId = reader.GetString(0),
            DisplayName = reader.GetString(1),
            Site = reader.GetString(2),
            EnvironmentName = reader.GetString(3),
            MachineName = reader.GetString(4),
            OperatingSystem = reader.GetString(5),
            AgentVersion = reader.GetString(6),
            CredentialFingerprint = reader.GetString(7),
            EnrolledUtc = FromDb(reader.GetString(8)),
            FirstSeenUtc = reader.IsDBNull(9) ? null : FromDb(reader.GetString(9)),
            LastSeenUtc = reader.IsDBNull(10) ? null : FromDb(reader.GetString(10)),
            LastIpAddress = reader.GetString(11),
            Status = status,
            Online = string.Equals(status, "online", StringComparison.OrdinalIgnoreCase),
            Revoked = reader.GetInt32(13) == 1,
            RevokedUtc = reader.IsDBNull(14) ? null : FromDb(reader.GetString(14)),
            ClientCertificateThumbprint = reader.IsDBNull(15) ? string.Empty : reader.GetString(15)
        };
    }

    private static void AddAgentStatusHistory(SqliteConnection connection, string agentId, string status,
        string detail, DateTimeOffset now, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_status_history(agent_id, status, detail, timestamp_utc)
            VALUES ($agentId, $status, $detail, $timestamp);
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$timestamp", ToDb(now));
        command.ExecuteNonQuery();
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;

                CREATE TABLE IF NOT EXISTS metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                INSERT INTO metadata(key, value) VALUES ('schema_version', '7.0')
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;

                CREATE TABLE IF NOT EXISTS incidents (
                    id TEXT PRIMARY KEY,
                    rule_key TEXT NOT NULL,
                    agent_id TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    category TEXT NOT NULL,
                    title TEXT NOT NULL,
                    evidence TEXT NOT NULL,
                    recommendation TEXT NOT NULL,
                    first_seen_utc TEXT NOT NULL,
                    last_seen_utc TEXT NOT NULL,
                    resolved_utc TEXT NULL,
                    active INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_incidents_rule_active
                    ON incidents(rule_key, active);

                CREATE TABLE IF NOT EXISTS primary_incidents (
                    id TEXT PRIMARY KEY,
                    correlation_key TEXT NOT NULL,
                    agent_id TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    title TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    probable_root_cause TEXT NOT NULL,
                    blast_radius TEXT NOT NULL,
                    confidence TEXT NOT NULL,
                    confidence_score REAL NOT NULL,
                    signals_json TEXT NOT NULL,
                    first_seen_utc TEXT NOT NULL,
                    last_seen_utc TEXT NOT NULL,
                    resolved_utc TEXT NULL,
                    active INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_primary_incidents_correlation_active
                    ON primary_incidents(correlation_key, active);

                CREATE TABLE IF NOT EXISTS timeline_events (
                    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_id TEXT NOT NULL,
                    source_type TEXT NOT NULL,
                    source_id TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    title TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_timeline_timestamp
                    ON timeline_events(timestamp_utc DESC);



                CREATE TABLE IF NOT EXISTS agent_registry (
                    agent_id TEXT PRIMARY KEY COLLATE NOCASE,
                    display_name TEXT NOT NULL,
                    site TEXT NOT NULL,
                    environment_name TEXT NOT NULL,
                    machine_name TEXT NOT NULL,
                    operating_system TEXT NOT NULL,
                    agent_version TEXT NOT NULL,
                    api_key_hash TEXT NOT NULL,
                    credential_fingerprint TEXT NOT NULL,
                    enrolled_utc TEXT NOT NULL,
                    first_seen_utc TEXT NULL,
                    last_seen_utc TEXT NULL,
                    last_ip_address TEXT NOT NULL,
                    status TEXT NOT NULL,
                    revoked INTEGER NOT NULL DEFAULT 0,
                    revoked_utc TEXT NULL,
                    client_certificate_thumbprint TEXT NOT NULL DEFAULT ''
                );

                CREATE INDEX IF NOT EXISTS ix_agent_registry_status
                    ON agent_registry(status, revoked);

                CREATE TABLE IF NOT EXISTS agent_status_history (
                    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_agent_status_history_agent
                    ON agent_status_history(agent_id, event_id DESC);

                CREATE TABLE IF NOT EXISTS operator_users (
                    user_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    display_name TEXT NOT NULL,
                    role TEXT NOT NULL,
                    password_salt TEXT NOT NULL,
                    password_hash TEXT NOT NULL,
                    password_iterations INTEGER NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    must_change_password INTEGER NOT NULL DEFAULT 1,
                    created_utc TEXT NOT NULL,
                    last_login_utc TEXT NULL,
                    password_changed_utc TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS operator_sessions (
                    session_token_hash TEXT PRIMARY KEY,
                    user_id INTEGER NOT NULL,
                    csrf_token_hash TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    expires_utc TEXT NOT NULL,
                    remote_ip_address TEXT NOT NULL,
                    FOREIGN KEY(user_id) REFERENCES operator_users(user_id)
                );

                CREATE INDEX IF NOT EXISTS ix_operator_sessions_user ON operator_sessions(user_id);
                CREATE INDEX IF NOT EXISTS ix_operator_sessions_expires ON operator_sessions(expires_utc);

                CREATE TABLE IF NOT EXISTS audit_log (
                    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    actor_username TEXT NOT NULL,
                    actor_display_name TEXT NOT NULL,
                    actor_role TEXT NOT NULL,
                    action TEXT NOT NULL,
                    target TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    remote_ip_address TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_audit_log_timestamp ON audit_log(timestamp_utc DESC);

                CREATE TABLE IF NOT EXISTS telemetry_samples (
                    sample_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_id TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    cpu_percent REAL NOT NULL,
                    memory_used_percent REAL NOT NULL,
                    probe_total INTEGER NOT NULL,
                    probe_failed INTEGER NOT NULL,
                    probe_average_latency_ms REAL NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_telemetry_samples_agent_time
                    ON telemetry_samples(agent_id, timestamp_utc);
                CREATE INDEX IF NOT EXISTS ix_telemetry_samples_time
                    ON telemetry_samples(timestamp_utc);

                CREATE TABLE IF NOT EXISTS maintenance_windows (
                    maintenance_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    agent_id TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    start_utc TEXT NOT NULL,
                    end_utc TEXT NOT NULL,
                    created_by TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    cancelled INTEGER NOT NULL DEFAULT 0,
                    cancelled_by TEXT NOT NULL DEFAULT '',
                    cancelled_utc TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_maintenance_windows_time
                    ON maintenance_windows(start_utc, end_utc, cancelled);

                CREATE TABLE IF NOT EXISTS incident_workflow (
                    incident_id TEXT PRIMARY KEY,
                    acknowledged INTEGER NOT NULL DEFAULT 0,
                    acknowledged_by TEXT NOT NULL DEFAULT '',
                    acknowledged_utc TEXT NULL,
                    owner_username TEXT NOT NULL DEFAULT '',
                    owner_display_name TEXT NOT NULL DEFAULT '',
                    assigned_utc TEXT NULL,
                    last_note TEXT NOT NULL DEFAULT '',
                    updated_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS commands (
                    command_id TEXT PRIMARY KEY,
                    agent_id TEXT NOT NULL,
                    type TEXT NOT NULL,
                    target TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    delivered_utc TEXT NULL,
                    completed_utc TEXT NULL,
                    success INTEGER NULL,
                    result_message TEXT NULL,
                    verification_status TEXT NOT NULL,
                    verified_utc TEXT NULL,
                    verification_message TEXT NULL,
                    requested_by TEXT NOT NULL DEFAULT ''
                );
                """;
            command.ExecuteNonQuery();

            EnsureColumn(connection, "agent_registry", "client_certificate_thumbprint", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "commands", "requested_by", "TEXT NOT NULL DEFAULT ''");
            using (var cleanup = connection.CreateCommand())
            {
                cleanup.CommandText = "DELETE FROM operator_sessions WHERE expires_utc <= $now;";
                cleanup.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.UtcNow));
                cleanup.ExecuteNonQuery();
            }

            using var abandon = connection.CreateCommand();
            abandon.CommandText = """
                UPDATE commands
                SET completed_utc = $now,
                    success = 0,
                    result_message = 'Abandoned safely after OpsForge.Server restart.',
                    verification_status = 'Not verified',
                    verification_message = 'The command was not replayed after server restart.'
                WHERE completed_utc IS NULL;
                """;
            abandon.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.UtcNow));
            abandon.ExecuteNonQuery();
        }
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string declaration)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = check.ExecuteReader();
        var exists = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
        }
        reader.Close();
        if (exists) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {declaration};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void BindIncident(SqliteCommand command, IncidentDto incident)
    {
        command.Parameters.AddWithValue("$id", incident.Id);
        command.Parameters.AddWithValue("$ruleKey", incident.RuleKey);
        command.Parameters.AddWithValue("$agentId", incident.AgentId);
        command.Parameters.AddWithValue("$severity", incident.Severity);
        command.Parameters.AddWithValue("$category", incident.Category);
        command.Parameters.AddWithValue("$title", incident.Title);
        command.Parameters.AddWithValue("$evidence", incident.Evidence);
        command.Parameters.AddWithValue("$recommendation", incident.Recommendation);
        command.Parameters.AddWithValue("$firstSeen", ToDb(incident.FirstSeenUtc));
        command.Parameters.AddWithValue("$lastSeen", ToDb(incident.LastSeenUtc));
    }

    private static IncidentDto ReadIncident(SqliteDataReader reader)
    {
        var firstSeen = FromDb(reader.GetString(8));
        DateTimeOffset? resolved = reader.IsDBNull(10) ? null : FromDb(reader.GetString(10));
        var active = reader.GetInt32(11) == 1;
        var end = resolved ?? DateTimeOffset.UtcNow;

        return new IncidentDto
        {
            Id = reader.GetString(0),
            RuleKey = reader.GetString(1),
            AgentId = reader.GetString(2),
            Severity = reader.GetString(3),
            Category = reader.GetString(4),
            Title = reader.GetString(5),
            Evidence = reader.GetString(6),
            Recommendation = reader.GetString(7),
            FirstSeenUtc = firstSeen,
            LastSeenUtc = FromDb(reader.GetString(9)),
            ResolvedUtc = resolved,
            Active = active,
            DurationSeconds = Math.Max(0L, (long)(end - firstSeen).TotalSeconds)
        };
    }


    private static void BindPrimaryIncident(SqliteCommand command, PrimaryIncidentDto incident)
    {
        command.Parameters.AddWithValue("$id", incident.Id);
        command.Parameters.AddWithValue("$correlationKey", incident.CorrelationKey);
        command.Parameters.AddWithValue("$agentId", incident.AgentId);
        command.Parameters.AddWithValue("$severity", incident.Severity);
        command.Parameters.AddWithValue("$title", incident.Title);
        command.Parameters.AddWithValue("$summary", incident.Summary);
        command.Parameters.AddWithValue("$rootCause", incident.ProbableRootCause);
        command.Parameters.AddWithValue("$blastRadius", incident.BlastRadius);
        command.Parameters.AddWithValue("$confidence", incident.Confidence);
        command.Parameters.AddWithValue("$confidenceScore", incident.ConfidenceScore);
        command.Parameters.AddWithValue("$signals", JsonSerializer.Serialize(incident.Signals));
        command.Parameters.AddWithValue("$firstSeen", ToDb(incident.FirstSeenUtc));
        command.Parameters.AddWithValue("$lastSeen", ToDb(incident.LastSeenUtc));
    }

    private static PrimaryIncidentDto ReadPrimaryIncident(SqliteDataReader reader)
    {
        var firstSeen = FromDb(reader.GetString(11));
        DateTimeOffset? resolved = reader.IsDBNull(13) ? null : FromDb(reader.GetString(13));
        var active = reader.GetInt32(14) == 1;
        var end = resolved ?? DateTimeOffset.UtcNow;
        var signals = JsonSerializer.Deserialize<List<CorrelatedSignalDto>>(reader.GetString(10)) ?? new();

        return new PrimaryIncidentDto
        {
            Id = reader.GetString(0),
            CorrelationKey = reader.GetString(1),
            AgentId = reader.GetString(2),
            Severity = reader.GetString(3),
            Title = reader.GetString(4),
            Summary = reader.GetString(5),
            ProbableRootCause = reader.GetString(6),
            BlastRadius = reader.GetString(7),
            Confidence = reader.GetString(8),
            ConfidenceScore = reader.GetDouble(9),
            Signals = signals,
            FirstSeenUtc = firstSeen,
            LastSeenUtc = FromDb(reader.GetString(12)),
            ResolvedUtc = resolved,
            Active = active,
            DurationSeconds = Math.Max(0L, (long)(end - firstSeen).TotalSeconds)
        };
    }

    private static object DbValue(DateTimeOffset? value) => value.HasValue ? (object)ToDb(value.Value) : DBNull.Value;
    private static string ToDb(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset FromDb(string value) => DateTimeOffset.Parse(value).ToUniversalTime();
    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : FromDb(reader.GetString(ordinal));
}
