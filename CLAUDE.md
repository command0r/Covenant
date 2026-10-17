# Covenant — project root

Covenant is a **regulated AI governance & FinOps control plane** for LLM inference: governed routing, real-time cost attribution, tamper-evident audit, and audit-ready compliance evidence — deployed **inside** a regulated enterprise's perimeter (BFSI first).

This file is always loaded. Area-specific rules live in nested `CLAUDE.md` files (see Map). Keep this file thin; put detail in the area files.

## Prime directives (never violate)
1. **We are not building a gateway.** Implement the smallest viable canonical proxy core; spend effort on governance, FinOps, and evidence — that is the moat. Don't chase provider breadth (OpenAI, Anthropic, Ollama/OpenAI-compatible only). Governed routing, approved fallback, cost attribution and request inspection ARE in scope (ADR-0013) — they are governance, not gateway breadth.
2. **Governance is pipeline middleware, never a bolt-on.** Policy, attribution, audit, and budgets are ordered stages in the request pipeline.
3. **Fail-closed.** No policy match → deny. Misconfig → refuse to start. Never default to an unapproved model.
4. **The perimeter is sacred.** No prompt, response, or metadata leaves the customer boundary. No phone-home. Egress only to the customer-approved model allow-list.
5. **Provenance is product.** We sell clean lineage, so our own lineage is spotless: borrow *patterns* from open source, not code. Any reused code is attributed (Apache-2.0 NOTICE) and logged — see `docs/`. No un-provenanced snippets.
6. **Secrets never touch code, logs, or images.** Config via env/files; secrets via the customer's vault.

## Stack (fixed)
.NET / C#. NativeAOT, single self-contained artifact, one deployable appliance. Async/streaming throughout.

## Map (path-based rules)
- `src/CLAUDE.md` — code development: canonical core, adapter contract, pipeline patterns, C# conventions.
- `docs/CLAUDE.md` — architecture & decision discipline: ADRs, invariants, provenance, doc style.
- `tests/CLAUDE.md` — testing strategy: test the governance moat hardest; fail-closed coverage.
- `deploy/CLAUDE.md` — deployment & appliance: single artifact, in-perimeter, immutable audit store.

## Engineering rules (apply everywhere — that's why they live here, not in an area file)
- Conventional Commits; small, reviewable changes.
- A significant or hard-to-reverse decision gets an ADR (`docs/adr/`) **before** code.
- Tests ship with the feature, not after.
- Ask before anything irreversible (data deletion, history rewrite, adding a copyleft-licensed dependency).
- Prefer the standard library and minimal dependencies; every dependency is audited (license + provenance).

## Status / source of truth
Product spec: `docs/PRD-regulated-ai-governance-finops-v0.1.md` (not yet written). Current iteration: `docs/CONSOLE-V2-IMPLEMENTATION-SPEC.md`.
Current phase: P0 built (governed proxy, classification routing, attribution, budgets/pause, tamper-evident audit, evidence export, embedded console). Now: console v2 — editable versioned configuration (ADR-0011), console inference as authorized test applications (ADR-0012), fallback + real traces (ADR-0013), budget reservations (ADR-0014).
