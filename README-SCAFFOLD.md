# Scaffold bring-up

One-time steps to get this tree compiling. Delete this file once the build is green.

> **This code has never been compiled.** It was written outside a .NET environment, so the first
> `dotnet build` is its first real test. Expect to fix small things (package versions, API drift in
> `Microsoft.Extensions.AI`). Nothing here is verified until the tests pass.

## 1. Check the target framework
`Directory.Build.props` targets **net10.0**. If the installed SDK differs, change it there — one line, one place.

```
dotnet --list-sdks
```

## 2. Restore packages
All PackageReferences are pinned in the `.csproj` files (verified against nuget.org 2026-07-15) and
recorded in `docs/PROVENANCE.md`. Just:

```
dotnet restore
```

## 3. Build and test
```
dotnet build
dotnet test
```

Coverage (target: ≥60% line coverage on src/, governance hardest):
```
dotnet test --collect:"XPlat Code Coverage"
# summary: the generated coverage.cobertura.xml under tests/Covenant.Tests/TestResults/<guid>/
grep -o 'line-rate="[0-9.]*"' tests/Covenant.Tests/TestResults/*/coverage.cobertura.xml | head -1
```

The tests that must pass, hardest first:
- `Pii_request_is_denied_never_reaches_public_provider_and_is_audited` — fail-closed. This one is the
  product. It uses a tripwire client that throws if PII ever reaches a public provider.
- `Tripped_kill_switch_denies_never_reaches_a_provider_and_is_audited` — the kill switch.
- `Exhausted_team_budget_blocks_the_request_and_never_reaches_a_provider` and
  `Exhausted_global_budget_blocks_teams_without_their_own_cap` — budget enforcement.
- `Internal_request_is_served_attributed_and_audited`, `Served_request_records_its_attributed_cost_in_the_ledger`,
  `Denied_request_consumes_no_budget` — the happy path and the ledger.

## 4. Run it
```
dotnet user-secrets init --project src/Covenant.Host
dotnet user-secrets set "OpenAI:ApiKey"        "<key>"           --project src/Covenant.Host
dotnet user-secrets set "Admin:Token"          "dev-admin-token" --project src/Covenant.Host
dotnet user-secrets set "Budget:GlobalCapUsd"  "5.00"            --project src/Covenant.Host
dotnet user-secrets set "Auth:Keys:0:Key"       "demo-key"       --project src/Covenant.Host
dotnet user-secrets set "Auth:Keys:0:Principal" "demo-user"      --project src/Covenant.Host
dotnet user-secrets set "Auth:Keys:0:Team"      "platform"       --project src/Covenant.Host
dotnet run --project src/Covenant.Host
```
All of these are required — the appliance refuses to start without them (fail-closed). Callers present
their key as a standard `Authorization: Bearer <key>` header (OpenAI SDK clients do this natively);
the key decides principal and team — client headers can't impersonate. To serve without keys you must
opt in explicitly: `Auth:AllowAnonymous` = `true`. Optional per-team
caps: `dotnet user-secrets set "Budget:TeamCapsUsd:platform" "1.00" --project src/Covenant.Host`.

Optional in-perimeter model (any OpenAI-compatible server — Ollama, vLLM, LM Studio). Without it,
PII/PHI requests are denied by design; with it, they are served locally and never leave the perimeter:
```
# example: Ollama on the same machine (`ollama pull llama3.1:8b` first)
dotnet user-secrets set "Local:Endpoint" "http://localhost:11434/v1" --project src/Covenant.Host
dotnet user-secrets set "Local:ModelId"  "llama3.1:8b"               --project src/Covenant.Host
```

Pricing: defaults are real per-1M rates (gpt-4o-mini $0.15/$0.60, gpt-4o $2.50/$10.00) converted
internally to per-1K. Override per model when providers change prices:
`dotnet user-secrets set "Pricing:gpt-4o-mini:InPer1M" "0.15" ...` (+ `OutPer1M`).

