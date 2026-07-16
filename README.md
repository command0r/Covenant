# Covenant

An in-perimeter **AI inference governance and FinOps control plane** for regulated environments (BFSI first). Every LLM request passes through one governed pipeline: classified, policy-routed, budget-checked, cost-attributed, and audited — including the requests that get refused.

Research artifact: can inference governance be an architecturally separable control layer — enforceably, evidentially, and at acceptable cost?

## How a request flows

```mermaid
flowchart LR
    classDef caller fill:#e3f2fd,stroke:#1565c0,color:#0d47a1
    classDef govern fill:#fff3e0,stroke:#ef6c00,color:#e65100
    classDef provider fill:#e8f5e9,stroke:#2e7d32,color:#1b5e20
    classDef evidence fill:#f3e5f5,stroke:#6a1b9a,color:#4a148c
    classDef denied fill:#ffebee,stroke:#c62828,color:#b71c1c

    Client["Caller<br/>(OpenAI-compatible request)"]:::caller --> Ingress["Ingress"]:::caller
    Ingress --> Classify

    subgraph Pipeline["Governance pipeline — every request, in order"]
        direction LR
        Classify["Classify<br/>data sensitivity"]:::govern --> Policy{"Policy<br/>route permitted?"}:::govern
        Policy -->|allow| Budget{"Budget<br/>within cap? switch live?"}:::govern
        Budget -->|ok| Provider["Provider call<br/>allow-listed model only"]:::provider
        Provider --> Attribute["Attribute cost<br/>team · workflow · use case"]:::evidence
    end

    Policy -->|"no permitted route"| Deny["403 — denied"]:::denied
    Budget -->|"exceeded / kill switch"| Deny
    Attribute --> Response["Response + usage"]:::caller
    Response --> Audit["Tamper-evident audit<br/>hash-chained · allow, deny, error alike"]:::evidence
    Deny --> Audit
```

**Color key:** 🔵 caller surface · 🟠 governance decisions · 🟢 model call · 🟣 evidence (attribution & audit) · 🔴 refusal

## Working principles

1. **Fail-closed.** No policy match → deny. Unclassified defaults to the most restrictive class. Misconfiguration → the appliance refuses to start. PII/PHI never reaches a public provider because no public route exists for those classifications.
2. **Governance is the pipeline, not a bolt-on.** Every capability — classification, policy, budgets, attribution, audit — is an ordered stage in one middleware chain. Nothing bypasses it.
3. **Everything is evidence.** Each request, allowed or denied, appends exactly one hash-chained audit entry. Altering any past entry breaks every hash after it.
4. **The perimeter is sacred.** Deployed inside the customer boundary; egress only to the approved model allow-list; no phone-home.

## Where the detail lives

Architecture decisions: `docs/adr/` · code lineage: `docs/PROVENANCE.md` · area rules: `CLAUDE.md` files per directory · first-run instructions: `README-SCAFFOLD.md` (deleted once the build is stable).
