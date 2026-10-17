# Independent acceptance review — feat/console (TASKS 01–07)

Reviewer: independent, read-only. Date: 2026-10-16. Scope: uncommitted work on `feat/console` (base `0624887`),
reviewed against `docs/UI-implementation-tasks.txt` TASKS 01–07, the CODEX verification order, root/area
`CLAUDE.md` files, and ADR-0004/0007/0009/0010. TASK 08 is out of scope (pending).

## Verdict: **REQUEST CHANGES**

The design is mostly sound. Correlation, audit v2 compatibility, fail-closed accounting, local-only mode and
credential separation are in good shape when read as code. Approval is still blocked for three reasons:

1. **Nothing has been compiled or run.** `docs/console-progress.md` says "nothing below has been compiled or run
   yet" for both rounds, and another agent is making compile fixes in `src/Covenant.Ui` right now. No acceptance
   bullet that needs runtime behaviour can be marked done, and CODEX verification steps 2–9 have not been executed.
   The tests below were judged by reading them, not by running them.
2. **Packaging has a build race and a stale-asset problem**, and the caching rule can break the console after an
   upgrade (B2, S1).
3. **The stale-success fix (TASK 06) is incomplete.** With the cache on, the connection test can be answered from
   cache. A client disconnect is recorded as a provider failure and turns local-route health to degraded (B3, B4).

Status key: **MET** = code is present and a test would fail if the guarantee were removed (not executed).
**PARTIAL** = code is present but there is a gap, or only runtime/visual evidence could close it. **NOT MET** = no
implementation or no evidence. Nothing in this table has been executed.

## Acceptance table

