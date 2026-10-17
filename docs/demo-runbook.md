# Covenant console v2 — demo runbook

Everything after startup is done in the browser. `demo.sh` remains an optional `/v1` smoke tool. Scenario letters
follow `docs/CONSOLE-V2-IMPLEMENTATION-SPEC.md` §8. Expected behaviour below describes the implementation; it is
not a record of a run — see `docs/console-v2-acceptance.md` for what has actually been executed.

## 1. Start an isolated demo profile

```
scripts/run-local-demo.sh                    # Local-only: Ollama only, no cloud credential, port 5210
PROFILE=hybrid OPENAI_API_KEY=… CLOUD_PRICE_IN_1M=… CLOUD_PRICE_OUT_1M=… scripts/run-local-demo.sh
```
The launcher checks Ollama and the model tag, publishes the self-contained binary, starts it on its own port with
its own state directory (`/tmp/covenant-v2-demo-<profile>/`: audit log, configuration repository, budget ledger),
and opens the console already connected (admin token in the URL fragment, read once, never sent to the server).
It never touches another instance (e.g. :5100, :5201). State persists per profile; `FRESH=1` moves it aside
(renamed, not deleted).

**Seeds** (`Config:Seed`, first start only; afterwards the configuration repository wins — ADR-0011):
- Workload profiles: `public-demo` (trusted baseline **Public**, synthetic public content only) and
  `internal-default` (baseline **Internal**). Detection can only escalate a baseline (US-SSN pattern → PII;
  MRN/diagnosis/patient-id keywords → PHI). It is a demonstration detector, not DLP.
- Application `demo-app` (team `platform`, console testing allowed, granted both profiles).
- Policies — Local-only: everything → `ollama/<tag>`. Hybrid: Public → `openai/<model>` with approved fallback
  `ollama/<tag>`; Internal → local; PII & PHI → local only (cloud targets are never eligible for PII/PHI).
- Budget: global, monthly, cap `CAP` (default $5). Hybrid cloud prices come only from the operator
  (`CLOUD_PRICE_IN_1M`/`OUT_1M`, current provider list price); the seed refuses to start without them.

## 1a. Supported artifact (2026-10-17)

The supported appliance artifact is a **self-contained single-file publish** (one deployable, no runtime install):

```
dotnet publish src/Covenant.Host -c Release -r <rid> --self-contained true -p:PublishSingleFile=true
```

**NativeAOT is not supported yet.** An AOT publish reports IL2104 (trim analysis warnings inside the assembly) /
IL3053 (AOT analysis warnings inside the assembly) for **Anthropic 12.8.0** and **Neo4j.Driver 6.2.1**. These are not
suppressed and the build is not claimed AOT-verified. Unblocking needs an AOT-clean release of each library (or
replacing the Anthropic connector with an AOT-clean `IChatClient`, and moving the optional Neo4j projector out of the
appliance image), then a clean `PublishAot=true` run. See ADR-0001 "Status update 2026-10-17".

## 2. Preflight (before the audience arrives)

1. Header shows the environment (`demo-local-only` / `demo-hybrid`), the **mode badge** (Local-only / Hybrid),
   **Live**, and "New provider calls: running". No red alerts on Overview.
2. **Models & connections**: the Ollama connection shows the selected tag; Test succeeds. Run one request from
   the Playground to **warm the model** (first Ollama load can take 10–60 s) — the header local health turns ok.
3. **Policies**: route view reads e.g. "Public → openai/gpt-4o-mini · fallback: ollama/qwen2.5-coder:7b";
   "Internal → ollama/…"; "PII & PHI → ollama/… (local only)".
4. **Budgets**: global budget, period id, spent/reserved/remaining, warning none.
5. **Audit & evidence**: "Audit integrity verified" (internal consistency of the hash chain; not answer quality).
6. Settings: prompt preview retention 0 (no prompt text in evidence) unless you deliberately change it.

