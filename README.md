# OpsForge v0.7.2

OpsForge is a self-contained C#/.NET 8 incident-response and NOC lab. **This v0.7.2 ZIP is a full build, not an incremental patch. You do not need v0.1-v0.6 or any files from an earlier OpsForge release.**

v0.7.2 adds a reliability command center on top of the complete agent authentication, RBAC, audit, correlation, topology, remediation, and optional mTLS feature set from earlier releases.

## Requirements

- Windows 10/11 or Windows Server for the complete local demo experience.
- .NET 8 SDK available as `dotnet` in `PATH`.
- PowerShell 5.1+.
- A modern browser.

No Node.js, Python, external chart package, external database server, or previous OpsForge installation is required. NuGet restore performed by `dotnet build` supplies the project's normal .NET package dependencies.

## First 60 seconds

1. Extract the **entire** `OpsForge-v0.7.2` folder from the ZIP.
2. Double-click `START-HERE.cmd`.
3. OpsForge restores/builds the solution, initializes a fresh SQLite database if needed, starts the server, demo HTTP service, and local Windows agent, then opens `http://localhost:5080`.
4. On the first run only, open `data/security/admin-bootstrap.txt`.
5. Sign in as `admin` using the temporary password in that file.
6. Choose a permanent password. The obsolete bootstrap credential file is deleted automatically after the change succeeds.
7. The local agent enrolls automatically. Use **Inject Demo Failure** to create a correlated outage, acknowledge/take ownership of it, preview the restart, execute it, and watch later telemetry verify recovery.
8. Open **Executive Reliability** to inspect availability, SLA/error budget, MTTR, incident trends, resource history, and per-node reliability.

`START-HERE.cmd` always performs a real `dotnet build` first and stops if compilation fails.

## v0.7 Reliability Command Center

The v0.7 dashboard adds persistent operational history rather than relying only on the latest heartbeat:

- fleet availability for 24 hours, 7 days, or 30 days
- configurable SLA target and remaining error budget
- CPU and memory history
- probe success rate and average latency
- incident-open/resolution trend
- average MTTR
- per-agent reliability table
- per-node historical telemetry chart
- maintenance-excluded monitored time
- active maintenance count
- acknowledged and unowned primary-incident counts

Telemetry samples are retained for 30 days. OpsForge records at most one reliability sample per agent every 15 seconds to keep the lab database compact.

### Availability calculation

OpsForge does not equate “no sample” with “healthy.” A heartbeat establishes a short healthy interval; when the gap exceeds the offline threshold, the remainder counts as unavailable until another heartbeat arrives. This lets the historical view reflect agent loss instead of silently dropping missing time.

Planned maintenance is removed from the SLA denominator. Maintenance cannot be backdated by more than five minutes, preventing an operator from retroactively rewriting a bad outage into planned maintenance.

## Incident workflow

Primary Incidents now support an operator lifecycle:

- **Acknowledge** — records that an operator has seen the incident.
- **Take ownership / assign** — persists the responsible Operator or Administrator.
- **Release ownership** — returns the incident to the unowned queue.
- **Maintenance suppression** — keeps the failure and evidence visible while removing it from the actionable NOC count during a valid maintenance window.
- **Preview → Execute → Verify** — retains the existing constrained-remediation workflow. Execution alone is not considered recovery; later telemetry must verify it.

Acknowledgement, assignment, maintenance scheduling/cancellation, remediation, and security actions are written to the persistent timeline/audit trail with operator attribution.

## Roles

- **Viewer** — read-only NOC, reliability, incident, topology, telemetry history, reports, inventory, and maintenance visibility.
- **Operator** — Viewer plus acknowledgement/ownership, maintenance scheduling/cancellation, failure injection, and remediation.
- **Administrator** — Operator plus user management, agent enrollment, API-key rotation/revocation, certificate binding, and the security audit log.

## Correlation, topology, and noise reduction

The full v0.7 build includes the earlier deterministic operations engine:

- process, Windows service, HTTP, TCP, and DNS monitoring
- multi-agent inventory and live telemetry
- dynamic topology and cross-machine dependency discovery
- process → listener → application dependency modeling
- deterministic incident correlation
- probable root cause and confidence
- blast-radius traversal
- derivative-signal suppression without deleting evidence
- incident reassessment as telemetry changes
- persistent incident and command history
- Markdown incident reports

The local demo models:

`OpsForge.DemoService process → TCP/5091 → HTTP /health`

Killing the demo process therefore provides several independent symptoms that OpsForge can correlate into a single Primary Incident.

## Operator authentication and audit

Operator passwords use PBKDF2-SHA256 with per-user random salts and 210,000 iterations. Interactive login creates an 8-hour server-side session. Browser mutation requests require the HttpOnly SameSite=Strict session cookie and the session CSRF token. Repeated failed logins are throttled.

Temporary passwords from account creation/reset are displayed once. Password reset revokes that user's active sessions and forces another password change.

