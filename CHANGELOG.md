# Changelog

## v0.7.2

- Fixed `CS0104` in `OpsForge.Agent/SystemMetricsCollector.cs` by explicitly catching `System.TimeoutException` instead of the ambiguous `TimeoutException` name introduced by the `System.ServiceProcess` reference.
- Fixed both `CS0173` errors in `OpsForge.Server/SqliteRepository.cs` by explicitly typing nullable incident-resolution timestamps as `DateTimeOffset?`.
- Replaced the console banner middle-dot with ASCII punctuation to avoid mojibake in Windows `cmd.exe`.
- Complete standalone/full build: no previous OpsForge version is required.

# OpsForge Changelog

## v0.7.1

- Fixed the v0.7.0 C# build failure reported by the .NET compiler.
- Replaced invalid `Find(...)` usage on `IReadOnlyList` with LINQ `FirstOrDefault(...)`.
- Corrected analytics duration/count numeric typing so `long` duration values do not flow through `int` assumptions.
- Preserves every v0.7.0 feature and remains a complete standalone build; no previous OpsForge version is required.

## v0.7.0

- Shipped as a **complete standalone source build**; no earlier OpsForge ZIP or source tree is required.
- Added persistent rolling telemetry history in SQLite with 30-day retention and per-agent sample throttling.
- Added historical fleet/node reliability analytics for 24-hour, 7-day, and 30-day views.
- Added availability calculation that accounts for missing heartbeat intervals instead of silently treating missing telemetry as healthy.
- Added configurable SLA target and error-budget-remaining calculation.
- Added CPU/memory average and peak analytics plus probe success and latency history.
- Added incident-open/resolution trends and fleet/per-node MTTR analytics.
- Added a new Executive Reliability / NOC command-center dashboard with self-rendered SVG charts and no external JavaScript chart dependency.
- Added persistent Primary Incident acknowledgement and operator ownership workflow.
- Added acknowledged/unowned counts to the live NOC summary.
- Added planned maintenance windows for individual agents or the entire fleet.
- Added maintenance suppression that preserves evidence while removing planned failures from actionable-alert counts.
- Added maintenance exclusion from SLA/availability denominators.
- Added a five-minute maximum maintenance backdate to protect historical reliability integrity.
- Added attributed audit/timeline events for acknowledgement, assignment/unassignment, and maintenance scheduling/cancellation.
- Added per-node historical telemetry views from the topology/agent inspector.
- Added `telemetry_samples`, `maintenance_windows`, and `incident_workflow` persistence.
- Preserved all v0.6 RBAC/session/audit, v0.5 authenticated-agent, v0.4 topology/blast-radius, v0.3 correlation, and v0.2 persistence/remediation capabilities.
- Database schema version is now **7.0** and can migrate an existing v0.6 database in place while also supporting a clean standalone initialization.
- Expanded `Test-OpsForge.ps1` into a full-build smoke test covering compilation, schema 7, RBAC, authenticated telemetry, correlation, acknowledgement, ownership, maintenance, reliability/history, and audit.

## v0.6.0

- Added named operator accounts with Viewer / Operator / Administrator RBAC.
- Added PBKDF2-SHA256 password hashing with per-user salts and 210,000 iterations.
- Added forced first-login password change and one-time temporary passwords for create/reset flows.
- Added server-side sessions, CSRF protection, login throttling, persistent audit logs, operator attribution, and optional certificate-bound agent mTLS.

## v0.5.0

- Added authenticated agent enrollment, per-agent API keys, persistent inventory/status history, key rotation/revocation, and HTTPS-aware remote onboarding.

## v0.4.0

- Added multi-node topology, cross-machine dependency discovery, blast radius, and derivative-alert suppression.

## v0.3.0

- Added deterministic multi-signal incident correlation, confidence/root-cause reasoning, reassessment, and Primary Incident reporting.

## v0.2.0

- Added SQLite incident persistence, HTTP/TCP/DNS probes, Windows-service monitoring, MTTR, timeline history, and Preview → Execute → Verify remediation.

## v0.1.0

- Added the initial .NET 8 server, Windows telemetry agent, browser dashboard, constrained command channel, and deliberately killable demo service.
