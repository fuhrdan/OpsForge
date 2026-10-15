using Microsoft.Data.Sqlite;

namespace OpsForge.Server;

public sealed class NotificationStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public NotificationStore(IWebHostEnvironment environment)
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

    public long BeginDelivery(string routeId, string channelType, NotificationEnvelope envelope,
        string destinationLabel, int attempt, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO notification_deliveries
                    (route_id, channel_type, event_type, incident_id, severity,
                     destination_label, status, detail, attempt, created_utc, completed_utc)
                VALUES
                    ($route, $channel, $event, $incident, $severity,
                     $destination, 'pending', '', $attempt, $created, NULL);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$route", routeId);
            command.Parameters.AddWithValue("$channel", channelType);
            command.Parameters.AddWithValue("$event", envelope.EventType);
            command.Parameters.AddWithValue("$incident", envelope.IncidentId);
            command.Parameters.AddWithValue("$severity", envelope.Severity);
            command.Parameters.AddWithValue("$destination", destinationLabel);
            command.Parameters.AddWithValue("$attempt", attempt);
            command.Parameters.AddWithValue("$created", ToDb(now));
            return (long)(command.ExecuteScalar() ?? 0L);
        }
    }

    public void CompleteDelivery(long deliveryId, string status, string detail, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE notification_deliveries
                SET status = $status, detail = $detail, completed_utc = $completed
                WHERE delivery_id = $id;
                """;
            command.Parameters.AddWithValue("$id", deliveryId);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$detail", detail.Length > 1000 ? detail[..1000] : detail);
            command.Parameters.AddWithValue("$completed", ToDb(now));
            command.ExecuteNonQuery();
        }
    }

    public bool HasRecentSuccessfulDelivery(string routeId, string eventType, string incidentId, DateTimeOffset sinceUtc)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(1)
                FROM notification_deliveries
                WHERE route_id = $route AND event_type = $event AND incident_id = $incident
                  AND status = 'sent' AND created_utc >= $since;
                """;
            command.Parameters.AddWithValue("$route", routeId);
            command.Parameters.AddWithValue("$event", eventType);
            command.Parameters.AddWithValue("$incident", incidentId);
            command.Parameters.AddWithValue("$since", ToDb(sinceUtc));
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }
    }

    public bool HasAnySuccessfulDelivery(string routeId, string eventType, string incidentId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(1)
                FROM notification_deliveries
                WHERE route_id = $route AND event_type = $event AND incident_id = $incident AND status = 'sent';
                """;
            command.Parameters.AddWithValue("$route", routeId);
            command.Parameters.AddWithValue("$event", eventType);
            command.Parameters.AddWithValue("$incident", incidentId);
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }
    }

    public IReadOnlyList<NotificationDeliveryDto> GetDeliveries(int limit = 200)
    {
        lock (_gate)
        {
            var rows = new List<NotificationDeliveryDto>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT delivery_id, route_id, channel_type, event_type, incident_id, severity,
                       destination_label, status, detail, attempt, created_utc, completed_utc
                FROM notification_deliveries
                ORDER BY delivery_id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new NotificationDeliveryDto
                {
                    DeliveryId = reader.GetInt64(0),
                    RouteId = reader.GetString(1),
                    ChannelType = reader.GetString(2),
                    EventType = reader.GetString(3),
                    IncidentId = reader.GetString(4),
                    Severity = reader.GetString(5),
                    DestinationLabel = reader.GetString(6),
                    Status = reader.GetString(7),
                    Detail = reader.GetString(8),
                    Attempt = reader.GetInt32(9),
                    CreatedUtc = FromDb(reader.GetString(10)),
                    CompletedUtc = reader.IsDBNull(11) ? null : FromDb(reader.GetString(11))
                });
            }
            return rows;
        }
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS notification_deliveries (
                    delivery_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    route_id TEXT NOT NULL,
                    channel_type TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    incident_id TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    destination_label TEXT NOT NULL,
                    status TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    attempt INTEGER NOT NULL,
                    created_utc TEXT NOT NULL,
                    completed_utc TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_notification_delivery_incident
                    ON notification_deliveries(incident_id, event_type, route_id, created_utc);
                CREATE INDEX IF NOT EXISTS idx_notification_delivery_created
                    ON notification_deliveries(created_utc DESC);
                """;
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string ToDb(DateTimeOffset value) => value.UtcDateTime.ToString("O");
    private static DateTimeOffset FromDb(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
