# Evidence graph — canned auditor queries (ADR-0006)

Run in the Neo4j browser (http://localhost:7474) once the projector has data. The graph is a
projection of the audit log — derived, rebuildable, never the evidence of record.

## Who touched sensitive data, and where did it go?
```cypher
MATCH (p:Principal)<-[:BY]-(r:Request)-[:CLASSIFIED_AS]->(c:Classification)
WHERE c.name IN ['Pii', 'Phi']
OPTIONAL MATCH (r)-[:SERVED_BY]->(m:Model)
RETURN p.name AS principal, c.name AS classification, r.effect AS outcome,
       coalesce(m.name, '— (refused)') AS servedBy, count(r) AS requests
ORDER BY requests DESC
```

## Which teams' spend flows to which models?
```cypher
MATCH (t:Team)<-[:MEMBER_OF]-(:Principal)<-[:BY]-(r:Request)-[:SERVED_BY]->(m:Model)
RETURN t.name AS team, m.name AS model,
       count(r) AS requests, round(sum(r.costUsd), 5) AS totalUsd
ORDER BY totalUsd DESC
```

## Incident forensics: what happened while the kill switch was engaged?
```cypher
MATCH (r:Request)-[:DENIED_FOR]->(d:DenialReason)
WHERE d.text STARTS WITH 'kill switch engaged'
MATCH (r)-[:BY]->(p:Principal)
RETURN p.name AS principal, count(r) AS blockedAttempts,
       min(r.ts) AS firstAttempt, max(r.ts) AS lastAttempt
ORDER BY blockedAttempts DESC
```

## Denial breakdown — where does governance actually bite?
```cypher
MATCH (r:Request)-[:DENIED_FOR]->(d:DenialReason)
RETURN d.text AS reason, count(r) AS times
ORDER BY times DESC
```

## Duplicate-content detection via fingerprints (same prompt, many senders)
```cypher
MATCH (r:Request)
WHERE r.promptSha256 IS NOT NULL
WITH r.promptSha256 AS fingerprint, collect(DISTINCT r) AS reqs
WHERE size(reqs) > 1
UNWIND reqs AS r
MATCH (r)-[:BY]->(p:Principal)
RETURN fingerprint, count(r) AS occurrences, collect(DISTINCT p.name) AS senders
ORDER BY occurrences DESC LIMIT 20
```
