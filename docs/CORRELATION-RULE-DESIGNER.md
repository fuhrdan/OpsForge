# OpsForge v1.4 — Correlation Rule Designer

OpsForge v1.4 adds a controlled configuration workflow around the existing deterministic correlation engine.

**draft → validate → dry-run → review → activate → history → restore/export**

Drafts never change live correlation until an authenticated Administrator explicitly activates them.

## Validation

The validator rejects empty/duplicate IDs, IDs containing `:`, missing titles, invalid windows, insufficient failure thresholds, empty/duplicate selectors, and selectors shared across multiple rules.

## Dry-run

`POST /api/correlation-rules/preview` evaluates draft rules against an enrolled agent's latest heartbeat and returns completeness, failure count, and the exact Primary Incident candidate that would be produced. Live rules are not changed.

## Activation and persistence

`POST /api/correlation-rules/import` validates the full set, replaces the live in-memory rules, writes `data/correlation-rules.active.json`, and appends a version snapshot to `data/correlation-rule-history.jsonl`.

If a persisted override is missing or invalid at startup, OpsForge falls back to packaged `correlation-rules.json`.

## History / restore / export

- `GET /api/correlation-rules/history`
- `GET /api/correlation-rules/export`
- `POST /api/correlation-rules/restore/{versionId}`

Restore creates a new active history entry rather than rewriting old history. Exports include a SHA-256 fingerprint.

## Designer page

`/correlation-rules.html`

The page uses the same authenticated API surface; there is no bypass configuration path.
