# ADR-0002: Use Deterministic Correlation to Collapse Related Symptoms into Primary Incidents

* **Status:** Accepted
* **Date:** 2026-09-15
* **Decision owners:** OpsForge
* **Scope:** Incident detection, correlation, and blast-radius analysis

## Context

A single infrastructure or application failure can produce multiple monitoring symptoms.

For example:

```text
Process failure
    ↓
TCP listener unavailable
    ↓
HTTP health check fails
```

Treating each failed signal as an independent incident creates alert amplification.

Operators then see several incidents that may all describe the same underlying failure. This increases cognitive load, distorts incident counts, complicates MTTR measurements, and makes remediation less clear.

OpsForge also models dependency relationships between monitored components. That topology provides evidence that some failed signals may be consequences of another failure rather than independent root causes.

## Decision

OpsForge uses deterministic correlation rules to combine related operational symptoms into a **Primary Incident**.

Correlation considers evidence such as:

* monitored component relationships;
* dependency topology;
* timing of related failures;
* upstream versus downstream component state;
* known process, TCP, HTTP, DNS, and service relationships;
* existing active incidents;
* later telemetry that confirms or weakens the original hypothesis.

Related downstream symptoms may be classified as derivative signals rather than creating additional independent incidents.

The correlation engine also records probable root-cause information and confidence rather than presenting correlation as absolute certainty.

## Example

A monitored application may have the dependency chain:

```text
Application process
        ↓
TCP listener
        ↓
HTTP health endpoint
```

If the process disappears and the dependent TCP and HTTP checks subsequently fail, OpsForge can represent the event as:

```text
Primary Incident
Root cause candidate: Application process unavailable

Supporting symptoms:
- Process missing
- TCP listener unavailable
- HTTP health check failed
```

rather than:

```text
Incident 1: Process failed
Incident 2: TCP failed
Incident 3: HTTP failed
```

## Rationale

The objective is not simply to reduce alert volume.

The objective is to preserve the distinction between:

* the likely initiating failure;
* downstream symptoms;
* affected dependencies;
* operator-visible blast radius; and
* uncertainty in the diagnosis.

Deterministic reasoning is used so the same evidence produces the same correlation result.

This makes the behavior easier to test, audit, reproduce, and explain than an opaque correlation mechanism.

## Derivative-Signal Suppression

A derivative signal is still operational evidence.

OpsForge therefore does not discard it.

Instead, the signal remains associated with the Primary Incident while being suppressed from unnecessary independent incident creation.

This preserves diagnostic evidence without inflating the actionable incident queue.

## Reassessment

Incident correlation is not permanently fixed at the instant of detection.

Later telemetry can change the available evidence.

OpsForge can reassess incident state as components recover, additional failures appear, or topology information changes.

Correlation therefore represents the best current interpretation of observed evidence rather than an irreversible declaration of root cause.

## Consequences

### Positive

* Reduces alert amplification.
* Gives operators one actionable incident for one likely failure domain.
* Preserves downstream symptoms as evidence.
* Improves blast-radius visibility.
* Produces more meaningful incident and MTTR metrics.
* Deterministic behavior can be covered by automated tests.
* Correlation results can be explained to an operator.

### Tradeoffs

* Topology information must be sufficiently accurate.
* Correlation rules can miss relationships that have not been modeled.
* Multiple simultaneous failures may produce ambiguous evidence.
* Deterministic rules require explicit maintenance as monitored component types evolve.

These tradeoffs are preferable to silently hiding correlation logic behind an opaque scoring mechanism.

## Alternatives Considered

### Create one incident for every failed check

Rejected because dependent failures can generate excessive duplicate operational work.

### Automatically suppress all downstream failures

Rejected because downstream symptoms contain useful diagnostic and blast-radius evidence.

### Use only temporal proximity

Rejected because two failures occurring near each other do not necessarily share a cause.

### Use opaque probabilistic or ML-based correlation

Not selected for the current design because OpsForge prioritizes reproducibility, explainability, and deterministic testing for this portfolio/lab platform.

Probabilistic techniques could eventually provide advisory evidence without replacing the deterministic incident model.

## Operational Invariant

> **Correlation may reduce the number of actionable incidents, but it must not destroy the underlying evidence that produced the correlation.**

A Primary Incident should remain explainable in terms of the monitored signals and topology relationships that support it.