| # | Acceptance bullet | Status | Evidence / gap |
|---|---|---|---|
| 01.1 | Published single appliance serves UI + assets without a separate server | PARTIAL | `Covenant.Host.csproj:50-58` embeds `obj/console-publish/wwwroot/ui/**`; `ConsoleAssets.cs:120-163` serves it. Never published. The `wwwroot/ui` publish layout is unverified (console-progress "Not verified" #2). Race and stale assets: B2. |
| 01.2 | Invalid token / disconnect / outage / reconnect states | PARTIAL | `ConsoleSession.cs:33-67,153-159`, `AdminApi.cs` 401 → `Unauthorized` event, `StatusFeed` backoff and polling fallback, `AppState.Tick` stale >8 s. Correct by reading; not run. |
| 01.3 | Pause/resume via POST /admin/kill-switch, consistent across pages | PARTIAL | `ConsoleSession.SetPausedAsync` (`ConsoleSession.cs:132-145`) re-reads `/admin/status` after the POST and never claims success optimistically. The single `AppState` drives every page. Not run. |
| 01.4 | No fixture runtime / canned results / external fonts ship | MET | `grep https?://` over `src/Covenant.Ui` finds only placeholder/help text (`ConnectionLogic.cs:20,22`, `Connections.razor:113`). Fonts are local (`wwwroot/fonts`) and listed in PROVENANCE. Caveat: the untracked `covenant-prototype/` (incl. `runtime.js`) sits in the repo root and would ship if `git add -A` (S12). |
| 01.5 | Keyboard focus, labels, drawer Escape, focus restoration | PARTIAL | `Drawer.razor:10,37-73` + `interop.js trapFocus`. Escape only fires while focus is inside the wrapper; the initial focus call is best-effort. Not exercised. |
| 02.1 | Concurrent / identical prompts / equal timestamps resolve correctly | MET | Server id `req_<guid>` (`ConsoleBackend.cs:18`), exact Ordinal match (`Program.cs:730-733`). Test `Twenty_concurrent_identical_prompts_resolve_to_their_own_records`. There is no timestamp join anywhere in the Host or the UI. |
| 02.2 | Allow/auth/policy/pause/missing adapter/rate limit/upstream/cancel carry identity + outcome | PARTIAL | Host tests cover all except cancellation, which is tested only at pipeline level (`CorrelationAndEvidenceTests.cs:593-619`). A client disconnect mid-stream can be recorded as `UpstreamFailure` (B4). Anthropic `Cancelled` maps to 403 `permission_error` (S9). |
| 02.3 | Mid-stream failure: transport 200, final outcome failure | MET | `Program.cs` SSE error line with `request_id`; test `Mid_stream_failure_keeps_http_200_but_records_a_failed_outcome`; UI `OutcomeModel.cs` "Transport 200, final outcome: failed". |
| 02.4 | Existing fixtures, anchors, ledger replay still verify | MET | Chain hashes the stored bytes (`Evidence.cs:46-54`, `FileAuditSink.cs:58-63`). New fields are nullable and appended last (`Decisions.cs:36-48`). `DenialKind` is stored as a string, so no enum-converter break. `LegacyAuditCompatibilityTests` hand-writes v1 bytes and checks verify + anchors + replay. |
| 02.5 | Lookup admin-only, bounded, never exposes unconfigured prompt content | PARTIAL | Admin-only and shape-checked (`Program.cs:723-735`). Bounded only to "current segment", which has no size bound; every poll re-verifies the whole file (S7). On a broken chain the lookup returns a misleading 404 (S6). Third-party provider error text goes into `Reason` (S8). |
| 03.1 | Tests: complete/partial/missing usage, local zero, no call, cache hit, interrupted stream, provider failure | MET | `AccountingTests.cs` covers each case with real stages. |
| 03.2 | Unknown cost never a false $0 or false saving in any aggregate | PARTIAL | Savings exclude unknown/estimated rows (`StatusContracts.cs:192-201`). But `TotalCostUsd` in status and evidence export sums unknown rows as $0 with no caveat. The UI never reads `unknown_cost_requests`. Budgets miscounts (S3). The local route is implicitly priced $0 (S2). |
| 03.3 | Ledger replay + old records compatible | MET | `LedgerReplay.Rebuild` charges any `CostUsd>0` regardless of Effect, matching runtime `ChargeFailedCall` (`Budget.cs:139-155`). Covered by the legacy test. |
| 04.1 | A succeeds; B refuses unapproved model without provider call | PARTIAL | Presets only fill inputs (`ScenarioPresets.cs`). The Host policy-denial test exists. The UI flow has not been run. |
| 04.2 | C fails closed without local registration; succeeds locally when configured | PARTIAL | Host: `Missing_local_adapter_403_selected_but_never_attempted`, `LocalOnlyModeTests`. UI not run. |
| 04.3 | D refuses while paused, succeeds after resume, repeatedly | PARTIAL | A nonce is added on every Freeze (`ScenarioPresets.cs`), so the cache cannot mask it. Not run. |
| 04.4 | Empty key → real 401 and "classification not evaluated" | MET | `InferenceApi.cs:59-60` sends no header when the key is empty. Host test `Auth_denial_401_..._classification_is_not_evaluated`. `ModelFacts` renders "Not evaluated". |
| 04.5 | 1280×720 / 1920×1080 presentation fit without page scroll | NOT MET | No screenshots or measurements exist. CSS was changed to an app-height shell, which is unverified (console-progress "Not verified" #7). |
| 04.6 | No prompts/responses/app key persist after reload/disconnect | MET | `interop.js` persists only `covenant.theme` (localStorage) and `covenant.admin-token` (sessionStorage). `AppState.Disconnect` clears the key (`AppState.cs:132-148`). `PlaygroundState.Clear` clears runs and form. |
| 05.1 | Open from Playground selects the same record every time | PARTIAL | `Playground.razor:115,252,576` → `requests/{id}` built from the server header id. Not run. |
| 05.2 | Filters/empty/clear/selection/close/keyboard | PARTIAL | `RequestFilter.cs`, `RequestsViewState`, `ConsoleLogicTests`. UI not run. |
| 05.3 | Long model names/IDs readable at 1280 px | PARTIAL | Fixed-layout table with min-width 1120, ellipsis and `title`. No screenshot. |
| 05.4 | Raw JSON agrees with visible facts, follows retention config | PARTIAL | `Requests.razor:456` re-serializes the UI DTO with `WhenWritingNull`. That is not the stored record: null v2 fields disappear, so a v2 record with a null field and a legacy record look the same, and hashes are absent (S10). |
| 05.5 | Row removed from the recent window → clear unavailable state | PARTIAL | A 404 gives "Not in the current audit segment…". A tampered chain also gives 404 (S6). |
| 06.1 | Mock contract tests: reachable/refused/timeout/empty/tag mismatch/unauthorized/redirect/stale/response failures | PARTIAL | `ProviderProbeTests.cs` and `ConsoleLogicTests.cs:596-601` cover these. SSRF canonicalisation cases are untested (trailing dot, case, IPv6, default port, `0.0.0.0`, decimal IP, IDN) (S5). |
| 06.2 | Real smoke with Alex's Ollama/tag (buffered + stream + correlation + no cloud fallback) | NOT MET | Not performed. |
| 06.3 | Failed probe blocks progression; endpoint change invalidates draft | MET | `Connections.razor:124-222` uses gated fieldsets. `OnEndpointInput` → `InvalidateFromProbe` (`:353-370`). Draft probes never touch applied state (`Program.cs` probe handler only updates the applied endpoint key). |
| 06.4 | Applied config read back; checkbox never proof | MET | `ConnectionLogic.CheckApplied` uses the server `registered` flag from the live registry (`Program.cs` catalog). Tested. |
| 06.5 | Success then failure updates all pages consistently | PARTIAL | `ProviderHealth` lets a failure supersede and keeps last success (`ConsoleBackend.cs:40-50`). `RouteHealth.LocalRoute` treats a newer failure as degraded. Broken when the cache is on (B3). False failures come from client disconnects (B4). |
| 06.6 | Local-only starts without cloud secrets; incomplete config fails | MET | `StartupValidation.cs:22-51` + tests. Gap: no test proves the real registry has no cloud clients in local-only mode (S4). |
| 06.7 | Open WebUI instructions use Covenant /v1 + app key | MET | `Connections.razor:269-281`. |
| 07.1 | Pages agree on environment, pause, health, counts, accounting scope | PARTIAL | They share `AppState`/`CatalogState`. Budgets counts unknown cost differently from the server (S3). |
| 07.2 | Failed verification, stale data, unknown usage, missing config shown accurately | PARTIAL | Chain failure and stale states are honest. Unknown usage is under-reported (S3). A deleted anchor file shows "no anchor written yet" (S6b). |
| 07.3 | Archive/reset retains evidence and behaves as described | MET | `Settings.razor:110-213` confirms by typed environment name, is blocked while the chain fails, names both effects and re-syncs from the server. The Host `RotateAsync` archives; nothing is deleted. |
| 07.4 | No destructive maintenance from navigation / scenario selection | MET | `/admin/reset` is called only from `ResetAsync` behind `CanConfirm`. Presets never call admin mutations. |

### CODEX verification order

| Step | Status |
|---|---|
| 1 Diff/ADRs/provenance/audit compat | Done in this review. ADR-0009/0010 accepted before code; PROVENANCE updated for packages and fonts. ADR-0008 number skipped (nit N1). |
| 2 Unit/integration tests | **Not run** (no SDK here; author also had none). |
| 3 Credential separation + probe boundary | Reviewed statically. Separation clean (`AdminApi` vs `InferenceApi` own `HttpClient`, `Program.cs:271-273` UI). Probe has the gaps in S5. |
| 4–9 Published appliance, A/B/C/D, concurrency, accounting, Ollama, screenshots | **Not executed.** |

## Findings

### Blockers

**B1 — No build, test or publish evidence for any task.**
`docs/console-progress.md:3-4` and the TASKS 04–06 section both say "not compiled, not run". The repository rule is
"tests ship with the feature" (root `CLAUDE.md`), and the CODEX order requires a run. Fix: get
`dotnet build -c Release` and `dotnet test -c Release` (the CI command, `.github/workflows/ci.yml:43`) green with and
without `-p:SkipConsolePublish=true`. Attach output and coverage (≥0.80 gate), then re-request review.

**B2 — `PublishConsole` races with the solution build and embeds stale files.** (`src/Covenant.Host/Covenant.Host.csproj:50-58`, `Covenant.sln`)
(a) `Covenant.Ui` is a solution project (`Covenant.sln` Build.0 entries), and the Host target also `Exec`s
`dotnet publish Covenant.Ui -c $(Configuration)`. CI's `dotnet test -c Release` at the root builds both in parallel
against the same `src/Covenant.Ui/obj/Release/net10.0/`. That produces intermittent file-in-use or corrupt
intermediate failures.
(b) `publish -o obj/console-publish/` never empties the directory. Every UI change leaves old fingerprinted
`_framework/*` files behind, and the glob embeds all of them, so the binary bloats and old assets stay servable.
(c) The target has no Inputs/Outputs, so every Host build (including each test build) re-publishes the UI.
Fix:
- Add `<RemoveDir Directories="$(ConsolePublishDir)" />` before publishing.
- Replace `Exec` with `<MSBuild Projects="..\Covenant.Ui\Covenant.Ui.csproj" Targets="Publish" Properties="Configuration=$(Configuration);PublishDir=$(ConsolePublishDir)" />`. Either add a `ProjectReference ... ReferenceOutputAssembly="false"` so MSBuild orders it, or remove the Ui `Build.0` lines from the sln.
- Add Inputs/Outputs over `src/Covenant.Ui/**`.
- Add a test or CI assertion that the bundle IS embedded in the non-skip build (see S11).

**B3 — The connection test can be served from cache, so "success then failure" can stay green.** (`src/Covenant.Ui/Logic/ConnectionLogic.cs:14-15`, `Pages/Connections.razor:458-468`)
The governed test prompt is a constant. With `Cache:TtlSeconds > 0`, the second run (after Ollama is stopped) is a
cache hit. `CacheStage` runs before the provider, so no provider call is made and `ProviderHealth` is not updated.
The UI then shows "Served from cache" and the header keeps "Local route: ready". This breaks acceptance 06.5 and the
explicit "residual stale-success problem". Fix:
- Append a per-run nonce, as Scenario D already does (`ScenarioPresets` `[run {Guid:N}]`).
- When the record says `CacheHit`, have the step-7 result say "Cache hit — not a reachability result" (as for paused).
- Add a `ConsoleLogicTests` case asserting two test prompts differ.

**B4 — A client disconnect mid-stream is recorded as an upstream failure and marks the provider failed.** (`src/Covenant.Adapters/ProviderLayer.cs:70-89`)
The sink catch only applies `when (!ct.IsCancellationRequested)`. When the browser goes away, `RequestAborted` is
already cancelled. If the write throws a non-OCE (for example `IOException` or `ConnectionResetException`), it falls
to the outer `catch (Exception ex)`. That produces `DenialKind=UpstreamFailure` in tamper-evident evidence and calls
`health.RecordFailure`, which turns the header and Overview to "degraded" across all pages. The author acknowledges
this (console-progress "Unverified / remaining risks"), but it violates the honest-evidence and honest-health
requirements and can be triggered by closing a tab. Fix:
- Wrap the sink call in its own try with no `when` filter, and classify any exception from `sink(...)` as
  `DenialKind.Cancelled` with no health record.
- Keep `catch (Exception ex)` only around `MoveNextAsync` of the provider enumerator: iterate manually with
  `await using var e = ...GetAsyncEnumerator(ct)` so the two exception sources are separable.
- Add a pipeline test whose sink throws `IOException` after cancelling the token: assert `Cancelled` and no
  `RecordFailure`.

### Should-fix

**S1 — Every `_framework/*` file is cached immutable whether or not it is fingerprinted.** (`src/Covenant.Host/ConsoleAssets.cs:87`)
`index.html` loads `_framework/blazor.webassembly.js` by its plain name, and `OverrideHtmlAssetPlaceholders`/importmap
are not used. So whether runtime and app files are content-hashed depends on SDK defaults, which nobody has checked
here. If any non-fingerprinted file (for example `Covenant.Ui.wasm` or `dotnet.native.wasm`) is cached for a year,
an appliance upgrade gives a new boot config with stale cached assemblies. The result is integrity failures and a
dead console until the browser cache is cleared. Fix:
- Mark immutable only names matching a fingerprint pattern (for example `\.[0-9a-z]{8,}\.(js|wasm|dat|json)$`) or
  listed in the publish `*.staticwebassets.endpoints.json`.
- Default to `no-cache` (ETag revalidation is already implemented, so it costs little).
- Verify against the real publish output before shipping.

**S2 — The local model is implicitly priced $0, which makes the ADR-0009 §5(a) startup gate dead code.** (`src/Covenant.Host/Program.cs:187-207`)
`priceMap[localModelId] = (0m, 0m)` is always present, so `routableModels` never fails, as the author notes. ADR-0009
calls a zero price "explicit", but here it is a default. An operator who points `Local:Endpoint` at a billable
OpenAI-compatible service gets "zero_priced", and those calls escape the budget silently. Fix: drop the default
local price. Either require `Pricing:<Local:ModelId>` (0 allowed) whenever a local route is registered, or keep the
default but record `CostBasis="zero_priced_default"` and surface it as "assumed $0" in Budgets. Add a
StartupValidation test.

**S3 — Unknown and estimated cost are under-reported in the aggregates the UI shows.**
- `Overview.razor:86-88` shows `TotalCostUsd` with the hint "Reported tokens × configured prices". The total actually
  includes chars/4 estimates and counts unknown rows as $0. `FinOpsSummary.unknown_cost_requests` and
  `estimated_cost_requests` (`StatusContracts.cs:84-87`) exist but are never deserialized by the UI (no reference in
  `src/Covenant.Ui`).
- `Budgets.razor:186-187` recounts on the client from at most 1000 items. It counts only `unpriced` and
  `estimated_from_text`, missing `unknown_failed_call` and `estimated_failed_call`, and labels estimated rows
  "Unknown-cost".
- `EvidenceReport.total_cost_usd` (`Evidence.cs:175`) has no unknown or estimated counts, so the exported evidence
  presents unknowns as $0.
Fix: add both counts to the UI `StatusReport` contract and to `EvidenceReport`. Show "≈ $X incl. N estimated · M
unknown (not included)" wherever a total appears, and delete the client recount.

**S4 — No test proves local-only mode registers no cloud client.** (`tests/Covenant.Tests/LocalOnlyModeTests.cs:44-52`)
The factory replaces `IChatClientRegistry` with one that contains OpenAI tripwires. The `if (!localOnly)` guard in
`Program.cs` around client construction is therefore untested: deleting it leaves the suite green. Fix: add a factory
without `ConfigureTestServices` and resolve the real `IChatClientRegistry`. Assert `TryResolve("openai", "gpt-4o-mini")`
and `TryResolve("anthropic", …)` are false, and that `local:<tag>` is true.

**S5 — Probe allow-list hardening and tests.** (`src/Covenant.Host/ConsoleBackend.cs:72-134`)
Every bypass form I checked fails closed: case is normalised by `Uri`, and trailing dot, default port 80/443 and
`0.0.0.0` do not match unless listed. Decimal or hex IPs normalise to the same address, so they are the same
destination. Userinfo, query, fragment and redirects are refused; the proxy is off. Remaining issues:
- (a) `ConnectCallback` compares `context.DnsEndPoint.Host` with `Uri.Host`. For IPv6 literals (`[::1]` vs `::1`) and
  IDN hosts (Unicode vs punycode) this is a functional false refusal. Compare against `Uri.IdnHost` with brackets
  stripped.
- (b) Hostname entries trust DNS at connect time (rebinding). Resolve once, check the resolved address against
  IP-literal entries, then connect to that IP — or document "IP entries only" for strict deployments in ADR-0010.
- (c) `BuildAllowList` defaults a port-less entry to 11434 for any scheme. The doc comment at line 71 says "defaults
  by scheme". Fix one or the other.
- (d) Add tests for: trailing-dot host, upper-case host, `http://localhost/v1` (port 80) against a `localhost:11434`
  entry, `[::1]`, `0.0.0.0`, `2130706433`, an IDN host, and a path like `/a/../../x`. All must be refused or proven
  to hit only the listed authority.
- (e) The refused result echoes the raw input, including any userinfo, back to the caller (`ConsoleBackend.cs:106`).
  Echo the normalised authority instead.

**S6 — Evidence lookups and anchor status hide integrity failures.**
- (a) `/admin/requests/{id}` (`Program.cs:730`) discards the verification result. After a chain break only the
  verified prefix is returned; after an anchor contradiction nothing is returned. Every lookup then answers
  `404 not_found`, and the UI says "not in the current audit segment yet — retry". Return `chain_valid` (and
  `first_invalid_line`) in the 404 body, or answer 409 with an explicit "verification failed — record not trusted"
  state, and render that in `EvidenceTracker`/Requests.
- (b) `AuditChainVerifier` skips anchor comparison when `File.Exists(anchorPath)` is false (`Evidence.cs:68`). The UI
  then shows "Configured · no anchor written yet" (`Evidence.razor:192`) even when `entries ≥ anchorEvery`. That
  means the anchor file was deleted, which is exactly what anchoring should detect. ADR-0007 permits this, but the
  console must not present it neutrally. Flag it red when configured, `entries ≥ every` and `count == 0`, and expose
  `anchor_file_present`.

**S7 — Lookup cost is unbounded.** (`Program.cs:703,730`)
Every `/admin/requests` and `/admin/requests/{id}` call re-reads and re-hashes the whole current segment. That is up
to 7 polls per Playground run, plus `/admin/status/stream` every 2 s per console. TASK 02 asks for "lookup
retention/snapshot limits explicitly". Fix: keep an in-memory index `id → entry` fed by `FileAuditSink` after each
durable write (bounded ring, for example the last 10k). Fall back to a file scan only on miss, and document the bound
in `ui-api-contract.md`.

**S8 — Third-party error text is persisted into the tamper-evident record.** (`ProviderLayer.cs:88,117`)
`Bound(ex.Message)` keeps up to 240 characters of provider and SDK messages in `Reason`, which is permanent and
exported. Some providers and gateways echo request fragments in errors (validation and content-filter messages). It
is bounded, which is better than before, but it still lets unvetted text into evidence. Fix: record a fixed taxonomy
(exception type, HTTP status if `HttpRequestException.StatusCode`, provider error code). Keep the message only in the
non-persisted response or log.

**S9 — Anthropic dialect.** `AnthropicWire.MapDenial` (`AnthropicContracts.cs:138-145`) maps `Cancelled` to
403 `permission_error`. It should map to a non-permission type (for example `api_error`, 499). `AnthropicDenial`
(`Program.cs:552-559`) and the stream `error` event carry no request id in the body; only the header has it, while
the OpenAI path puts `request_id` in both. Add it for parity.

**S10 — "Raw JSON" is not raw.** (`src/Covenant.Ui/Pages/Requests.razor:252,456`)
It is a re-serialization of `AuditEntryView` with nulls omitted. Either label it "Record (as parsed by the console)",
or return and show the stored line content (and its entry hash) from the Host, so "Raw JSON agrees with visible
facts" means agreement with the evidence itself.

**S11 — Console asset tests pass when the console is missing.** (`tests/Covenant.Tests/ConsoleAssetsTests.cs:158,168,182` and siblings)
Every embedded-path test `return`s when `!Embedded`, so a broken embedding target goes green. No test fetches a
`.wasm` from the manifest (`Framework_js_when_present_is_javascript_and_wasm_is_wasm` never requests a wasm). The
literal `_framework/blazor.webassembly.js` assertion will break if .NET 10 fingerprints it. Fix:
- Add a `[Fact]` that asserts `Embedded` unless an env var or MSBuild-generated constant says `SkipConsolePublish`.
- Enumerate `GetManifestResourceNames()` and assert at least one `.wasm` is served as `application/wasm`.
- Find the loader script by parsing `index.html` rather than hard-coding its name.

**S12 — Prototype directory in the repo root.** `covenant-prototype/` (724 KB, incl. `runtime.js` and pages with
external URLs) is untracked at the root. The brief says not to ship the prototype runtime, and provenance requires
every committed artifact to be accounted for. Add it to `.gitignore` or move it outside the repo.

**S13 — No evidence link when cancelled before headers.**
`PlaygroundState.RunAsync` only tracks evidence when `RequestId` is known. Buffered responses and lazily committed
SSE headers mean a cancel before the first byte never yields the id, although the server did record a `Cancelled`
entry. The UI must say "Covenant recorded this request, but its id never reached the console; find it in Requests
by time and team (not linked automatically)". It must never auto-select "most recent". Consider committing SSE
headers (with `X-Covenant-Request-Id`) immediately after the pipeline passes policy, so streaming runs always have
the id.

### Nits

- **N1** ADR numbering skips 0008; add a one-line note or renumber before merge.
- **N2** `Settings.razor:157` cites `Routing:ComplexityThresholdTokens`; the real key is
  `Routing:ComplexityTokenThreshold` (`Program.cs`).
- **N3** In local-only mode `routableModels` (`Program.cs:204`) still requires prices for the unrouted cloud models.
  It is harmless today because they have defaults, but it is the wrong set. Build the list from
  `policy.AllowedRoutes` instead.
- **N4** `InferenceContext.Deny` takes `CurrentStage` when no stage is given. Because stages are nested and
  `CurrentStage` is only set on entry, a cancellation caught by `AuditStage` is attributed to the innermost stage
  entered (for example `attribution`), not necessarily the stage that was waiting. Record `"provider"` only when
  `AttemptedModel` is set and the response is null, or leave it null.
- **N5** After `ProviderCallStage` records `Cancelled` for a client disconnect, the streaming handlers still try to
  write an `error` event to the dead connection (`Program.cs` OpenAI and Anthropic stream tails). That produces a
  noisy unhandled exception. Skip the write when `DenialKind == Cancelled`.
- **N6** `ProviderHealth` records failures for client-caused 4xx from the provider (bad `max_tokens`) as provider
  failures. Consider not degrading health on 4xx.
- **N7** `StatusHeader.razor:12` paints "Console connected" green. That is fine as an admin-API fact, but while stale
  it keeps showing the last known "New provider calls accepted" with no stale marker on that indicator.
- **N8** `docs/console-progress.md` "Unverified / remaining risks" says local-only FinOps still uses a cloud baseline,
  but `Program.cs` now passes `localModelId` as the baseline in local-only mode. Update the doc.

## What is good (verified by reading)

- Correlation id: generated in middleware before body binding (`Program.cs:303-312`), never taken from the caller,
  identical across header, body `id`, SSE chunk `id`, error `request_id` and audit `Id`/`CorrelationId`. Malformed
  JSON is defined as "header, no record".
- The audit write uses `CancellationToken.None` (`Audit.cs`), proven by `StrictSink`. Cancellation is recorded once,
  as `Cancelled`, with an estimated charge that the ledger and replay both see.
- Legacy compatibility follows from the design: the chain hashes stored bytes, the fields are additive and nullable,
  and the UI renders "Not recorded".
- Classification default stays restrictive for enforcement; `ClassificationEvaluated` is set only by `ClassifyStage`.
- Credential separation: `AdminApi` alone holds the admin token; `InferenceApi` uses its own `HttpClient` and only
  the in-memory application key. No admin endpoint returns `Local:ApiKey`, the OpenAI key, the admin token or
  application keys: the catalog shows only `scheme://host:port/path`, and status shows a key count only.
- CSP `script-src 'self' 'wasm-unsafe-eval'` matches .NET 8+ Blazor WASM needs, and there is no inline script in
  `index.html`. Admin and `/v1` responses carry no console CSP. The legacy `/admin/ui` remains.
- The UI never parses reason strings for outcomes. Outcomes come from `DenialKind`/`StoppedAtStage`, and legacy
  records get "Refused (legacy record)".
