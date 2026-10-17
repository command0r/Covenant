# Console progress — status record

## Current status (2026-10-16, working-branch recheck)

**Who ran what.** The authoring environment has no .NET SDK: the author has executed nothing. Every result in
this table was produced by an independent reviewer on macOS arm64, SDK 10.0.107. Evidence lives in the
git-excluded `docs/verification-2026-10-16/` folder (`recheck/CLAUDE-RECHECK.txt` for the run below).

**Source fingerprint of what was checked:** staged code subtrees `src` = `dd087f907b000d0fdb2070842527f2d69e758988`, `tests` = `79e65c587c846f70044fa82bc4efb6e216eb5d94` (on top of
`main` 0624887). Recompute with `T=$(git write-tree); git rev-parse $T:src $T:tests`. If either hash differs,
code has changed since this check, and the results below must be re-run before they're cited. Docs-only changes
don't affect these hashes.

### Executed on the working branch `feat/console` (after the three fixes below)
| Check | Result |
|---|---|
| `dotnet build Covenant.sln -c Release --disable-build-servers` | PASS — 0 warnings, 0 errors |
| `dotnet test tests/Covenant.Tests -c Release --collect:"XPlat Code Coverage" --settings tests/coverlet.runsettings` (console embedded) | 309 passed, 0 failed, 0 skipped · line coverage **0.9362** (CI gate 0.80) |
| Same tests with `-p:SkipConsolePublish=true` and isolated `--artifacts-path` | 309 passed, 0 failed, 0 skipped |
| `dotnet publish src/Covenant.Host -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true` | PASS |
| Published binary, GET `/ui` | 302 → `/ui/` |
| Published binary, GET `/ui/` | 200 text/html, `<base href="/ui/">`, CSP header present |
| Published binary, GET `/ui/requests/x` (nested-route reload) | 200 text/html |
| Framework loader, `.wasm`, CSS, local font | 200 with correct MIME types; missing framework asset → 404; conditional GET with ETag → 304 |
| API demo against real Ollama `qwen2.5-coder:7b`, `Providers:Mode=local-only`, no cloud credentials, isolated port/audit file | A served locally · B 403 policy, no provider attempted · C classified Pii, served locally · no key 401, classification not evaluated (stopped at auth) · paused 403, no attempt · resumed: streaming 200 ending `[DONE]` · application key cannot read `/admin/status` (401) · admin token cannot authorize inference (401) · every response resolves to its exact audit record by id · 5 concurrent identical prompts → 5 unique ids, 5 exact lookups · chain_valid true · local cost basis `zero_priced` |

Route checks use GET, not HEAD: these are `MapGet` endpoints, so `curl -I` (HEAD) returns 405, which proves
nothing about GET. Use `curl -sS -D - -o /dev/null http://localhost:5100/ui` (don't follow redirects; inspect
`Location`), and the same for `/ui/` and `/ui/requests/x`.

### NOT executed — not covered by any sign-off
- **Browser acceptance:** signed-in rehearsal; reload; presentation mode at 1280×720 and 1920×1080 in both themes;
  A–D from the UI; the Requests drawer with keyboard and focus restoration; disconnect/reconnect; stale state;
  screenshots.
- **Failure paths in a real browser:** real cancellation from the browser, and a controlled provider failure
  after a success (use an isolated mock endpoint, not the shared Ollama service).
- **Other publish targets:** linux-x64 and NativeAOT.
- **Overall conference-demo readiness.** A passing build plus an API demo does not establish that the browser
  presentation is ready.

### Fixes applied after the first (diagnostic-copy) review
1. `src/Covenant.Ui/Pages/Policies.razor` — `AdapterStatus(Covenant.Ui.Models.RouteView r)` (CS0104; Blazor's
   `RouteView` component in App.razor untouched).
2. `src/Covenant.Host/Covenant.Host.csproj` — EmbeddedResource `Include` then `Update` assigning `LogicalName`
   per file (CS1508).
3. `src/Covenant.Host/ConsoleAssets.cs` — a single `/ui/{**path}` endpoint that redirects only when the path is
   exactly `/ui`, so `/ui/` serves `index.html`. The literal `/ui` route had also matched `/ui/`, causing a
   redirect loop. No test assertion was weakened.

Superseded below: the packaging description mentioning `dotnet publish … -o obj/console-publish` (replaced by the
in-build `MSBuild` task plus `CleanConsolePublishDir`) and every "Not verified (no compiler)" section; those are
history. The tables above are the record of what has been executed.

---

# History — TASK 01 (shell & packaging), TASK 07 read-only pages, TASKS 04/05 + UI half of 06

Status as of 2026-10-16. Written without a .NET SDK on the authoring machine: **nothing below has been compiled
or run yet** — see "Not verified" before trusting any of it.

## What was built

