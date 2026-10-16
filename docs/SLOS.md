# OpsForge v1.3 — SLOs and Multi-Window Burn-Rate Alerting

OpsForge v1.3 adds explicit Service Level Objectives over the existing maintenance-aware reliability evidence.

## Core behavior

- SLOs are declared in `OpsForge.Server/slo-rules.json`.
- Empty `agentIds` means all non-revoked agents; explicit IDs scope an SLO to a service represented by known agents.
- Planned maintenance is excluded using the same semantics as reliability reporting.
- Missing telemetry becomes unavailable after the existing 20-second offline threshold.
- No-data is reported explicitly rather than counted as healthy.
- Error-budget consumption is derived from telemetry; schema remains 10.0.

## Burn rate

`allowed_error = 1 - objective`

`observed_error = downtime / monitored_time`

`burn_rate = observed_error / allowed_error`

A burn rate of `1x` consumes budget at exactly the rate that exhausts it at the end of the compliance window.

## Multi-window policies

The bundled disabled sample contains:

| Policy | Short | Long | Threshold | Severity |
|---|---:|---:|---:|---|
| fast | 5m | 1h | 14.4x | critical |
| medium | 30m | 6h | 6x | warning |
| slow | 2h | 24h | 3x | warning |

Both the short and long windows must exceed threshold before a policy alerts.

## API

Viewer:
- `GET /api/slos`
- `GET /api/slos/{id}`

Administrator:
- `GET /api/slos/status`

Evaluations return objective, compliance-window availability, monitored/downtime seconds, error budget consumed/remaining, burn-rate windows, and state (`healthy`, `burning`, `budget-exhausted`, `no-data`).

## Notifications

The hosted monitor emits `SloBurnRate` when a policy transitions into alerting and `SloRecovered` when it clears. These reuse the v1.2 asynchronous notification queue. Add the event names to any enabled route that should receive them. The v1.3 patch adds them to the disabled sample Slack route.

Alert state is transition-based in memory. After a server restart, a still-burning SLO can emit one fresh alert after startup; the telemetry evidence remains authoritative.
