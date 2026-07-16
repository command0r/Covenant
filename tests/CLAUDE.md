# tests — testing strategy

Scope: how Covenant is tested. The governance layer is the product; test it hardest.

## Priority (highest first)
1. **Governance unit tests** — policy decisions, classification routing, attribution math, budget enforcement. Deterministic, no live model calls (mock adapters at the contract boundary).
2. **Fail-closed tests (mandatory)** — no policy match → deny; budget exceeded → block/fallback per config; PII/PHI → never routed to a public provider; misconfig → refuse to start.
3. **Adapter contract tests** — canonical ↔ provider round-trips, SSE streaming, error mapping, usage accounting, per provider.
4. **Audit tests** — every request yields a complete, ordered, tamper-evident entry; tampering is detectable.
5. **Pipeline integration** — end-to-end happy path plus the full P0 demo flow.

## Rules
- Governance, policy, and audit paths: high coverage, no exceptions.
- Every fail-closed behaviour has an explicit negative test.
- Tests never call live providers; adapters are mocked at the contract boundary.
- A bug fix lands with the regression test that catches it.
