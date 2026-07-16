# ADR-0001 — Core engine and provider layer

**Status:** Proposed (awaiting acceptance)
**Date:** 2026-06-26

## Context
Covenant needs a proxy core under its governance pipeline: an OpenAI/Anthropic-compatible ingress, provider calls with SSE streaming, and a canonical request/response model. Root invariants already fix two things — we are not building a mass-market gateway, and we ship a single .NET appliance.

Survey of the current field (June 2026): every strong open-source gateway is foreign-language — LiteLLM (Python, MIT), Bifrost (Go, Apache-2.0), Portkey gateway (Rust, Apache-2.0), Kong and APISIX (Lua), Envoy AI Gateway (Go), agentgateway and LangDB (Rust). None are .NET. Embedding any of them in-process breaks the single-.NET-binary appliance; porting one is a cross-language derivative carrying a permanent maintenance tax on the fastest-churning, least-differentiated layer (provider adapters).

Decision-relevant fact: .NET has a first-party provider abstraction — **Microsoft.Extensions.AI** (`IChatClient`) — that unifies OpenAI, Azure OpenAI, Ollama/self-hosted and others behind one interface. That is the same canonical-provider role we would otherwise hand-build.

## Decision
Build Covenant's core natively in .NET, composed of three parts:
1. **Ingress** — a minimal OpenAI/Anthropic-compatible HTTP endpoint (ASP.NET minimal API). Small, and ours.
2. **Pipeline** — the ordered governance middleware (the moat). Written by us.
3. **Provider layer** — adapters implemented over **Microsoft.Extensions.AI `IChatClient`** rather than hand-rolled or ported. Self-hosted/vLLM targets are reached through the same abstraction via their OpenAI-compatible endpoints.

Do not embed, fork, or port a foreign-language OSS gateway.

Patterns we **study as influences** (logged in `docs/PROVENANCE.md`; not code we copy): LiteLLM (provider quirks, virtual-key/budget model), Bifrost (pipeline performance, air-gap deployment shape, two-layer caching), Portkey (guardrail/PII-redaction stage design), Kong/APISIX (plugin-ordering discipline).

## Consequences
- (+) One .NET codebase, one deployable; governance and provider plumbing share a single process.
- (+) We do not inherit the multi-provider maintenance tax — Microsoft.Extensions.AI absorbs provider churn, and a regulated allow-list needs few connectors anyway.
- (+) Provenance stays clean: influences, not copied code.
- (−) New dependency on Microsoft.Extensions.AI (first-party; pin + audit per rules). Provider coverage is bounded by its connectors — acceptable, and any gap is a custom `IChatClient` implementation.
- (−) **Risk to validate in a spike:** NativeAOT compatibility of Microsoft.Extensions.AI and the connectors we need. If full NativeAOT has gaps, fall back to a self-contained single-file trimmed publish, which still satisfies "one deployable." Resolve before committing AOT in `deploy/`.
- (−) We own the OpenAI/Anthropic-compatible wire surface (request/response/SSE). Small surface, but ours to keep correct.

## Alternatives considered
- **Embed Bifrost (Go) as a sidecar** — rejected: two deployables, breaks the appliance posture.
- **Fork/port Portkey (Rust) to .NET** — rejected: cross-language derivative, permanent upstream-churn tax, plus provenance and direct-competitor concerns.
- **Adopt Bifrost/Go wholesale, drop .NET** — rejected: abandons the founder's deepest stack and the .NET-in-BFSI fit. It is the closest single-binary alternative, noted for the record.
- **Hand-roll every provider adapter in .NET** — rejected: reinvents Microsoft.Extensions.AI and signs up for the exact provider-churn tax we are avoiding.

## Follow-ups
- **Spike:** NativeAOT vs self-contained single-file publish for the required connectors (feeds `deploy/`, may warrant its own ADR).
- **ADR-0002 candidate:** single-tenant vs multi-tenant control plane (already flagged in the PRD).
