# ADR-0006 — Evidence graph: optional Neo4j projection of the audit log

**Status:** Accepted (2026-07-19)
**Date:** 2026-07-19

## Context
The audit log is relationally rich — principals belong to teams; requests are made by principals,
classified, routed to models, denied for reasons, priced against budgets — but flat log lines make
the questions auditors and security teams actually ask (who touched PHI, which teams' spend reaches
which providers, what happened during an incident window, which credential produces denials)
expensive to answer. A property graph answers them in one hop. Separately, GraphRAG-style
natural-language querying over that graph is a credible future layer — but retrieval-augmented
generation as a *proxy feature* is application-layer scope creep and is rejected here.

Covenant has an established pattern for exactly this shape of need: **optional export, never
load-bearing** (ADR-0003 for telemetry; the spend ledger as a projection of the audit log).

## Decision
An **evidence graph projector**: an optional background service in the Host that tails the verified
audit log and idempotently MERGEs it into a customer-operated, in-perimeter Neo4j instance.

- **Off by default.** No `Neo4j:Uri` config → the service is never registered, no driver connection
  exists.
- **Never load-bearing.** The hash-chained log remains the sole evidence of record; no governance
  claim may cite the graph. If Neo4j is down, requests flow and evidence accrues; the projector
  catches up when it returns. Projection is derived data, rebuildable from the log at any time.
- **In-perimeter only.** Neo4j runs inside the customer boundary (compose stack in `deploy/neo4j/`);
  the endpoint falls under the same egress discipline as everything else.
- **Metadata only.** The graph carries what the audit entry carries — including the prompt SHA-256
  fingerprint and, only if the operator opted in (Audit:PromptPreviewChars), the truncated preview.

Graph model (first slice):
`(:Principal)-[:MEMBER_OF]->(:Team)`, `(:Request)-[:BY]->(:Principal)`,
`(:Request)-[:CLASSIFIED_AS]->(:Classification)`, `(:Request)-[:SERVED_BY]->(:Model)`,
`(:Request)-[:DENIED_FOR]->(:DenialReason)`. Request nodes carry timestamp, effect, tokens, cost,
duration, fingerprint. MERGE on stable keys (request id; names for dimension nodes) makes projection
idempotent — replays and restarts are safe by construction.

Natural-language querying over the graph (question → Cypher via an LLM routed through Covenant
itself, in-perimeter) is a follow-on layer on top of this projection, not part of this decision.

## Consequences
- (+) Auditor/forensics questions become one-hop Cypher; canned queries ship in `deploy/neo4j/`.
- (+) RQ2 strengthened: evidence proves sufficient not just to verify but to *interrogate*.
- (+) Idempotent projection means zero coordination with the audit path — read-only tailing.
- (−) New Host dependency: `Neo4j.Driver` 6.2.1 (Apache-2.0, official, Bolt protocol). Pinned,
  audited, recorded in PROVENANCE. Loaded only when configured, but shipped in the binary.
- (−) A second queryable copy of evidence metadata exists in Neo4j; its access control is the
  customer's Neo4j auth, not Covenant's admin token. Stated in deploy docs; the graph holds no
  content beyond what the operator already opted into for audit entries.
- (−) Projection lag (poll interval) means the graph trails the log by seconds. Irrelevant for
  audit/forensics use; anyone needing real-time uses the dashboard stream.

## Alternatives considered
- **Embed a graph store in the appliance.** Rejected: breaks single-binary posture (Langfuse
  reasoning, ADR-0003); Neo4j is customer-operated infrastructure like their vault or collector.
- **GraphRAG as a proxy feature (retrieval for customer apps).** Rejected: application-layer scope;
  customer RAG context already flows through prompts and is governed like any content.
- **SQL projection instead (Postgres).** Deferred, not rejected: the audit-store ADR owns durable
  storage. This projection is analytical, relationship-shaped, and disposable — graph fits; nothing
  prevents a relational projection later from the same log.
- **Query the log directly with scripts.** Status quo; works for single questions, degrades for
  multi-hop lineage, and gives auditors nothing self-service.
