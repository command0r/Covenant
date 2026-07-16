# src — code development

Scope: rules for writing Covenant's code. Read with the root `CLAUDE.md`.

## Project layout (arrows = allowed dependency direction)
- `Covenant.Core` — canonical request/response model, the pipeline, stage contracts. Depends on nothing.
- `Covenant.Adapters` — provider adapters (canonical ↔ provider). → Core.
- `Covenant.Governance` — policy engine, classification routing, attribution, budgets, audit, evidence. → Core.
- `Covenant.Host` — the appliance: HTTP API (OpenAI/Anthropic-compatible), composition root, config. → all.

Governance and Adapters never depend on each other; they meet only through Core's pipeline.

## The three patterns that define this codebase
1. **Canonical schema at the center.** Everything normalizes to one internal model. Provider-specific shapes exist *only* inside adapters. If a provider concept leaks past an adapter, that's a bug.
2. **Adapter contract.** Each provider implements one interface: canonical→request, response→canonical, SSE stream normalization, error mapping, token/usage accounting. Adding a provider is one adapter and nothing else. Start set: OpenAI, Anthropic, one self-hosted OpenAI-compatible (vLLM) target.
3. **Pipeline = ordered middleware.** `auth → classify → policy → cache-lookup → route → budget/rate → provider-call → post-process → attribute → audit`. Governance features are *stages*, not special cases.

## Ordering rules (non-obvious — do not reorder)
- **Cache before route** — a hit skips the model call entirely.
- **Policy before anything that costs money.**
- **Telemetry and audit emit off the hot path** — never block the response on logging.

## C# conventions
- `Nullable` + warnings-as-errors on. No `async void`. Stream with `IAsyncEnumerable<T>`.
- NativeAOT-safe: source-generated `System.Text.Json` (no reflection-based serialization), no runtime code-gen, no unsupported reflection.
- Hot path: minimal allocation, no blocking calls.

## Don't
- Don't add providers for breadth — only what a regulated buyer's allow-list needs.
- Don't put governance logic in adapters, or provider logic in governance.
- Don't reach for a framework where a small amount of explicit code will do.