The persistent audit log records actor, role, action, target, outcome, source IP, detail, and UTC timestamp. Remediation command records also carry the requesting operator's username.

## Agent enrollment and authentication

Every non-revoked agent has its own high-entropy API key. The server stores the key hash and fingerprint, not the plaintext credential. Agent heartbeat, command polling, and command result submission require that credential.

The independent bootstrap enrollment token is stored at:

`data/security/enrollment-token.txt`

A remote agent needs it only for first enrollment. The returned API key is then stored in the agent credentials file.

Administrators can rotate an API key or revoke an agent. Rotation invalidates the old key immediately.

## Optional agent mTLS

v0.7 preserves optional certificate-bound agent identity. Agents can generate and persist a client certificate, and an HTTPS OpsForge deployment can require both the correct API key and the bound client certificate thumbprint.

Example server settings:

```powershell
$env:OPSFORGE_AGENT_MTLS = '1'
$env:OPSFORGE_LISTEN_URL = 'https://0.0.0.0:5443'
# Configure the normal ASP.NET Core/Kestrel server certificate as appropriate.
```

When mandatory agent mTLS is enabled, remote agent traffic cannot downgrade to plain LAN HTTP. Loopback HTTP remains available for the local development lab. Browser sessions do not require client certificates.

Remote agents also refuse non-loopback HTTP by default unless `allowInsecureRemoteHttp=true` is deliberately chosen for a disposable trusted lab.

## Add another Windows agent

Create a copyable remote-agent package from this same full source build:

```powershell
.\Publish-Remote-Agent.ps1
```

This produces `dist\OpsForge.Agent`. Copy that folder to the remote Windows machine, edit `agent.json`, and on first run set:

```powershell
$env:OPSFORGE_AGENT_ENROLLMENT_TOKEN = '<server enrollment token>'
.\RUN-AGENT.cmd
```

For a real multi-machine deployment, use an HTTPS OpsForge endpoint and appropriate host firewall/reverse-proxy controls.

## Maintenance windows

Operators can schedule maintenance for:

- one enrolled agent, or
- `*` for fleet-wide maintenance.

A window includes a name, reason, start, end, creator, creation time, cancellation state, and cancellation attribution. Active maintenance mutes matching incidents/signals in the operator counts and excludes matching time from availability/SLA calculations. The original diagnostic evidence remains in history.

## Persistence and upgrades

The SQLite database is `data/opsforge.db`. Schema version is **7.0**.

A clean v0.7 extraction initializes schema 7 directly. If you intentionally copy an existing v0.6 `data/opsforge.db` into this full build, initialization adds the v0.7 reliability/workflow tables and preserves the existing v0.6 records. **Copying an old database is optional; v0.7 does not need it.**

New schema 7 persistence includes:

- `telemetry_samples`
- `maintenance_windows`
- `incident_workflow`

alongside the complete earlier incident, command, inventory, status, authentication, session, and audit tables.

## Important files

- `OpsForge.sln` — complete solution.
- `OpsForge.Contracts/` — shared DTO/contracts project.
- `OpsForge.Server/` — ASP.NET Core server, SQLite repository, correlation/topology/reliability engines, and complete browser UI.
- `OpsForge.Agent/` — Windows telemetry/probe/command agent.
- `OpsForge.DemoService/` — deliberately killable ASP.NET Core demo service.
- `START-HERE.cmd` — build and launch the full local lab.
- `STOP-OPSFORGE.cmd` — stop local OpsForge processes.
- `Test-OpsForge.ps1` — full-build compiler/runtime smoke test.
- `Publish-Remote-Agent.ps1` — publish a copyable framework-dependent remote Windows agent.
- `FULL-BUILD.txt` — standalone package manifest.
- `data/opsforge.db` — generated persistent schema-7 database; not shipped in the ZIP.
- `data/security/` — generated bootstrap/enrollment secrets; not shipped in the ZIP.
- `data/agents/` — generated local agent credentials/certificates; not shipped in the ZIP.

## Full-build smoke test

From PowerShell:

```powershell
.\Test-OpsForge.ps1
```

The test performs a real `dotnet build` and then exercises a clean schema-7 instance: bootstrap login/password replacement, RBAC denial, agent enrollment/authenticated telemetry, deterministic correlation, incident acknowledgement/ownership, maintenance suppression, reliability analytics/history, maintenance cancellation, inventory, and audit attribution.

The test uses a temporary data root and removes it afterward, so it does not overwrite your normal OpsForge database.

## Security scope

OpsForge is a portfolio/lab platform with increasingly production-oriented controls, not a claim of a completed enterprise security product. It does not yet include SSO/OIDC/SAML, MFA, a centralized secrets vault, full PKI lifecycle/CRL/OCSP, signed automatic updates, HA database/session storage, or a hardened production installer. Use HTTPS, firewall restrictions, least-privilege accounts, and appropriate reverse-proxy/security controls for any network-accessible deployment.
