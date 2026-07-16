# docs — architecture & decision discipline

Scope: architecture records, ADRs, provenance, and doc conventions.

## Sources of truth
- **Product:** `PRD-regulated-ai-governance-finops-v0.1.md`.
- **Architecture:** ADRs in `docs/adr/`. Accepted ADRs are authoritative over prose anywhere else.

## ADRs
- Write one for any significant or hard-to-reverse decision — engine/runtime choice, single- vs multi-tenant control plane, audit-store mechanism, framework mappings, pricing-affecting architecture.
- Format: **Context · Decision · Status · Consequences · Alternatives considered.** One decision per ADR. Name them `ADR-NNNN-short-title.md`.
- Write the ADR **before** the code it governs.

## Architecture invariants (don't relitigate without an ADR)
- Smallest-viable canonical core; governance as middleware; engine *patterns*, not engine *code*.
- In-perimeter, fail-closed, single deployable.

## Provenance — `docs/PROVENANCE.md`
- Patterns studied from open source (LiteLLM, Bifrost, Portkey, Kong) are credited as *influences*; this is allowed and expected.
- Any actually-reused code is logged here with source, license, and the NOTICE attribution it carries. **If it isn't in PROVENANCE.md, it shouldn't be in the repo.**

## Doc style
Sentence-case headers. Precise and short — no bloat. Diagrams as inline Mermaid. State assumptions and open questions explicitly.
