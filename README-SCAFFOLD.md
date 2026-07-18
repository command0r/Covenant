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

Optional tracing (ADR-0003 — off by default; endpoint must be in-perimeter). A permanent local
collector ships in the repo:
```
(cd deploy/otel && docker compose up -d)     # survives reboots; docker compose logs -f to watch spans
dotnet user-secrets set "Otel:Endpoint" "http://localhost:4318/v1/traces" --project src/Covenant.Host
```
Any OTLP/HTTP backend works — Langfuse (`https://<host>/api/public/otel/v1/traces` + `Otel:Headers`
`"Authorization=Basic <base64 pk:sk>"`), Grafana, a collector that forwards. See
`deploy/otel/otel-collector-config.yaml`.

Fully offline demo (no OpenAI account needed): point the "openai" adapter at the same local server —
governance, budgets, and audit behave identically:
```
dotnet user-secrets set "OpenAI:Endpoint" "http://localhost:11434/v1" --project src/Covenant.Host
dotnet user-secrets set "OpenAI:ModelId"  "llama3.1:8b"               --project src/Covenant.Host
```

Scripted demo (server running in another terminal): `./demo.sh`, or `./demo.sh --tamper` to also
prove the audit chain detects edits (destructive to the log).

Dashboard (FinOps savings, budgets, denials, kill switch, routing policy):
open **http://localhost:5000/admin/ui** and enter the admin token. Self-contained page — no CDN,
no external assets; the appliance stays a single artifact with no egress.

Governed request (routes to OpenAI, gets attributed and audited):
```
curl -s localhost:5000/v1/chat/completions \
  -H 'Content-Type: application/json' -H 'Authorization: Bearer demo-key' \
  -H 'X-Covenant-Workflow: demo' -H 'X-Covenant-UseCase: smoke-test' \
  -d '{"messages":[{"role":"user","content":"say hello"}]}'
```

Streamed (SSE; same governance, attribution and audit fire on stream completion — ADR-0002):
```
curl -sN localhost:5000/v1/chat/completions \
  -H 'Content-Type: application/json' -H 'Authorization: Bearer demo-key' \
  -H 'X-Covenant-Workflow: demo' -H 'X-Covenant-UseCase: smoke-test' \
  -d '{"stream":true,"messages":[{"role":"user","content":"count from 1 to 5"}]}'
```

Fail-closed request (classifies PII → no permitted route → 403, never reaches a provider):
```
curl -s localhost:5000/v1/chat/completions \
  -H 'Content-Type: application/json' -H 'Authorization: Bearer demo-key' \
  -d '{"messages":[{"role":"user","content":"my SSN is 123-45-6789"}]}'
```
(Without the `Authorization` header the same request is 401 — authentication is denied before
classification even runs, and that refusal is audited too.)

Kill switch (engage, watch requests 403, disengage):
```
curl -s -X POST localhost:5000/admin/kill-switch \
  -H 'Content-Type: application/json' -H 'X-Covenant-Admin-Token: dev-admin-token' \
  -d '{"engaged":true,"reason":"incident drill"}'

curl -s -X POST localhost:5000/admin/kill-switch \
  -H 'Content-Type: application/json' -H 'X-Covenant-Admin-Token: dev-admin-token' \
  -d '{"engaged":false}'
```

Compliance-evidence export (verifies the hash chain, then summarizes allow/deny counts, spend by team,
requests by classification):
```
curl -s localhost:5000/admin/evidence -H 'X-Covenant-Admin-Token: dev-admin-token'
```
To see tamper-evidence work, edit any middle line of `covenant-audit.log` and re-run the export —
`chain_valid` flips to false with the first broken line number.

Then inspect `covenant-audit.log` — one hash-chained line per request, **including every denial**.

## 5. Next
- Run the **NativeAOT spike** from ADR-0001 (`PublishAot=true` in `Covenant.Host.csproj`) and record the
  result. It decides the `deploy/` story.
- ADR: durable audit store + chain-head anchoring (truncation-from-the-end is not detectable from the
  file alone — see `AuditChain.cs`).
