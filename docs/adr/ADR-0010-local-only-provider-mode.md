# ADR-0010 — Local-only provider mode and the governed local route

**Status:** Accepted
**Date:** 2026-10-16

## Context
The demo and many regulated deployments run a single in-perimeter model (Ollama serving one exact Qwen
tag) with no cloud credential. Startup required `OpenAI:ApiKey`; the local model id defaulted to a
hard-coded `llama-3.1-8b-instruct`; and the console needs to probe/discover an Ollama endpoint without
becoming an arbitrary server-side URL fetcher.

## Decision
- `Providers:Mode` = `cloud` (default, unchanged behaviour: cloud key required; Public/Internal on cloud
  routes; PII/PHI on the local route) or `local-only`.
- **local-only** requires `Local:Endpoint` (absolute http/https) and `Local:ModelId` (exact tag); it
  registers **no** cloud provider and **refuses to start** if `OpenAI:ApiKey` or `Anthropic:ApiKey` is
  configured — no silent fallback, no quiet rerouting. Every classification routes to `local:<tag>`: an
  explicit mode choice recorded in status/catalog, not a model dropdown.
- In both modes the local model id is never guessed: `Local:Endpoint` without `Local:ModelId` refuses to
  start; with no local config the route reads `local-model-not-configured` and fails closed at the
  provider stage.
- Probing (`POST /admin/providers/probe`, admin-only): destinations must be on
  `Providers:ProbeAllowList` (host:port list) or be the applied `Local:Endpoint` authority; http/https;
  no userinfo/query/fragment; redirects not followed; connection only to the validated host; 5 s
  timeout; 64 KiB response cap; no Covenant credential forwarded. `localhost` means Covenant's host.
- Applying configuration remains manual (config + restart). The console reads back the applied
  configuration (`GET /admin/catalog`); draft probe results never alter applied state. Observed health
  comes from real pipeline calls and probes of the applied endpoint; a later failure supersedes an
  earlier success while preserving the last-success time. A paused (refused) request records no health.

## Consequences
- (+) A no-cloud appliance starts and serves the local model through the full governed pipeline.
- (+) The probe cannot be aimed at arbitrary hosts by a console user.
- (−) One local model only; multiple local models need a registry extension (later ADR).
- (−) Runtime apply is deferred; the wizard ends with configuration guidance and a restart.
