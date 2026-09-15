# ADR-0003: Do Not Treat Missing Telemetry as Healthy Time

* **Status:** Accepted
* **Date:** 2026-09-15
* **Decision owners:** OpsForge
* **Scope:** Availability, SLA, error-budget, and reliability calculations

## Context

Reliability reporting depends on knowing whether a monitored system was available during a period of time.

A dangerous simplification is to assume that a system was healthy whenever no explicit failure was recorded.

That assumption breaks down when telemetry itself disappears.

Missing telemetry may indicate:

* an agent stopped running;
* network connectivity was lost;
* the monitored host became unavailable;
* the OpsForge server could no longer receive telemetry;
* monitoring configuration failed;
* credentials were revoked or invalid;
* the monitored system stopped producing evidence.

In these cases, absence of a failure signal is not evidence of availability.

Treating unknown time as healthy would systematically overstate reliability.

## Decision

OpsForge does not count prolonged missing telemetry as healthy monitored time.

A received heartbeat establishes a bounded interval during which the monitored agent can reasonably be considered reachable.

If no later heartbeat or telemetry arrives before the configured offline threshold expires, subsequent time is treated as unavailable until communication resumes.

Conceptually:

```text
heartbeat received
      ↓
short known-healthy interval
      ↓
offline threshold exceeded
      ↓
availability = unavailable
      ↓
telemetry resumes
      ↓
availability can become healthy again
```

Unknown gaps are therefore not silently converted into successful uptime.

## Rationale

Availability should describe observed service behavior, not optimistic assumptions.

If the monitoring system cannot establish that a host or agent remained reachable, OpsForge should not award that interval as healthy time.

This produces a more conservative reliability model, but it is also more defensible.

The distinction is especially important for:

* SLA calculations;
* remaining error budget;
* fleet availability;
* per-node reliability;
* outage duration;
* MTTR interpretation; and
* historical reporting.

## Maintenance Windows

Planned maintenance is treated separately from unexpected loss of telemetry.

Time covered by a valid maintenance window is excluded from the SLA denominator rather than classified as either healthy or failed production time.

This prevents planned operational work from consuming the same error budget as an unexpected outage.

Maintenance does not erase historical evidence.

Telemetry, incidents, and audit information remain available for investigation.

## Maintenance Integrity

OpsForge limits retrospective maintenance creation.

An operator should not be able to observe an outage and then rewrite a large historical interval as planned maintenance after the fact.

Backdating is therefore constrained.

This preserves the credibility of reliability and SLA reporting.

## Example

Suppose an agent reports healthy telemetry until 14:00.

The configured offline threshold allows a short grace period.

No further telemetry arrives until 14:20.

OpsForge should not calculate:

```text
14:00–14:20 = healthy
```

simply because no explicit failure probe was received.

Instead:

```text
14:00
last confirmed telemetry

14:00 → offline threshold
bounded known/reasonable interval

offline threshold → 14:20
unavailable

14:20
communication resumes
```

This reflects the fact that the platform lacked evidence of availability during the missing interval.

## Consequences

### Positive

* Prevents artificially inflated uptime.
* Produces more defensible SLA calculations.
* Makes monitoring loss operationally visible.
* Aligns availability with observable evidence.
* Prevents silent monitoring failures from appearing as perfect health.
* Keeps planned maintenance distinct from unexpected outages.
* Makes error-budget calculations more meaningful.

### Tradeoffs

* Temporary monitoring-path failures may reduce measured availability even when the underlying application remained functional.
* Reliability numbers may be more conservative than systems that assume missing data is healthy.
* Threshold selection must balance delayed telemetry against actual disconnection detection.

These tradeoffs are accepted because overstating availability is more damaging than explicitly representing uncertainty or loss of observability.

## Alternatives Considered

### Treat missing telemetry as healthy

Rejected because absence of evidence is not evidence of availability.

### Ignore missing intervals entirely

Rejected because removing unknown periods from the denominator can also inflate reliability and hide monitoring outages.

### Immediately mark unavailable after one missed heartbeat

Rejected because transient scheduling, network, or processing delays can occur. A bounded offline threshold provides tolerance without creating unlimited optimistic uptime.

### Estimate health from the previous known state indefinitely

Rejected because the confidence in a previous observation decreases as time passes.

## Operational Invariant

> **OpsForge must not award healthy availability for time periods in which it has lost sufficient evidence that the monitored system remains reachable.**

Reliability reporting should remain conservative, explainable, and based on observed operational evidence.
