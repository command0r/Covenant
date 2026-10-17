# Covenant console — demo runbook

The demo runs entirely in the browser after startup. `demo.sh` stays an optional smoke/diagnostic tool.

## 1. Configuration (names only — values live in user-secrets / env / vault)

Required in every mode: `Admin:Token`, `Budget:GlobalCapUsd`, `Auth:Keys:0:Key`, `Auth:Keys:0:Principal`,
`Auth:Keys:0:Team` (the application key used by Playground is one of these keys — never the admin token).

| Mode | Settings |
|---|---|
| Cloud + local PII route (default) | `OpenAI:ApiKey`, optional `Local:Endpoint` + `Local:ModelId` |
| Local-only (no cloud credential) | `Providers:Mode=local-only`, `Local:Endpoint`, `Local:ModelId`; **no** `OpenAI:ApiKey` / `Anthropic:ApiKey` |

Optional: `Covenant:Environment` (header label, e.g. `vslive-demo`), `Providers:ProbeAllowList`
(host:port list the wizard may probe; the applied Local:Endpoint is always allowed), `Cache:TtlSeconds`,
`Audit:PromptPreviewChars` (0 = no prompt text in evidence), `Audit:AnchorPath` + `Audit:AnchorEvery`.

`Local:Endpoint` is as seen **from the Covenant host** (`http://localhost:11434/v1` only if Ollama runs on the
same machine/container as Covenant). `Local:ModelId` is the exact installed tag (`ollama list`) — nothing guesses it.

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

## 2. Startup and preflight (before the audience arrives)

```
dotnet run --project src/Covenant.Host -c Release        # or the published binary
```
1. Open `http://localhost:5100/ui/` → Connect with the admin token (held in this tab only).
2. Overview: no red alerts. Header shows the environment, "Console connected", fresh "Updated n s ago".
3. Models & connections: applied local endpoint/model shown, **Registered: yes**. Run *Test connection*, then
   the governed test with the application key → Served by `local:<tag>`. This also **warms the model**
   (first Ollama load can take 10–60 s; do it before the talk).
4. Evidence: chain "Internal consistency verified". If anchoring is configured, anchors written > 0.
5. Settings → note prompt retention (`prompt_preview_chars`). Do **not** use Maintenance → Archive & reset as
   a demo step; it archives evidence (budgets are NOT reopened — the ledger is independent of audit segments).

## 3. The A / B / C / D sequence (Playground, optionally Presentation mode)

Paste the application key once (memory only). Presets only fill the form; Run sends a real request.

| Run | Expected outcome | Evidence to show |
|---|---|---|
| **A** normal request | Served (cloud mode: cheapest permitted cloud model; local-only: `local:<tag>`) | Selected = attempted = served; cost basis |
| **B** unapproved model | HTTP 403 Policy denied; provider attempted: **None** | Stopped at `policy`; classification evaluated |
| **C** synthetic PII | Served by `local:<tag>`; without a local route: 403 at `provider`, attempted None | Signal "US SSN pattern"; never a cloud model |
| **D** pause → run → resume → run | 403 Paused (stopped at `pause`), then Served | Fresh nonce each run, so never a cache hit |

Auth failure demo: clear the key and Run → 401, classification **Not evaluated — stopped at auth**.
Every result has *View request evidence* → *Open in Requests*: the exact record by request id.

## 4. Recovery

| Symptom | Action |
|---|---|
| Header "Stale" / "Connection lost — retrying" | Covenant process down or network: check terminal; console reconnects by itself |
| Local route **degraded** (last success kept) | Ollama stopped/unreachable: restart Ollama, re-run the governed test; protected requests are refused meanwhile (no cloud fallback) |
| C refused at `provider` | Local route not registered: Models & connections shows why (not configured / wrong tag) |
| Everything 403 Paused | Header Resume (pause is global and persists across pages) |
| Budget exhausted | Raise `Budget:GlobalCapUsd` and restart — not Archive & reset |
| Token rejected | Re-enter the admin token (sessionStorage is per tab) |

## 5. Optional: Open WebUI as a client
Base URL `http://<covenant-host>:5100/v1`, API key = a Covenant **application** key. Text chat only; streaming
supported. Pointing Open WebUI at Ollama directly bypasses Covenant.
