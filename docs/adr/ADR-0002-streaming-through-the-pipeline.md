# ADR-0002 — Streaming through the governance pipeline

**Status:** Accepted
**Date:** 2026-07-16

## Context
The stack mandates async/streaming throughout (root `CLAUDE.md`), and ADR-0001 makes the
OpenAI-compatible wire surface — including SSE — ours to keep correct. The question is not whether to
stream but **where the stream is consumed**, because that placement decides whether three invariants
survive: audit wraps every request, attribution runs on completion with real usage, and budget spend
is recorded from attributed cost on the pipeline unwind.

## Decision
**Streaming is an emission mode of the provider-call stage, not a different pipeline.**

- The canonical request carries a `Stream` flag; the context exposes an optional **delta sink**
  (`Func<ChatDelta, CancellationToken, ValueTask>`) that the Host installs before execution.
- `ProviderCallStage` consumes the provider's stream *inside* the pipeline: it forwards each canonical
  `ChatDelta` to the sink as it arrives, accumulates the full text and the terminal usage
  (`UsageContent` on the final update), and only then sets `ctx.Response` and calls `next`. The
  pipeline unwind — attribution pricing, budget ledger recording, audit entry — is therefore identical
  for streamed and buffered requests.
- Pre-flight denials (classify/policy/budget/kill switch) occur before the sink is ever invoked, so
  the Host still returns a plain 403/502 JSON error — no SSE bytes are committed.
- A mid-stream provider failure becomes the same governed denial as ADR-0001's error mapping
  (`DenialKind.UpstreamFailure`); the Host, having already committed a 200, terminates the SSE stream
  with an error event followed by `[DONE]`. The audit entry records the denial either way.

## Consequences
- (+) One pipeline, one ordering, one set of governance tests covers both modes; streaming cannot
  drift from governance because there is no second code path.
- (+) Backpressure is natural: the provider loop awaits the sink, which awaits the client socket.
- (−) The audit entry for a streamed request is written at stream end, not stream start — a very
  long stream is invisible to evidence until it finishes. Acceptable for this slice; a "stream
  started" event pairs naturally with the durable audit-store ADR if needed.
- (−) Once bytes are committed, a mid-stream failure cannot change the HTTP status; the error event
  convention covers it. This is inherent to SSE, not to this design.
- (−) Usage accuracy depends on the provider emitting usage on the final chunk (OpenAI:
  `stream_options.include_usage`, which Microsoft.Extensions.AI's adapter handles). Absent usage
  accounts as zero tokens — visible in evidence, never invented.

## Alternatives considered
- **Return `IAsyncEnumerable` through the pipeline (deferred consumption).** Rejected: stages
  complete before the stream is consumed, so audit's `finally` fires with no outcome and attribution
  never sees usage — it breaks audit-wraps-everything, silently.
- **A separate streaming pipeline.** Rejected: two governance code paths that must be kept identical
  by discipline instead of by construction. That is how bolt-on governance starts.
- **Buffer fully, then fake-stream to the client.** Rejected: destroys time-to-first-token, the only
  reason streaming exists.
