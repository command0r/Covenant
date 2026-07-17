# ADR-0003 — Observability is an optional OpenTelemetry export, not an embedded platform

**Status:** Accepted (2026-07-16)
**Date:** 2026-07-16

## Context
Deep per-request traces are valuable for debugging, demos, and measuring governance overhead (RQ4).
Platforms like Langfuse provide them — but self-hosted Langfuse v3 is a six-component stack (web,
worker, Postgres, ClickHouse, Redis, blob store), which collides with the single-deployable appliance
posture, and its mutable trace store is not audit-grade evidence, so adopting it for attribution or
audit would weaken RQ2 rather than strengthen it. Langfuse (and Grafana, Datadog, and most peers)
ingest OpenTelemetry over OTLP/HTTP, and .NET has first-party OTel support.

## Decision
Covenant emits **OpenTelemetry traces from the pipeline** — one span per request, child spans per
stage, attributes for classification, policy effect, route, usage, and cost. Export is:

- **Off by default.** No config → no telemetry, no background exporters.
- **In-perimeter only.** The OTLP endpoint is customer-configured and subject to the same egress
  posture as model endpoints; the appliance never phones home (root CLAUDE.md #4).
- **Never load-bearing for governance.** Spans are diagnostics. The audit chain remains the only
  evidence of record; no governance claim may cite telemetry. Prompt/response content never enters
  span attributes — metadata only.

No observability platform is embedded, bundled, or depended on. Langfuse et al. become compatible
backends a customer may point the exporter at, not components of Covenant.

## Consequences
- (+) Appliance stays one binary; customers reuse whatever observability stack they already trust.
- (+) Span timings give RQ4 (overhead of the governance layer) a measurable, third-party-verifiable basis.
- (+) Positioning: observability tools record what happened; Covenant decides what is allowed to
  happen and proves it — complementary, not competing.
- (−) One new dependency family (`OpenTelemetry.*`, MIT) to pin, audit, and verify for NativeAOT
  compatibility before merging.
- (−) A second emission path from the pipeline (audit + telemetry) that must never diverge in meaning;
  span attributes are derived from the same context the audit stage reads.

## Alternatives considered
- **Embed/bundle Langfuse.** Rejected: multi-component deployment breaks the appliance; mutable trace
  store adds no evidentiary value; large security-review surface imported into regulated perimeters.
- **Build our own trace viewer into the dashboard.** Rejected: reinvents a commodity; the moat is
  enforcement and evidence, not observability UX.
- **No telemetry at all.** Rejected: forfeits the cheapest credible measurement of RQ4 and makes
  live debugging of the pipeline needlessly opaque.
