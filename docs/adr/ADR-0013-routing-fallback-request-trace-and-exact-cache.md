# ADR-0013 — Policies with approved fallback, real request trace, exact cache isolation

**Status:** Accepted
**Date:** 2026-10-16
**Supersedes in part:** ADR-0001 single-route provider call; ADR-0010 fixed local-only routing.
**Spec:** `docs/CONSOLE-V2-IMPLEMENTATION-SPEC.md` §3, §4.3, §4.4, §4.6.

## Decision — policies
A **policy**: `id`, `name`, `enabled`, `paused`, applicability (`classifications[]`, optional
`profiles[]`), ordered `targets[]` (each `{connectionId, modelId, paused}`), `strategy`
(`fixed` = primary then ordered fallback; `length-heuristic` = targets[0] when the chars/4 estimate ≤
`thresholdTokens`, else the last target, remaining targets as ordered fallback — named honestly, not
"quality-aware"), `retry` (`maxAttempts` ≤ 3, `deadlineMs`, `on` ⊆ {`connect`,`timeout`,`429`,`5xx`}),
`allowSensitiveCloud` (default false). **Precedence:** a policy naming the request's profile beats one
that names only classifications; two enabled policies at the same specificity covering the same
(classification, profile) pair are a **validation error** (no arbitrary winner). No match ⇒ refusal.
Paused policy ⇒ refuses its matching traffic. Paused target ⇒ removed from eligibility; the next approved
target may serve. Eligibility also requires: connection enabled; model permitted on the connection;
mode allows the boundary (local-only ⇒ cloud targets never eligible); PII/PHI ⇒ cloud targets ineligible
unless the policy sets `allowSensitiveCloud` (validation warns loudly; demo seeds never set it); priced for
paid targets under a hard budget. A model override must name an eligible target and pins it (no fallback).
**Modes:** `local-only` and `hybrid` (and `legacy-cloud` for imported pre-v2 configurations).

## Decision — fallback
The provider stage iterates eligible candidates. Before **each** attempt: re-check global pause and reserve
budget for that target (ADR-0014). Retry the next candidate only for the policy's configured transient
failures (connection failure, provider-side timeout, 429, 500/502/503/504), never for policy denial,
client cancellation, pause or other 4xx, and **never after any content was emitted** (partial output and
terminal failure are recorded). Total attempts ≤ `maxAttempts`, total time ≤ `deadlineMs`. Each attempt
is recorded (`attemptId`, target, outcome, error class, duration, usage/charge basis) and possibly-billed
failed attempts are charged an estimate (ADR-0009). Failure classification of SDK exceptions lives in the
Host (it knows the SDK types) behind `IProviderFailureClassifier`.

## Decision — trace
Canonical events `{requestId, configVersion, seq, stage, attemptId?, state, tsUtc, durationMs?, reasonCode?}`,
`state` ∈ pending|running|passed|refused|failed|cancelled|skipped, emitted by the pipeline itself
(Stopwatch-measured, server time). Stages not entered after a refusal/cache hit are emitted as `skipped`
with `reasonCode` (`not_reached`, `cache_hit`). No content, credentials or free-text provider errors (only
bounded codes). `TraceHub` (in-memory) creates the per-request buffer **before** the pipeline starts, so
early events are retained: ≤ 64 events per request, ≤ 2 000 requests, 10-minute TTL. APIs:
`GET /admin/requests/{id}/trace` (snapshot) and `/trace/stream` (SSE replay-then-live; `Last-Event-ID`
resume; clients de-duplicate by `seq`; `410` after expiry). The audit record (v3) persists `ConfigVersion`,
`Attempts[]` and a compact `Trace[]` (stage:state:ms) — the durable summary.

## Decision — exact cache
Key = SHA-256 over: application id, team, profile, **final** classification, config version, connection
id + model id, max output tokens, stream-independent request parameters, and the message content.
Any configuration activation changes the key space (no reuse across versions). Denials, failures and
partial streams are never stored. A hit runs after auth/shape/classify/policy (current checks), skips
rate-limit/pause/budget/provider (unchanged semantics: **a global provider-call pause does not stop cache
hits** — the UI says so), costs $0 provider charge with `CostBasis = no_call`, and is audited with
`CacheHit = true` and the original served model.

## Consequences
- (+) Fallback is bounded, explainable, evidenced, and cannot route sensitive data to cloud by accident.
- (+) The console shows real stage execution, not a configured-order animation.
- (−) Every config activation cold-starts the cache.