**Packaging (ADR-0004).** `src/Covenant.Ui` is a standalone Blazor WebAssembly project. `Covenant.Host` does not
reference it; instead the `PublishConsole` MSBuild target in `Covenant.Host.csproj` runs
`dotnet publish src/Covenant.Ui -c $(Configuration) -o src/Covenant.Host/obj/console-publish` before
`PrepareForBuild` and adds every file under `obj/console-publish/wwwroot/ui/**` as an `EmbeddedResource` with
`LogicalName=console/<relative path>` (published Blazor output lands under `wwwroot/<StaticWebAssetBasePath>/`,
i.e. `wwwroot/ui/`). `ConsoleAssets.MapConsole()` loads those resources once at startup and serves them under
`/ui/`: `/ui` → 302 `/ui/`; known file → bytes with the right MIME type, ETag (304 on `If-None-Match`),
`Cache-Control` (`no-cache` for `index.html` and the loader entry points, `immutable` for fingerprinted
`_framework/*` and the woff2 fonts); anything without a file extension → `index.html` (nested-route reload);
file-like path that does not exist → 404 (so a missing asset fails loudly instead of returning HTML to the
loader). Every console response carries a `self`-only CSP, `X-Content-Type-Options: nosniff` and
`Referrer-Policy: same-origin`. With `-p:SkipConsolePublish=true` nothing is embedded and `/ui/` answers 503
with a plain-text explanation. The Host stays minimal-API + RDG; no reflection, no MVC, no static-files
middleware. `Program.cs` gained exactly one line: `app.MapConsole();`.

