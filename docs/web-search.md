# Governed web search (Tavily): setup and use

Decision record: [ADR-0015](adr/ADR-0015-governed-web-search.md). Wire contract: `ui-api-contract.md`, section
"Governed web search".

Covenant can ground a console answer in fresh public web sources. It runs **one bounded Tavily search** as the pipeline
stage `search`, after auth, policy, rate limit, pause and budget. The model never calls a tool; it receives the
retrieved snippets as untrusted data. Web search is **off by default**.

## 1. Server setup (startup; restart required)

1. **API key**: set `Tavily:ApiKey` in the server's secret store.
   - Environment variable: `Tavily__ApiKey=tvly-…`
   - Development only: `dotnet user-secrets set "Tavily:ApiKey" "tvly-…" --project src/Covenant.Host`. User secrets
     load only in the Development environment.
   - The key is never entered in the console, never stored in configuration, never returned by any endpoint, and is
     redacted from logs.
   - Covenant does not read or import keys from other tools. For example, a key in Open WebUI's settings is not used.
2. **Egress**: add `api.tavily.com:443` to the startup allow-list `Providers:ProbeAllowList`. Separate entries with a
   comma, semicolon or space, for example `Providers__ProbeAllowList="api.tavily.com:443"`. The console cannot widen
   egress.
3. Restart the Host. Then check **Settings → Web search (Tavily)**: the checklist shows *API key configured* and
   *egress allow-list* as "yes".

For the local demo launcher (which deliberately runs in Production), use:

```bash
python3 scripts/run-with-tavily.py
```

This explicitly loads **only** the saved Tavily key into the child process, approves `api.tavily.com:443`,
and starts the usual demo launcher. It also accepts an existing `Tavily__ApiKey` environment variable.
It never prints the key or imports other saved credentials. Normal launcher options (`PORT`, `STATE`, `OUT`,
`SKIP_PUBLISH`) still work. It does not enable search or change policies automatically; complete the steps below.

## 2. Configuration (console; versioned, no restart)

1. **Mode**: the configuration must be **Hybrid** (Policies). Local-only means no external calls of any kind, so it can
   never search.
2. **Settings → Web search → Edit → Configure web search…**:
   - Enter the **price per Tavily credit** from your plan. A basic search uses 1 credit.
   - An empty price means unknown, and search stays refused. It is never treated as $0.
   - Optional: max results (1–3, default 3) and timeout (1–30 s, default 10).
   - Tick **Enable web search**.
3. **Policies → Applications & workload profiles → Edit**: tick **Allow web search** on a **Public-baseline** profile,
   for example `public-demo`. Internal, PII and PHI profiles cannot be allowed.
4. **Preview impact → Save & apply.** Validation refuses an incomplete setup with a field error. The codes are
   `search_requires_hybrid`, `search_unpriced`, `secret_unavailable`, `search_egress_not_approved` and
   `search_profile_not_public`.

Settings shows **Ready** when every item in the checklist holds.

## 3. Use (Playground)

1. **Run as** a console-testable application, with the Public profile you allowed.
2. **Web sources**:
   - **Off** (default): no search.
   - **Auto**: search only when the prompt asks for current information. Cues include weather, forecast,
     temperature, latest, today, tonight, this week, currently, right now, news, scores and stock price.
   - **Required**: always search first.
3. **Location** (optional): an explicit place for weather questions.
   - A weather question with no location, and no place named in the prompt ("weather **in** Toronto"), is answered
     with a request for one **before anything is sent externally**.
   - Covenant never guesses your location: no IP geolocation, no default city.
4. **Run.** Under the answer, **Web sources** shows what the Host actually did. Either:
   - *Searched: N sources retrieved at … UTC*, with the source titles and links. Links are shown only for safe
     http(s) URLs.
   - Or the plain reason no search ran or why it failed.

   The `[n]` citations in the answer are the model's own and are not verified; the source list is the evidence. The
   trace shows a **Web search** chip between Budget and Provider.

Example: Web sources **Auto**, Location `Toronto`, prompt *"What's the weather today?"*. Weather answers carry no
accuracy guarantee.

## 4. What is guaranteed (and tested)

- **No external call unless every check passes**:
  - Search must be enabled in configuration, and the mode must be Hybrid.
  - The profile must be allowed, and the request must be classified **Public**.
  - The search query and location are classified separately; PII or PHI in them refuses the search
    (`search_sensitive`).
  - The key must be configured, the egress approved and a price configured.
  - The search must fit the budget (it is reserved before the call), and provider calls must not be paused.
- **One call**: `POST https://api.tavily.com/search` with `search_depth: basic`, `max_results ≤ 3`,
  `include_answer: false`, `include_raw_content: false`, `include_usage: true` and `auto_parameters: false`. No
  retries, no redirects, no crawling, no follow-up searches. The call has a deadline and a 256 KiB response cap.
- **Clear failure**: a timeout, a 429, an HTTP error, an oversized or invalid response, or zero usable sources fails
  the request (502). The model is never asked to answer a "current" question without evidence.
- **Accounting**: the search is a separate tool call (`ToolCalls[]`) on the same ledger and budgets as inference.
  - It is charged as *credits reported × configured price*.
  - When the call was sent but failed, was cancelled or reported no usage, it is charged at the reservation, never $0.
  - When the call was never sent, the reservation is released.
- **No stale answers**: a request that triggers a search bypasses the exact cache (no lookup, no store).
- **Evidence**: the audit record keeps only bounded metadata: mode, cue, outcome, reason, source count, up to 3 source
  hosts, duration and the charge. It never keeps the query, the location or the snippets. The full source list
  (title, URL, retrieval time) lives only in the transient request store
  (`GET /admin/requests/{id}/search`, same retention as traces).

## 5. Troubleshooting

| You see | Cause | Fix |
|---|---|---|
| *Web search is not permitted … Nothing was sent externally* | disabled, Local-only, profile not allowed, no price, key or egress missing | Settings → Web search checklist and setup steps |
| *Refused: only Public requests may be searched* | the profile or the detector made the request Internal/PII/PHI, or the query/location contains sensitive data | use a Public profile and remove the sensitive content |
| *A weather question needs a location* | no Location field and no place in the prompt | fill Location, or write "weather in <city>" |
| *The search would exceed a budget* | the reservation (price × 1 credit) does not fit | raise the budget or wait for the period to roll over |
| *The search timed out / was rate-limited (429) / failed* | Tavily did not answer in time or refused | retry later; the request was charged at its reservation if it was sent |
| *The search returned no usable sources* | zero results, or none with a safe http(s) URL | rephrase; nothing was answered without evidence |