Reset between demo runs: **Settings → Archive log & reset counters** in the dashboard (or
`curl -X POST localhost:5100/admin/reset -H 'X-Covenant-Admin-Token: dev-admin-token'`). The audit
log is archived with a timestamp, never deleted; budgets reopen.

Chain-head anchoring (ADR-0007 — makes end-truncation of the audit log detectable; point the anchor
file at an INDEPENDENT storage domain, that placement is the security):
```
dotnet user-secrets set "Audit:AnchorPath"  "/Volumes/other-disk/covenant.anchors" --project src/Covenant.Host
dotnet user-secrets set "Audit:AnchorEvery" "100"                                  --project src/Covenant.Host
```

Rate limits (opt-in; refusals are HTTP 429 and audited; 0 = unlimited):
```
dotnet user-secrets set "RateLimit:PerTeamPerMinute" "30"  --project src/Covenant.Host
dotnet user-secrets set "RateLimit:GlobalPerMinute"  "120" --project src/Covenant.Host
```
With `Auth:AllowAnonymous` set, team names come from client headers — set BOTH caps there, since
per-team alone can be diluted by invented team names.

Response cache (opt-in; a hit skips the provider entirely — $0, no latency; keys are team-scoped,
entries live in memory only and die with the process):
```
dotnet user-secrets set "Cache:TtlSeconds" "300" --project src/Covenant.Host
```

Complexity routing: Public/Internal route lists are ordered cheapest → strongest
(`gpt-4o-mini` → `gpt-4o` by default; override via `OpenAI:ModelId` / `OpenAI:StrongModelId`).
Prompts estimated above `Routing:ComplexityTokenThreshold` tokens (default 400, chars÷4 heuristic)
escalate to the strong model; an explicitly requested model wins if policy permits it.

Optional tracing (ADR-0003 — off by default; endpoint must be in-perimeter). A permanent local
collector ships in the repo:
```
(cd deploy/otel && docker compose up -d)     # survives reboots; docker compose logs -f to watch spans
dotnet user-secrets set "Otel:Endpoint" "http://localhost:4318/v1/traces" --project src/Covenant.Host
```
Any OTLP/HTTP backend works — Langfuse (`https://<host>/api/public/otel/v1/traces` + `Otel:Headers`
`"Authorization=Basic <base64 pk:sk>"`), Grafana, a collector that forwards. See
`deploy/otel/otel-collector-config.yaml`.

Optional evidence graph (ADR-0006 — auditor/forensics queries over the audit log as a Neo4j graph;
derived data, never the evidence of record):
```
(cd deploy/neo4j && docker compose up -d)    # browser at http://localhost:7474 (neo4j / covenant-graph)
dotnet user-secrets set "Neo4j:Uri"      "bolt://localhost:7687" --project src/Covenant.Host
dotnet user-secrets set "Neo4j:Password" "covenant-graph"        --project src/Covenant.Host
```
The projector tails the verified log every 5s; canned Cypher queries live in `deploy/neo4j/queries.md`
(who touched PHI, spend lineage per team, kill-switch forensics, duplicate-prompt fingerprints).

Fully offline demo (no OpenAI account needed): point the "openai" adapter at the same local server —
governance, budgets, and audit behave identically:
```
dotnet user-secrets set "OpenAI:Endpoint" "http://localhost:11434/v1" --project src/Covenant.Host
dotnet user-secrets set "OpenAI:ModelId"  "llama3.1:8b"               --project src/Covenant.Host
```

Scripted demo (server running in another terminal): `./demo.sh`, or `./demo.sh --tamper` to also
prove the audit chain detects edits (destructive to the log). For realistic dashboard data use the
traffic generator: `./demo.sh --traffic 60 10` sends 60 randomized requests over ~10 minutes
(short/long prompts, PII/PHI, missing and wrong keys, disallowed model asks).