**Shell.** Compact left nav (Overview, Playground, Requests, Models & connections, Policies, Budgets, Evidence;
Settings + connection control + theme toggle at the bottom), slim status header (environment, "Console
connected/disconnected", "Updated n s ago" / "Stale" / "Connection lost — retrying", "New provider calls
paused/accepted", Pause/Resume button), amber "New provider calls paused" banner with reason and scope text.
Dark/light via `data-theme` on `<html>`; only the theme is persisted (localStorage `covenant.theme`).

**Services** (`Services/`): `AppState` (pure state + `Changed` event; admin token in memory, application key in
memory only, `Connected`, `LastStatus`, `LastUpdatedUtc`, `IsStale` (>8 s), `Paused`, `Environment`, catalog,
feed mode/error, `Restoring`), `AdminApi` (typed `/admin/*` calls with `X-Covenant-Admin-Token`; 401 →
`Unauthorized` event → disconnect + `/connect` "Token rejected"; error bodies parsed into readable messages),
`StatusFeed` (SSE via `HttpClient` + `SetBrowserResponseStreamingEnabled` + `ResponseHeadersRead`, `data:` line
parsing, backoff reconnect, polling fallback after two stream failures, 1 s staleness tick),
`ConsoleSession` (connect/validate/restore/disconnect, sessionStorage mirror of the token under
`covenant.admin-token`, pause/resume with server re-sync, catalog load), `Interop` (typed wrapper over
`wwwroot/js/interop.js`), `Fmt` (invariant formatting), `RouteHealth` (unresolved / missing local route from
`status.routes` × `catalog`).

**Components** (`Components/`): `StatusPill` (served/refused/failed/paused/unknown; `KindFor(effect, denialKind)`
never defaults to green), `Button`, `Drawer` (drawer or dialog; Escape/scrim close, JS focus trap, focus restored
to `OpenerId`), `FactList`, `ErrorBanner`, `EmptyState`, `Skeleton`, `AlertCard`, `RequiresConnection` (gate:
restoring → skeleton, disconnected → "Connect to the console", else content).

**Pages** (`Pages/`): `/connect`, `/` Overview, `/policies`, `/budgets`, `/evidence`, `/settings` as specified in
the task (alerts first on Overview; segment label on totals; activity labelled with its real window; chain
status with "Internal consistency verified…" wording; Configured ≠ Verified for anchors; Export downloads the
server's JSON byte-for-byte via a Blob; Settings read-only values + separate Maintenance with a typed
environment-name confirmation that names both effects and is blocked while chain verification fails).
`Playground.razor`, `Requests.razor` and `Connections.razor` were placeholders after TASK 01; they are now
implemented — see "TASKS 04 / 05 / 06 (UI)" below.

No fixtures, canned values, fake hashes or default-green states were ported from `covenant-prototype/`; only
`tokens.css`, the fonts (SIL OFL 1.1, recorded in `docs/PROVENANCE.md`) and layout/copy.

## Files

```
Covenant.sln                                   + Covenant.Ui project (GUID 4F1C2B7E-9D3A-4B86-A2E5-6C0D8F7B1E42), src folder
src/Covenant.Host/Covenant.Host.csproj         + PublishConsole target, EmbeddedResource glob
src/Covenant.Host/ConsoleAssets.cs             new — MapConsole, MIME map, NormalizePath, CacheControlFor, Load
src/Covenant.Host/Program.cs                   + app.MapConsole(); (one line before app.Run())
src/Covenant.Ui/Covenant.Ui.csproj             new
src/Covenant.Ui/Program.cs, App.razor, _Imports.razor
src/Covenant.Ui/Layout/{MainLayout,BareLayout,SideNav,StatusHeader}.razor
src/Covenant.Ui/Components/{StatusPill,Button,Drawer,FactList,ErrorBanner,EmptyState,Skeleton,AlertCard,RequiresConnection}.razor
src/Covenant.Ui/Services/{AppState,AdminApi,StatusFeed,ConsoleSession,Interop,Fmt,RouteHealth}.cs
src/Covenant.Ui/Models/Contracts.cs            DTOs + UiJsonContext (source-generated)
src/Covenant.Ui/Pages/{Connect,Overview,Policies,Budgets,Evidence,Settings,Playground,Requests,Connections}.razor
src/Covenant.Ui/wwwroot/index.html, css/tokens.css, css/app.css, js/interop.js
src/Covenant.Ui/wwwroot/fonts/*.woff2, fonts.css, LICENSE-ibm-plex-{sans,mono}.txt
tests/Covenant.Tests/ConsoleAssetsTests.cs     new — unit (MIME/normalize/cache/CSP) + integration via WebApplicationFactory
docs/PROVENANCE.md                             + two NuGet packages, bundled fonts
docs/console-progress.md                       this file
```

## How to build

- `dotnet build` (solution or `src/Covenant.Host`) — triggers `dotnet publish` of `Covenant.Ui` and embeds the
  bundle. First build restores the Blazor packages; the `wasm-tools` workload is **not** required (no WASM AOT,
  no relinking).
- `dotnet test -p:SkipConsolePublish=true` — fast tests; `ConsoleAssetsTests` detects the absence of the bundle
  and asserts the 503 branch instead.
- `dotnet test` (without the flag) — also exercises the embedded bundle: 200 `text/html` for `/ui/`, nested
  route reload, `font/woff2`, `text/javascript`, ETag/304.
- `dotnet run --project src/Covenant.Host` then open `http://localhost:5100/ui/`. The UI calls the same origin
  (`/admin/*`), so no dev-server/proxy is involved; the DevServer package is only a dev-time dependency.

## Not verified (no compiler here) — check these first

1. **Razor/C# compiles under warnings-as-errors.** Written conservatively, but the obvious risk spots are:
   nested `if` inside C# blocks vs `@if` inside markup (checked by hand), pattern variables in `@if (... is not { } s)`,
   collection expressions targeting `IReadOnlyList<Fact>` / `List<Row>` with target-typed `new(...)`,
   `EventCallback` bound to `Task`-returning method groups, `@bind-Open` on `Drawer`.
2. **MSBuild embedding.** `%(RecursiveDir)` inside a target-scoped `ItemGroup`, `WithCulture="false"`, and the
   `BeforeTargets` list (`PrepareForBuild;AssignTargetPaths;PrepareResourceNames;PrepareEmbeddedResources` —
   the last is a no-op if that target does not exist). If `CoreCompile` sees no resources, move the target to
   `BeforeTargets="BeforeBuild"` and confirm `_GenerateCompileInputs` runs after it. Verify the publish layout is
   `obj/console-publish/wwwroot/ui/...` (docs say so for `StaticWebAssetBasePath`); if it is `wwwroot/...`,
   change the glob.
3. **Package versions.** `Microsoft.AspNetCore.Components.WebAssembly[.DevServer]` 10.0.11 exist on nuget.org
   (checked 2026-10-16 via the flat-container index). `CompressionEnabled=false` is the .NET 8+ property name.
4. **SSE over `HttpClient` in the browser** (`SetBrowserResponseStreamingEnabled(true)` +
   `HttpCompletionOption.ResponseHeadersRead` + `StreamReader.ReadLineAsync(ct)`). Polling fallback kicks in
   after two failures if the browser buffers the response.
5. **Blazor + CSP.** `script-src 'self' 'wasm-unsafe-eval'`, `style-src 'self' 'unsafe-inline'` should satisfy the
   .NET 10 loader (`dotnet.js` is imported from `_framework/`, no inline script). If the loader needs
   `blazor.boot.json`/`dotnet.boot.js` revalidation, both are already `no-cache`.
6. **Freshness UX**: the 1 s `System.Threading.Timer` in `StatusHeader` + `AppState.Tick` in `StatusFeed` — confirm
   no double re-render storms in the browser.
7. **Environment name in confirmations.** Until Task 07's `environment` lands in `/admin/status`, the Settings
   confirmation asks you to type `Environment not reported`. Expected to resolve when the field ships.
8. **Drawer focus trap** (`interop.js trapFocus`) — exercise with keyboard: Tab cycles inside, Escape closes,
   focus returns to the opener.

## Decisions worth knowing

- Disconnect never calls `/admin/kill-switch`; pause/resume always re-fetch `/admin/status` after the POST and
  render the server's answer, never the optimistic one.
- "Current audit segment since …" on Overview/Settings uses the oldest activity bucket (status has no
  `first_utc`); Budgets uses `segment.first_utc` from `/admin/requests` when that endpoint exists.
- Evidence anchor status reads `anchor_*` from `/admin/evidence` first, then `status.anchor`; any missing field
  renders "Not reported", never "Verified".
- The legacy `/admin/ui` vanilla page is untouched; retire it in a later change once the console is proven.

## Backend (Tasks 02/03/06)

Design: ADR-0009 (correlation, audit v2, honest accounting), ADR-0010 (local-only mode); wire shapes in
`docs/ui-api-contract.md`. Nothing here was compiled or run in the authoring environment (no SDK) — the
first `dotnet build && dotnet test` is the real gate.

### Endpoints
- Every `/v1/*` response carries `X-Covenant-Request-Id: req_<32 hex>` (middleware, set before body binding).
  Same value = OpenAI `id` / Anthropic message `id` / SSE chunk `id` / `request_id` on error bodies / audit `Id`
  (+ `CorrelationId`). Malformed JSON → 400 with the header and **no** audit record (never reached the pipeline).
- `GET /admin/requests?limit=N` — current segment, newest first, `limit` clamped to 1..1000 (default 200).
- `GET /admin/requests/{id}` — exact lookup; 400 malformed id, 404 `{error:"not_found", scanned}` (sink is async:
  poll before concluding "unavailable").
- `GET /admin/catalog` — mode, per-route registered / price basis / observed health, local config, probe allow-list.
- `POST /admin/providers/probe` — allow-listed, http(s) only, no userinfo/query/fragment, redirects refused,
  5 s overall deadline (connect + headers + body), 64 KiB cap, no credentials forwarded. Probing the applied
  `Local:Endpoint` updates its endpoint health (failure supersedes success; last success kept).
- `/admin/status` adds `environment, mode, audit_path, cache_ttl_seconds, prompt_preview_chars, anchor, version`;
  FinOps adds `unknown_cost_requests, estimated_cost_requests`, recent rows add `cost_basis`;
  `/admin/evidence` adds `anchor_configured, anchor_count, last_anchor_utc`.

### Review fixes (this pass)
- `ProviderCallStage`: only a *caller* cancellation (`ct.IsCancellationRequested`) is rethrown; a provider-side
  `OperationCanceledException` (HttpClient timeout) is now a governed `UpstreamFailure` (502, charged estimate)
  instead of escaping as an unhandled exception recorded as "Cancelled".
- `ProviderProbe`: overall deadline now also bounds the body read (a server that sent headers then stalled could
  hold the admin request open); `IOException` mid-body is a probe failure, not a 500; overload
  `RunAsync(raw, allowList, timeout, ct)` for tests.
- `/admin/catalog` resolves `IChatClientRegistry` from `RequestServices` instead of relying on RDG parameter
  inference for an interface type on a GET; `/admin/requests` uses `Enumerable.Reverse(entries)` explicitly.
- Provider-mode startup rules extracted into `StartupValidation.ValidateProviders(IConfiguration)` (pure,
  behaviour-preserving) so refusal cases are unit-tested without `Environment.Exit`.
- `StartupValidation` additionally refuses a whitespace-only `Local:ModelId` (previously it would register `local:` with an empty tag).
- `CanonicalModel.cs`: duplicate `<summary>` on `InferenceResponse` and a merged trailing comment on
  `InferenceRequest` cleaned up.

### Tests added (tests/Covenant.Tests)
- `CorrelationAndEvidenceTests.cs`
  - `CorrelationAndEvidenceTests` (host, stub registry): header == body id == audit record for buffered 200,
    Anthropic 200, 401 (stage `auth`, `ClassificationEvaluated=false`, classification still `Phi`), policy 403,
    pause 403 (stage `pause`, provider not called), missing local adapter 403 (stage `provider`, selected
    `local-model-not-configured`, attempted null), upstream 502 (attempted set, `estimated_failed_call` > 0),
    mid-stream failure (HTTP 200, SSE error line with `request_id`, record Deny/UpstreamFailure), malformed JSON
    (400 + header, no record — proven after a later record lands); 20 concurrent identical prompts → 20 distinct
    ids each resolving to its own record; lookup/list admin-only; bad id 400; unknown id 404 + `scanned`; list
    newest-first + limit clamping; no prompt text in records/log/list without `Audit:PromptPreviewChars`.
  - `CorrelationRateLimitTests` (own host, `RateLimit:GlobalPerMinute=1`): 429 correlated, stage `rate_limit`.
  - `CancellationEvidenceTests` (pipeline): cancelled in-flight call → exactly one record, `Cancelled`, attempted
    model, `estimated_failed_call`, cost > 0 and charged, enqueued via a sink that rejects any cancellable token
    (proves `CancellationToken.None`); provider-side timeout → `UpstreamFailure`, not `Cancelled`.
  - `LegacyAuditCompatibilityTests`: hand-written v1 lines (no v2 keys) + v2 lines via `FileAuditSink` with
    anchors → chain + anchors verify, legacy bytes unchanged, legacy v2 fields read as null, ledger replay
    includes the estimated failed-call charge, evidence export reports anchors.
- `AccountingTests.cs` (real stages, stub clients): reported / missing usage (`estimated_from_text`, charged,
  buffered + streamed) / partial usage (not reported) / local `zero_priced` / refusal `no_call` / cache hit
  `no_call` charged once / upstream failure `estimated_failed_call` exact estimate charged / interrupted stream
  estimate includes partial output / unpriced → `unpriced` + FinOps unknown count + no saving / failed call on
  unpriced → `unknown_failed_call`; FinOps: unpriced/estimated never produce savings while the same record as
  `reported` does; legacy (null basis) keeps old treatment; `TokenEstimate` ceiling.
- `ProviderProbeTests.cs` (TcpListener loopback server): valid discovery, no credential headers forwarded, empty
  discovery, refused port, silent server timeout (400 ms overload), non-JSON / wrong shapes, non-2xx, 302 not
  followed (Location never requested), oversized body with and without Content-Length, not-allow-listed and
  unsafe shapes (userinfo, query, fragment, ftp, file, relative, empty) refused with zero connections, allow-list
  building, `ProviderHealth` success→failure→success, and host-level: applied-endpoint probe ok → failed updates
  `/admin/catalog` `endpoint_health` while keeping `last_success_utc`; probe is admin-only.
- `LocalOnlyModeTests.cs`: host in `local-only` with no cloud key starts; status mode `local-only`, all routes
  local; Internal/PII/PHI served by `local:qwen3:8b` with cloud tripwire clients at 0 calls; explicit cloud model
  request is denied at policy (not rerouted); catalog mode/local registered/no openai models/price `zero`;
  `/v1/models` lists only the tag. `StartupValidationTests`: cloud needs key; local-only needs endpoint + tag,
  refuses OpenAI/Anthropic keys; endpoint without tag refused in any mode; non-http/invalid/blank endpoint
  refused; unknown mode refused; mode normalization.

### Unverified / remaining risks
- Not compiled here. Watch especially: xunit analyzer severities, RDG handling of `int? limit`, and the
  `ConnectCallback` dual-mode socket on the CI OS.
- In the **Development** environment minimal APIs throw on bad JSON and the developer exception page clears
  response headers, so the 400 loses `X-Covenant-Request-Id` there (Production — the shipped mode and all
  tests — keeps it).
- Exceptions thrown by the Host's SSE writer (client disconnect surfacing as `IOException`) are inside the
  provider stream `try` and would be recorded as an upstream failure and mark provider health failed.
- ADR-0009 §5(a) "startup refuses an unpriced routable model": the built-in price map always contains every
  routable model, so that gate cannot currently fire; the runtime `unpriced` flag is the effective protection.
- In local-only mode FinOps still computes "savings" against the (unrouted) cloud strong model as baseline.
- Probe allow-list entries are host names; the connect callback re-resolves DNS, so a hostname entry trusts DNS
  (rebinding) — prefer IP entries for strict deployments.
- The `TcpListener`-based timeout/refused tests depend on loopback behaviour (refused port relies on nothing
  rebinding the just-released ephemeral port).


---

# TASKS 04 / 05 / 06 (UI half) — Playground, Requests, Models & connections

Status 2026-10-16. Same caveat: **written without a .NET SDK — not compiled, not run.** Backend files were not touched
(another engineer owns Core/Governance/Adapters/Host/tests except the new test file).

## What was built

**Pure view-model logic — `src/Covenant.Ui/Logic/`** (plain C#, only `System.*` + `Covenant.Ui.Models`; linked into
`tests/Covenant.Tests` via `<Compile Include … Link="UiLogic\…">` together with `Models/Contracts.cs`):
- `OutcomeModel.cs` — `DisplayOutcome` {Served, ServedFromCache, PolicyDenied, Paused, AuthenticationFailed, RateLimited,
  ProviderUnavailable, ProviderFailedMidStream, Unsupported, RejectedBeforeGovernance, Cancelled, Unknown};
  `OutcomeModel.Compute(OutcomeInput)` → title, plain explanation, status line, `TransportDiffers` (HTTP 200 but not a
  success → "Transport 200, final outcome: failed/incomplete"), pill kind, `AwaitingRecord`. Before the record: transport
  only (403 → "Refused (HTTP 403) — awaiting evidence for the stage", never a guessed stage). With the record:
  `DenialKind` first, then `StoppedAtStage` ("pause" → Paused; "provider" with `AttemptedModel==null` → missing adapter;
  "budget"/"policy" → governance denial); legacy deny without kind/stage → "Refused (legacy record)". Reason text is
  never parsed. Also `PipelineStages` (configured order; pause+budget share one row).
- `ModelFacts.cs` — Selected / Provider attempted / Served (cache → "Served from cache (no provider call)"), legacy nulls
  → "Not recorded", classification "Not evaluated — stopped at {stage}" / "Not recorded" / class + signal.
- `CostText.cs` — CostBasis wording exactly as specified; unknown/unrecognised basis → "Not reported" (never "$0");
  tokens "Not reported" whenever a provider call was attempted and `UsageReported != true`.
- `RequestFilter.cs` — immutable `RequestFilterCriteria` (outcome, model matching selected/attempted/served, team, id
  substring, time window) + option lists.
- `RequestSnapshot.cs` — `PlaygroundForm` (mutable inputs) and the immutable `RequestSnapshot` frozen at Run
  (sequence, "Scenario X · title" or "Custom", sent prompt incl. nonce, base prompt, requested model or null = "Let
  policy choose", streaming, max tokens, labels, time); `MatchesForm` drives "Previous request — the form has changed since".
- `ScenarioPresets.cs` — A permitted; B explicit `gpt-4-unapproved-test` (test input); C synthetic sensitive fixture
  (published sample SSN 078-05-1120 — matches `RegexDataClassifier`'s PII rule, verified by test); D normal prompt +
  `" [run {Guid:N}]"` appended at every Freeze. Presets only fill inputs.
- `ConnectionLogic.cs` — endpoint validation/normalisation (catalog shape `scheme://host:port/path`), host:port, applied-
  config read-back check, route review text, generated configuration block (names + values, no secrets), governed
  test prompt (contains the sample SSN so cloud mode routes it to the local model).

**Services**
- `InferenceApi` — own `HttpClient` (same origin, 5 min timeout), `Authorization: Bearer <application key>` only when a
  key is set (empty → no header → real 401), never the admin token; `X-Covenant-Workflow/UseCase` labels (printable ASCII
  only). Buffered and SSE streaming (`SetBrowserResponseStreamingEnabled` + `ResponseHeadersRead`, `data:` lines, deltas,
  the mid-stream `{"error":…}` line, `[DONE]`; missing `[DONE]` = incomplete). Request id from `X-Covenant-Request-Id`,
  falling back to the body/chunk id or `request_id`. Cancellation → `Cancelled` ("Stopped waiting … not a rollback").
- `EvidenceTracker` — polls `GET /admin/requests/{id}` after 0.5,1,1.5,2,3,3,3 s; 404 → Pending → "Not in the current
  audit segment yet — retry."; 401 handled by ConsoleSession; other errors → Error + Retry. Never says the write failed.
- `PlaygroundState` (scoped) — form, runs, selection, in-flight run (one at a time), evidence tracking; refreshes the
  catalog after each run; cleared by `AppState.Disconnected`.
- `RequestsViewState` (scoped) — snapshot, filters, selection; cleared on disconnect.
- `CatalogState` (scoped) — 30 s refresh loop while connected + `RefreshAsync()` after governed tests/probes; reuses
  `ConsoleSession.LoadCatalogAsync` / `AppState.Catalog`. `RouteHealth.LocalRoute()` (extended, not duplicated) gives one
  "Local route: ready / degraded (last success …) / not configured / not registered / not yet observed" summary used by
  the header, Overview (new "degraded" alert) and Models & connections. A failure newer than the last success is degraded.
- `AppState.Disconnected` event (raised in `Disconnect` before `Changed`).
- `Interop.CopyTextAsync` + `copyText` in `interop.js` (request ids only).

**Pages**
- `/playground/{Scenario?}` (+ `?present=1`): two columns (setup | result), bottom row "Configured processing order"
  (recorded `StoppedAtStage` highlighted, cache-hit row marked, no timings/animation) | collapsible Evidence for the
  selected run with "Open in Requests". Application key masked, memory-only. Routing: policy / permitted catalog model
  (GET /admin/catalog, classifications > 0) / "Advanced: explicit model id (test input — does not grant permission)".
  Run disabled while in flight, Cancel, Clear output, session history (#n). Presentation mode: fixed full-viewport layer
  (nav/header hidden), own header with environment, provider mode, pause state, Pause/Resume + scope note, Setup drawer,
  Inspect-evidence drawer with "Open in Requests", "Exit presentation mode". Sizes use `--text-outcome-present` /
  `--text-facts-present`; long output scrolls inside `.response-box`.
- `/requests`, `/requests/{Id}`: coverage label from `segment`, filters persisted in `RequestsViewState`, Clear filters,
  empty / no-match states, full-width fixed-layout table (min-width 1120 px, horizontal scroll inside its container,
  ellipsis + `title` cells), row click or the id button (Enter) opens the drawer, URL becomes `/requests/{id}`. Deep link
  always fetches `GET /admin/requests/{id}`; notes "hidden by the current filters" / "not in the loaded recent list";
  404 → "Not in the current audit segment (may be archived or not yet written)" + Retry. Drawer sections 1–5 in the
  specified order; prompt preview only when the record has `PromptPreview`; raw JSON is a re-serialisation of the same
  `AuditEntryView` via `UiJsonContext` (indented, nulls omitted).
- `/connections`: running configuration (mode, local configured/endpoint/model/registered from the server flag only,
  endpoint health, probe allow-list, per-model routes/registered/permitted/price basis/health with last success/failure/
  error, "unknown" when never observed). Wizard as gated `<fieldset>`s: 1 endpoint (edit → clears all later draft results
  with a notice; applied config untouched) → 2 probe (allowed/reachable/status/duration/error; not-allow-listed → add
  host:port to `Providers:ProbeAllowList`) → 3 discovered tags (empty state) → 4 exactly one tag (radio) → 5 route review +
  generated config + "Runtime apply is not available in this version…" → 6 check applied config (re-GET catalog; pass only
  if endpoint, tag and `registered` match) → 7 governed test through `InferenceApi` with the application key (asks for one
  if empty), requested model = tag, outcome + request id + "Open evidence"; paused → "Paused — not a reachability result";
  catalog refreshed after the test and after probes. Optional Open WebUI section (`<origin>/v1`, application key).
- Header: "Local route: …" indicator linking to Models & connections.
- CSS: desktop shell is now app-height (`.shell {height:100vh}`; `#main` scrolls) so Playground can size to the viewport.

## Files (this round)

```
src/Covenant.Ui/Logic/{OutcomeModel,ModelFacts,CostText,RequestFilter,RequestSnapshot,ScenarioPresets,ConnectionLogic}.cs   new
src/Covenant.Ui/Services/{InferenceApi,EvidenceTracker,PlaygroundState,RequestsViewState,CatalogState}.cs                new
src/Covenant.Ui/Services/AppState.cs        + Disconnected event
src/Covenant.Ui/Services/RouteHealth.cs     + LocalRouteState / LocalRouteSummary / LocalRoute()
src/Covenant.Ui/Services/Interop.cs         + CopyTextAsync
src/Covenant.Ui/Models/Contracts.cs         + catalog endpoint_health / probe_allow_list / probe checked_utc; chat DTOs; UiJsonContext entries
src/Covenant.Ui/Program.cs                  + service registrations (InferenceApi with its own HttpClient)
src/Covenant.Ui/_Imports.razor              + @using Covenant.Ui.Logic
src/Covenant.Ui/Pages/{Playground,Requests,Connections}.razor   implemented;  Pages/Overview.razor + degraded-local-route alert
src/Covenant.Ui/Layout/StatusHeader.razor   + local route indicator (instantiates CatalogState)
src/Covenant.Ui/wwwroot/css/app.css         + app-height shell, Playground/presentation/Requests/Connections styles
src/Covenant.Ui/wwwroot/js/interop.js       + copyText
tests/Covenant.Tests/ConsoleLogicTests.cs   new (outcome branches, model facts, cost text, filters, snapshots, presets, connection logic)
tests/Covenant.Tests/Covenant.Tests.csproj  + <Compile Include> links to Ui Logic/*.cs and Models/Contracts.cs
```

## Manual QA checklist

Setup: Host running with at least one application key, cloud mode with a fake/real cloud adapter, optionally Ollama.

A — Playground basics
- [ ] Fresh load: Playground is empty ("No request yet"); nothing in localStorage/sessionStorage except `covenant.theme`
      and `covenant.admin-token` (DevTools → Application).
- [ ] Empty key, Run A → "Authentication failed", HTTP 401; evidence arrives; classification "Not evaluated — stopped at
      authentication" (never PHI).
- [ ] With key, A buffered → Served; selected/attempted/served models, tokens or "Not reported", cost per CostBasis,
      duration "(recorded by Covenant)" once the record arrives, client-measured before; request id copy works.
- [ ] A streamed → text appears incrementally; final usage/cost from the record.
- [ ] B → Policy denied, Provider attempted "None — no provider call", stage "policy" highlighted.
- [ ] C without a local route → refused; with Ollama configured → served by `local:<tag>`; never a cloud model.
- [ ] D: pause (header or presentation) → Run D → "Paused — new provider calls refused", stage pause/budget highlighted;
      resume → Run D again → Served (not from cache: prompt carries a new `[run …]` nonce each time).
- [ ] Run twice identical A without nonce → second is "Served from cache"; cache row highlighted; cost "$0.00 — no provider call".
- [ ] Edit the prompt after a run → "Previous request — the form has changed since"; result title unchanged.
- [ ] Run is disabled while in flight; Cancel → "Cancelled / incomplete … not a rollback".
- [ ] Mid-stream provider failure (fake adapter) → "Transport 200, final outcome: failed", partial text kept.
- [ ] Malformed request (not reachable from the UI; use curl) is shown in Requests as absent — no record.
- [ ] "Open in Requests" (normal + presentation drawer) opens exactly `/requests/<same id>`.
- [ ] Disconnect → reconnect: key, runs, responses and form are gone; reload does the same.

B — Presentation mode
- [ ] Button and `?present=1` both enter it; nav/status header hidden; environment, pause state and Pause/Resume visible
      with the scope note; Exit returns to the normal layout.
- [ ] 1280×720 and 1920×1080 (light + dark): prompt, Run, outcome, model, cost and "Inspect evidence" visible without page
      scroll; a long response scrolls inside its box.
- [ ] Setup and Inspect drawers: Tab trapped, Escape closes, focus returns to the opener.

C — Requests
- [ ] Coverage label shows segment start and record count; "not a full audit history search".
- [ ] Each filter narrows the table; combinations; Clear filters; "No requests match" state; filters survive navigating
      away and back; cleared on disconnect.
- [ ] Row click and Enter on the id button open the drawer; URL becomes `/requests/<id>`; Close/Escape returns to `/requests`.
- [ ] Deep link to an id excluded by filters → record shown + "hidden by the current filters"; unknown id → "Not in the
      current audit segment…"; after archive/reset the old id → same unavailable state.
- [ ] Legacy record → "Not recorded" for v2 facts; raw JSON matches the visible facts; prompt preview only when retained.
- [ ] Long model ids/ids readable at 1280 px (drawer wraps; table ellipsis + title; table scrolls horizontally).

D — Models & connections
- [ ] Running configuration matches `GET /admin/catalog`; "Registered" only from the server flag; unknown health shown as such.
- [ ] Endpoint not on the allow-list → probe "Allowed: No" + Providers:ProbeAllowList guidance; later steps stay disabled.
- [ ] Reachable endpoint → tags listed; no tags → empty state; choose one tag; config block shows names/values, no secrets.
- [ ] Edit the endpoint after a probe → all later steps cleared with the notice; running config unchanged.
- [ ] Before restart, "Check applied configuration" fails with the mismatch; after config + restart it passes.
- [ ] Governed test: asks for a key if none; served by `local:<tag>`; "Open evidence" opens the record. Stop Ollama → run
      again → failed; header shows "Local route: degraded (last success …)" on every page; Overview shows the alert.
- [ ] Paused test → "Paused — not a reachability result"; health unchanged.

## Not verified without a compiler — check first

1. **Razor templates in `@code`** (`RenderFragment` / `RenderFragment<T>` properties with `@<div>…</div>`) containing
   `@switch` with `case … when … is { } rec:` and components — valid Razor, but the most complex markup here.
2. **`@page "/playground/{Scenario?}"`** (optional parameter) also matching `/playground`; `[SupplyParameterFromQuery]`
   on a public property without `[Parameter]` (.NET 8+ style).
3. **Pattern variables in markup** (`@if (x is { } y)`) leaking into the enclosing render lambda — names were kept unique
   per lambda by hand; a CS0128/CS0136 here is a rename.
4. **Linked compile in the test project**: `Contracts.cs` + Logic compile in a non-Blazor project (they reference only
   System.Text.Json); `UiJsonContext` source generation runs there too. No `using Covenant.Host` in ConsoleLogicTests, so
   no ambiguity with Host's `ErrorResponse`/`StatusReport`.
5. **xunit analyzers under warnings-as-errors** (e.g. xUnit2013 style rules) in ConsoleLogicTests.
6. **SSE in the browser**: as for StatusFeed — if `fetch` buffers, streaming still completes but text arrives at once.
7. **App-height shell CSS** (`.shell {height:100vh}`, `#main` scrolling) changes scrolling for every page on desktop;
   check Overview/Policies still scroll and drawers still overlay correctly.
8. **`InputType=number` bound to `int?`** and `<select value=…>` + `@onchange` (Requests filters) — standard Blazor, verify
   selected options render after the options list changes.
9. **Endpoint comparison** in step 6 lower-cases the whole URL (path included), matching the Host's OrdinalIgnoreCase compare.
10. Catalog refresh failure keeps the last catalog (with `CatalogError`); the header may show an older health state until
    the next successful read — acceptable but worth seeing once.

## Compile review pass

Read-through of every file in src/Covenant.Ui, both console test files, ConsoleAssets.cs and the console bits of both
csproj files, done by reading the code because no `dotnet` was available. Fixes:

- `src/Covenant.Ui/App.razor:5` replaced the `<NotFound>` render fragment with `NotFoundPage="@typeof(Covenant.Ui.Pages.NotFound)"`.
  In .NET 10, `Router.NotFound` is `[Obsolete("NotFound is deprecated. Use NotFoundPage instead.")]`, and the CS0618 it causes becomes
  an error with TreatWarningsAsErrors. The two parameters can't be set together.
- `src/Covenant.Ui/Pages/NotFound.razor` (new) is the routable not-found page (`@page "/not-found"`).
  `NotFoundPage` needs a `[Route]`. Same content as before, and MainLayout applies through `DefaultLayout`.
- `src/Covenant.Ui/Pages/Connect.razor:53` `Disabled="@string.IsNullOrWhiteSpace(_token)"` → `@(…)`. This is
  defensive: Razor's handling of an implicit expression that starts with a keyword (`string`) is the only odd parse in the file.
- `src/Covenant.Ui/Pages/Budgets.razor:81` renamed the lambda parameter `t` → `x` in
  `foreach (var t in b.Teams.OrderByDescending(t => …))`. This is also defensive, so the name never collides with the
  iteration variable.
- `src/Covenant.Host/Covenant.Host.csproj` PublishConsole now uses the MSBuild task instead of `Exec dotnet publish`.
  A solution build used to build Covenant.Ui twice at the same time (once from the .sln, once from a separate
  `dotnet publish` process), and both wrote to the same `src/Covenant.Ui/obj/`. Now the target calls
  `Restore` first, as a separate instance via `MSBuildRestoreSessionId`; this is a no-op in a solution build and needed for a Host-only build.
  It then calls the Ui's `PublishConsoleForHost` target with no extra global properties, so MSBuild reuses the
  solution's Covenant.Ui instance. The publish directory comes back as the target's return value, and an `<Error>` fires
  when `wwwroot/ui/index.html` is missing. Host-only publish properties (RuntimeIdentifier, SelfContained, PublishAot,
  PublishSingleFile, PublishTrimmed, PublishReadyToRun, PublishDir, OutDir, OutputPath, _IsPublishing, …) are
  removed through `RemoveProperties`, so `dotnet publish -r linux-x64` of the Host can't reach the WASM build.
  The `SkipConsolePublish`/DesignTimeBuild guard and the EmbeddedResource glob/LogicalName are unchanged.
- `src/Covenant.Ui/Covenant.Ui.csproj` (new target) `PublishConsoleForHost` depends on `Publish` and returns the
  absolute `$(PublishDir)`.

Checked and found to compile (no change):
- Pattern variables in markup are unique within each render lambda (RequiresConnection/Drawer ChildContent, the
  `@code` templates).
- `@bind-Open` has matching `Open`/`OpenChanged`.
- Every `JsonSerializer`/`ReadFromJsonAsync`/`JsonContent.Create` call uses a `UiJsonContext` type info, and every
  DTO is listed.
- JS interop names match interop.js exactly.
- There is no `async` method without `await`, no unawaited Task without a `_ =` discard, and no unused private field.
- `[SupplyParameterFromQuery]` without `[Parameter]` is fine on .NET 8+.
- Logic/*.cs and Contracts.cs have no Blazor dependency.
- ConsoleLogicTests matches the current signatures of `InferenceRequest(Principal, Messages, RequestedModel, Attribution, …)` and
  `RegexDataClassifier.Classify(...).Classification`.
- No type name in Covenant.Core or Covenant.Governance collides with Ui.Logic or Ui.Models.

Remaining risks:
- Instance reuse depends on Covenant.Host and the solution's Covenant.Ui build getting identical global properties.
  That is the normal case for `dotnet build Covenant.sln`. If a caller passes extra global properties only to the Host,
  MSBuild creates a second Ui instance and the old race comes back.
- The Ui publish directory isn't cleaned, so fingerprinted `_framework` files from earlier builds stay embedded. This
  bloats the bundle but doesn't break anything. A `dotnet clean` (or deleting src/Covenant.Ui/bin) resets it.
- `index.html` still references `_framework/blazor.webassembly.js` without the .NET 10 fingerprint placeholder
  (`#[.{fingerprint}]`). As far as I know the publish output still contains the plain name, but that has not been confirmed.
- The old `src/Covenant.Host/obj/console-publish/` folder, if present, is no longer used and can be deleted by hand.
- The trim analyzer on Blazor WASM publish: all STJ/interop calls are typeinfo-based or use primitive types, but
  ILLink only runs on a real publish.
