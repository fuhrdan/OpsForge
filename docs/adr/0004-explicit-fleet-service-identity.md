# ADR-0004: Explicit fleet service identity and recovery evidence

## Context

Failures observed by separate agents may describe the same service outage. Time proximity alone cannot distinguish a shared dependency from simultaneous independent failures. A server restart also cannot be treated as recovery when no new measurement has arrived.

## Decision

Authenticated heartbeats may carry a `fleetServiceId`, a configured `fleetRuleId`, and a `fleetRole` of `source` or `observer`. A matching source must satisfy the rule's process/service and probe failure threshold before a fleet primary incident opens. Observers contribute failures of the rule's configured probes within the rule window. The server groups by service ID and rule ID, retains each affected agent's evidence and trace ID, and keeps unrelated signals as separate incidents. An observer cannot open a fleet primary incident on its own.

An affected agent is marked recovered only after a later complete healthy observation. The primary incident resolves once all affected agents have recovered. Missing or stale telemetry does not prove recovery. Persisting last heartbeats and primary incident evidence in SQLite makes this rule hold across server restarts. The trace ID of the first source heartbeat remains the primary incident trace; each affected agent has its own opening and optional recovery trace ID.

The service identifier is explicitly configured on agents with valid credentials. There is one source per service/rule identity and one service/rule identity per agent. The server validates the tag shape, role, and configured rule, but cannot independently confirm that the agent really belongs to the declared service. This feature is suitable for a trusted operations lab; tighter membership authorization is future work.

## Verification

`OpsForge.CorrelationChecks` checks source and observer grouping, distinct services, ordering and traces, and partial/missing recovery. `Test-OpsForge.ps1` covers authenticated multi-agent ingestion, report evidence, server restart, and complete recovery on a temporary SQLite database. `Start-OpsForge-ChaosLab.ps1` provides a manual visual demonstration.
