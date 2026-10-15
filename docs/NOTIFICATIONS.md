# OpsForge v1.2.0 — Alert Routing & Notification Engine

OpsForge v1.2 sends notifications from **correlated Primary Incident lifecycle events**, not independently from every derivative failure signal.

## Pipeline

```text
authenticated telemetry
        |
        v
correlation
        |
        v
Primary Incident
        |
        v
bounded in-memory queue
        |
        v
route matching + cooldown
        |
        +--> generic JSON webhook
        +--> Slack-compatible webhook
        +--> Teams-compatible webhook
        +--> SMTP email
        |
        v
SQLite delivery history
```

External notification I/O happens in a background worker. A slow webhook or mail server does not block heartbeat ingestion.

## Lifecycle events

- `Opened`
- `Resolved`
- `Acknowledged`
- `Assigned`
- `Escalated`

## Safe defaults

`notification-rules.json` ships with the notification engine and every route disabled.

Webhook URLs and SMTP passwords are **not** stored in Git. Webhook routes reference environment-variable names.

Examples:

```powershell
$env:OPSFORGE_NOTIFY_WEBHOOK = 'https://internal.example/opsforge'
$env:OPSFORGE_SLACK_WEBHOOK = '<secret webhook URL>'
$env:OPSFORGE_TEAMS_WEBHOOK = '<secret webhook URL>'
$env:OPSFORGE_SMTP_PASSWORD = '<secret>'
```

Then explicitly enable the chosen route in:

```text
OpsForge.Server/notification-rules.json
```

## Route controls

A route can filter on:

- lifecycle event
- minimum severity
- agent ID
- fleet-service ID
- cooldown
- maintenance suppression

Maintenance-suppressed incidents are excluded by default.

## Escalation

The default escalation threshold is 900 seconds.

An active Primary Incident becomes eligible for `Escalated` when it:

- remains active,
- remains unacknowledged,
- is not maintenance suppressed,
- has exceeded the configured threshold.

Successful escalation delivery is persisted, preventing repeated escalation spam to the same route.

## Persistence

v1.2 uses SQLite schema **10.0** and adds:

```text
notification_deliveries
```

Each attempt records route, channel, lifecycle event, incident, severity, destination label, status, detail, attempt number, and timestamps.

Secrets are not persisted.

## APIs

Viewer:

```text
GET /api/notifications
```

Administrator:

```text
GET /api/notifications/status
POST /api/notifications/test
```

`/api/notifications/status` reveals whether an expected secret environment variable is configured, but never returns its value.

## Validation

```powershell
dotnet restore .\OpsForge.sln

dotnet build .\OpsForge.sln `
    --configuration Release

dotnet run `
    --project .\OpsForge.NotificationChecks\OpsForge.NotificationChecks.csproj `
    --configuration Release

powershell -ExecutionPolicy Bypass -File .\Test-OpsForge.ps1
```

Expected routing check:

```text
PASS: notification routing checks
```

The existing full smoke suite must also pass.
