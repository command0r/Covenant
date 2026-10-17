# ADR-0009 — Request correlation, audit record v2, and honest usage accounting

**Status:** Accepted
**Date:** 2026-10-16

## Context
The console must open the *exact* evidence record for a request it just sent, show what actually
happened (which stage stopped it, whether classification ran, which model was selected / attempted /
served), and never present unknown usage as a real $0. Before this ADR: the audit stage minted its own
id unrelated to any response id; the provider adapter replaced missing token counts with 0; a cancelled
request could lose its audit record (the sink write used the cancelled token); and the audit record
had no structured outcome beyond Effect + a reason string.

## Decision
1. **One correlation id.** Host middleware creates `req_<32 hex>` for every `/v1/*` request *before*
   body binding, sets `X-Covenant-Request-Id`, and passes it into the canonical request. It is the
   buffered OpenAI `id`, the Anthropic message `id`, the SSE chunk id, `request_id` on error bodies, and
   the audit record `Id` (duplicated in `CorrelationId` for explicitness). Caller-supplied ids are never
   used. Requests rejected by the JSON binder never reach the pipeline: they carry the header but have no
   audit record — the console states this instead of searching.
2. **Audit record v2 — additive, hash-compatible.** New nullable fields appended to `AuditEntry`:
   `SchemaVersion=2, CorrelationId, ClassificationEvaluated, DenialKind, StoppedAtStage, SelectedModel,
   AttemptedModel, Stream, UsageReported, CostBasis`. The chain hashes the *stored* line bytes and the
   verifier re-hashes stored content, so legacy lines verify unchanged and are never rewritten; on
   deserialize missing fields are null and the console renders **Not recorded**. Facts are observed, not
   inferred: the pipeline sets `CurrentStage` on entry to each stage and `Deny` captures it (the budget
   stage names the finer "pause" for the kill switch); `AttemptedModel` is set immediately before a
   provider call starts; `ClassificationEvaluated` only by the classify stage. The restrictive default
   classification is unchanged for enforcement. No measured per-stage timings yet.
3. **Cancellation is evidence.** The audit stage records a cancelled request as `DenialKind=Cancelled`
   and writes with `CancellationToken.None`, so the client going away can never suppress the record.
   HTTP 499 if anything is still listening.
4. **Usage honesty.** `InferenceResponse.UsageReported` is false when the provider omitted either token
   count. `CostBasis` ∈ `reported | zero_priced | estimated_from_text | estimated_failed_call |
   unknown_failed_call | unpriced | no_call`.
5. **Fail-closed accounting.** (a) Startup refuses any routable model without a price (0 is an explicit,
   valid price for in-perimeter compute — "provider charge $0", infrastructure excluded). (b) A served
   call on a priced model with no reported usage is charged from a chars/4 estimate of prompt + output
   (`estimated_from_text`); token counts stay "not reported". (c) A call that was *started* but produced no
   response (upstream failure, mid-stream failure, cancellation) is charged an estimate of input + any
   streamed output (`estimated_failed_call`) — never a silent zero. (d) Savings aggregates exclude
   estimated/unknown records and expose their counts. The budget remains a **pre-call threshold** check:
   concurrent requests admitted under the cap can overshoot it by their own cost; it is not an invoice
   ceiling.

## Consequences
- (+) Exact evidence lookup by id (`GET /admin/requests/{id}`), robust to identical prompts, equal
  timestamps and concurrency; no timestamp joins anywhere.
- (+) Unknown cost cannot become a false $0 or a false saving; ledger replay reproduces estimated charges
  because they are recorded in `Usage.CostUsd` of the audit line.
- (−) Lookups re-read the current segment (bounded to it; archived segments are out of scope).
- (−) Estimates can over- or under-charge relative to the provider invoice; they are labelled estimates.

## Alternatives considered
- Rewrite legacy lines into v2 — rejected: rewriting history is exactly what the chain forbids.
- Nullable token fields inside `Usage` — rejected: changes the nested shape every consumer and the
  ledger replay read; an entry-level flag is additive.
- Charge nothing for failed calls — rejected: providers bill input tokens on many failures; fail-closed.