## 3. Walkthrough (Playground; Presentation mode for the projector)

"Run as" **demo-app**; pick the profile; presets only fill the form — Run sends a real governed request. Each run
shows the live trace (auth → shape → classify → policy → cache → rate limit → pause → budget → provider (per
attempt) → attribution), the outcome, destination (Local/Cloud tag), final classification, tokens, cost basis,
and **Evidence recorded** → *Open in Requests* for the exact record.

| | Do | Expected decision / trace | Cost & evidence |
|---|---|---|---|
| **A** Public → cloud (Hybrid) | Preset A, profile Public demo | classify passed (Public, no escalation); policy → openai primary; provider passed | reported usage × configured price ("estimated provider charge"); team `platform`, app `demo-app` |
| **A′** same in Local-only | Preset A | Public → ollama | $0 configured provider charge — infrastructure excluded |
| **B** Sensitive → local | Preset B (synthetic SSN 078-05-1120), profile Public demo | classify: **PII (baseline Public, escalated: US SSN pattern)**; policy → ollama only | zero-priced local; evidence shows the signal, never the prompt |
| **B′** cloud override refused | B with Advanced → model `openai/<model>` | policy refused: target not eligible for PII; provider **skipped** | no provider call, $0, audited refusal |
| **C** missing local | Models & connections → disable the Ollama connection → Preview → Save (new version) → run B | policy refused: no eligible destination; provider skipped; **no cloud call** | re-enable afterwards (another new version) |
| **D** approved fallback | (fixture) Policies → pause the primary route **or** stop the primary provider → run A | provider attempt 1 failed/paused-skipped → attempt 2 on the approved fallback | both attempts in the drawer; failed attempt charged its estimate, never $0 |
| **F** budget | Budgets → lower the cap below spend → Preview → Save → run A | budget refused ("would exceed cap") | spend preserved across the edit; raise the cap again (audited new version) |
| **G** exact cache | Settings → cache TTL 60 → Save; run the same request twice | 2nd run: cache passed, rate limit/pause/budget/provider **skipped (cache_hit)** | $0 no provider call; drawer names the original served connection/model; any config change invalidates |
| **H** policy editing | Policies → pause the primary route → Save; run A; pause the whole policy → Save; run A; resume → Save | route paused → fallback serves; policy paused → refused; resumed → primary again | each change is a new version (history + rollback available) |
| **Pause** | Header → Pause new provider calls → run D (fresh nonce) → Resume → run D | pause stage refused → then served | cache hits are not stopped by the pause (documented) |
| **Auth** | Applications & workload profiles → disable console testing for demo-app → Save → Run | auth refused (governed 403, audited) | re-enable afterwards |

Stage timings are measured by the server; skipped stages are shown as skipped, never animated as if they ran.

## 4. Recovery

| Symptom | Action |
|---|---|
| Header **Stale** / connection lost | Covenant stopped? Check the launcher terminal / `$STATE/server.log`; the console reconnects by itself |
| Local health **degraded** | Ollama down/unreachable: restart Ollama; protected requests are refused meanwhile (no cloud fallback for Internal/PII/PHI) |
| Save shows "Configuration changed since you started editing" | Another tab/operator applied a version: Reload, re-apply your change |
| Save refused with field errors | Fix the highlighted fields; the active version is unchanged |
| Everything refused at **pause** | Header → Resume |
| Budget exhausted | Raise the cap (Budgets → Save). Archive/reset does **not** reopen budgets (ADR-0014) |
| Startup refused | The launcher prints Covenant's reason (e.g. missing hybrid prices, corrupt journal — never auto-repaired silently) |

## 5. Optional: Open WebUI as a client
Base URL `http://localhost:<PORT>/v1`, API key = the launcher's **/v1 app key** (never the admin token); optional
header `X-Covenant-Profile` selects a granted profile. Text chat only; streaming supported. Pointing Open WebUI at
Ollama directly bypasses Covenant.