Input previews in request details are OFF by default — audit evidence is metadata plus a SHA-256
content fingerprint. To capture a truncated input preview (your perimeter, your call):
`dotnet user-secrets set "Audit:PromptPreviewChars" "120" --project src/Covenant.Host` and restart.

Dashboard (FinOps savings, budgets, denials, kill switch, routing policy):
open **http://localhost:5100/admin/ui** and enter the admin token. Self-contained page — no CDN,
no external assets; the appliance stays a single artifact with no egress.

Governed request (routes to OpenAI, gets attributed and audited):
```
curl -s localhost:5100/v1/chat/completions \
  -H 'Content-Type: application/json' -H 'Authorization: Bearer demo-key' \
  -H 'X-Covenant-Workflow: demo' -H 'X-Covenant-UseCase: smoke-test' \
  -d '{"messages":[{"role":"user","content":"say hello"}]}'
```

Streamed (SSE; same governance, attribution and audit fire on stream completion — ADR-0002):
```
curl -sN localhost:5100/v1/chat/completions \
  -H 'Content-Type: application/json' -H 'Authorization: Bearer demo-key' \
  -H 'X-Covenant-Workflow: demo' -H 'X-Covenant-UseCase: smoke-test' \
  -d '{"stream":true,"messages":[{"role":"user","content":"count from 1 to 5"}]}'
```

Fail-closed request (classifies PII → no permitted route → 403, never reaches a provider):
```
curl -s localhost:5100/v1/chat/completions \
  -H 'Content-Type: application/json' -H 'Authorization: Bearer demo-key' \
  -d '{"messages":[{"role":"user","content":"my SSN is 123-45-6789"}]}'
```
(Without the `Authorization` header the same request is 401 — authentication is denied before
classification even runs, and that refusal is audited too.)

Kill switch (engage, watch requests 403, disengage):
```
curl -s -X POST localhost:5100/admin/kill-switch \
  -H 'Content-Type: application/json' -H 'X-Covenant-Admin-Token: dev-admin-token' \
  -d '{"engaged":true,"reason":"incident drill"}'

curl -s -X POST localhost:5100/admin/kill-switch \
  -H 'Content-Type: application/json' -H 'X-Covenant-Admin-Token: dev-admin-token' \
  -d '{"engaged":false}'
```

Compliance-evidence export (verifies the hash chain, then summarizes allow/deny counts, spend by team,
requests by classification):
```
curl -s localhost:5100/admin/evidence -H 'X-Covenant-Admin-Token: dev-admin-token'
```
To see tamper-evidence work, edit any middle line of `covenant-audit.log` and re-run the export —
`chain_valid` flips to false with the first broken line number.

Then inspect `covenant-audit.log` — one hash-chained line per request, **including every denial**.

## 5. Real traffic through the proxy
Covenant is OpenAI-wire-compatible, so any OpenAI-compatible client can use it as its backend —
every chat then flows through governance and shows up on the dashboard. A ChatGPT-style UI
(Open WebUI) pointed at Covenant:
```
docker run -d --name open-webui -p 3000:8080 \
  -e OPENAI_API_BASE_URL=http://host.docker.internal:5100/v1 \
  -e OPENAI_API_KEY=demo-key \
  ghcr.io/open-webui/open-webui:main
```
Open http://localhost:3000 — the model picker lists only policy-permitted models (`/v1/models` is
governed too). Or from code:
```python
from openai import OpenAI
client = OpenAI(base_url="http://localhost:5100/v1", api_key="demo-key")
client.chat.completions.create(model="gpt-4o-mini", messages=[{"role": "user", "content": "hi"}])
```
First slice speaks plain-text chat (buffered + SSE). Multimodal content and tool calls are not yet
part of the wire surface.

## 6. Next
- Run the **NativeAOT spike** from ADR-0001 (`PublishAot=true` in `Covenant.Host.csproj`) and record the
  result. It decides the `deploy/` story.
- ADR: durable audit store + chain-head anchoring (truncation-from-the-end is not detectable from the
  file alone — see `AuditChain.cs`).
