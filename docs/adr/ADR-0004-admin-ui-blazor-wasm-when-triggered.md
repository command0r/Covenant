# ADR-0004 — Admin UI: embedded vanilla page now, standalone Blazor WebAssembly when triggered

**Status:** Accepted (2026-07-16)
**Date:** 2026-07-16

## Context
The appliance ships an admin dashboard (FinOps, budgets, kill switch, routing view) as a single
embedded vanilla HTML page — zero dependencies, AOT-safe, no egress. A richer UI is anticipated:
policy editing, an evidence browser, runtime-editable budgets. The framework choice is constrained by
invariants, not preference: single NativeAOT-capable binary, no CDN or external assets, no second
toolchain entering the provenance story. As of .NET 10, minimal APIs, gRPC, and workers are
NativeAOT-compatible; **MVC, Razor Pages, and server-hosted Blazor are not**.

## Decision
Two-stage, with explicit triggers:

1. **Now:** keep the embedded vanilla page. It covers read-only FinOps + the kill-switch toggle at
   zero dependency cost.
2. **When triggered:** move to **standalone Blazor WebAssembly**, compiled to static assets and
   embedded in the Host binary. The Host remains a pure minimal-API NativeAOT application that also
   serves the WASM bundle. .NET end-to-end; no Node/npm toolchain.

**Triggers** (any one): a policy or budget *editor* (mutating config through the UI), an evidence
browser with client-side filtering/pagination, or the vanilla page exceeding roughly a thousand lines
of hand-written JS — whichever arrives first.

**Rejected regardless of trigger:** Blazor Server and Blazor Web App server-hosted render modes —
they are not NativeAOT-compatible and would permanently force the Host off the AOT path, and
long-lived SignalR circuits fit an appliance poorly.

## Consequences
- (+) The Host's AOT path is never hostage to the UI; the UI upgrade is additive, not architectural.
- (+) One language and IDE across the stack; dependency audit stays within the .NET/NuGet world.
- (−) Blazor WASM adds a multi-megabyte bundle to the binary — acceptable for an in-perimeter
  appliance, irrelevant to request latency.
- (−) Until a trigger fires, contributors extend hand-written JS; the trigger list caps how far that
  is allowed to grow.

## Alternatives considered
- **Blazor Server / server-hosted Blazor Web App.** Rejected: breaks NativeAOT (ADR-0001's deploy
  goal), SignalR circuit state on an appliance.
- **React/Svelte/Vue SPA.** Rejected: second toolchain and dependency ecosystem to audit and
  provenance-track for marginal gain over Blazor WASM in a .NET shop.
- **Grow the vanilla page indefinitely.** Rejected: fine for dashboards, hostile to editors and
  browsers with real state; the trigger list exists to stop this before it rots.
