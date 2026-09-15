# ADR-0001: Separate Remediation Preview, Execution, and Recovery Verification

* **Status:** Accepted
* **Date:** 2026-09-15
* **Decision owners:** OpsForge
* **Scope:** Incident remediation and recovery workflow

## Context

OpsForge can identify operational failures, correlate related symptoms into incidents, and offer constrained remediation actions to an operator.

A common automation failure mode is to treat successful command execution as equivalent to successful service recovery.

For example, a process restart command may return successfully while:

* the process immediately fails again;
* the service starts but remains unable to serve requests;
* a dependent TCP endpoint remains unavailable;
* an HTTP health check continues to fail; or
* a downstream dependency prevents actual customer-facing recovery.

Command success therefore provides evidence that an action was executed, but it does not prove that the original operational condition has been resolved.

OpsForge also needs to keep human control at the point where diagnostic information becomes a production-changing action.

## Decision

OpsForge separates remediation into three explicit stages:

```text
Preview
   ↓
Operator approval
   ↓
Execute constrained remediation
   ↓
Wait for subsequent telemetry
   ↓
Verify recovery
```

### Preview

Before execution, OpsForge presents the proposed remediation so the operator can review the intended action and target.

Previewing does not alter the monitored system.

### Execute

After explicit operator approval, OpsForge sends only a supported constrained command through the agent command channel.

Successful command execution records that the requested operation completed from the command channel's perspective.

It does **not** mark the incident as recovered.

### Verify

Recovery is established only from later monitoring evidence.

The same telemetry and probe mechanisms responsible for detecting the failure are used to determine whether the affected system returned to a healthy state.

For example, if a failed process caused dependent TCP and HTTP failures, recovery requires subsequent evidence showing that the relevant monitored conditions have recovered.

## Rationale

This model intentionally distinguishes:

* **intent** — what the operator wants to change;
* **execution** — whether OpsForge successfully issued and completed the action; and
* **outcome** — whether monitoring evidence demonstrates actual recovery.

Keeping those states separate reduces false recovery declarations and preserves a clearer operational audit trail.

It also creates a safer boundary for future automation because remediation logic cannot assume that an API call, process exit code, or command acknowledgement proves service health.

## Consequences

### Positive

* Incidents cannot be closed merely because a remediation command returned successfully.
* Recovery is tied to observable system state.
* Operators can inspect an action before allowing it to change the environment.
* Command execution and recovery can be audited independently.
* Failed or incomplete remediation remains visible instead of being converted into a false healthy state.
* Future automated remediation can reuse the same verification boundary.

### Tradeoffs

* Recovery is not instantaneous after command execution.
* OpsForge must wait for new telemetry before declaring success.
* Monitoring cadence affects how quickly verified recovery can be established.
* A remediation may succeed operationally while verification remains pending because telemetry has not yet arrived.

These are accepted tradeoffs because OpsForge prioritizes evidence-backed recovery over optimistic state transitions.

## Alternatives Considered

### Mark healthy when the command succeeds

Rejected because command success does not prove that the monitored service or dependency recovered.

### Automatically execute remediation immediately after correlation

Rejected as the default model because diagnosis confidence and remediation safety are separate concerns. Operator review provides an explicit control boundary.

### Use a fixed delay and then assume recovery

Rejected because elapsed time is not evidence of system health.

## Operational Invariant

> **A successful remediation command is not equivalent to a recovered incident. Recovery requires subsequent monitoring evidence.**

This invariant should remain true even if OpsForge later introduces additional remediation types or more automated operational workflows.
