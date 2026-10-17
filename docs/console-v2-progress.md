# Console v2 — progress record

Spec: `docs/CONSOLE-V2-IMPLEMENTATION-SPEC.md`. Contract: `docs/ui-api-contract.md` (v2 section). One section per task.

---

## T07 UI — shell and request workspace

**Status: NOT EXECUTED.** Written without a .NET SDK (the authoring sandbox has none and cannot download one), so
nothing below has been compiled, unit-tested or opened in a browser. Backend endpoints were being implemented
concurrently by another agent; every UI call to a v2 endpoint is **integration pending until the backend lands**
and the verifier steps below pass. Do not cite T07 as complete before then (spec §7).

### Files

```
src/Covenant.Ui/Models/ConfigContracts.cs       new  v2 DTOs (config envelope/config mirror of Core/Configuration.cs,
                                                     validate/put/versions/rollback, applications, playground request,
                                                     trace event/snapshot, budget state, simulator). All "…Dto" names
                                                     (no clash with Covenant.Core types); enums kept as strings.
src/Covenant.Ui/Models/Contracts.cs             audit v3 fields on AuditEntryView (+ AuditAttemptView; Trace[] kept as
                                                     JsonElement so object or "stage:state:ms" shapes both read);
                                                     ChatRequestDto removed; UiJsonContext lists every new DTO
src/Covenant.Ui/Logic/SafeMarkdown.cs           new  encode-first Markdown renderer (the console's only MarkupString source)
src/Covenant.Ui/Logic/TraceModel.cs             new  trace reduction (dedupe by seq, seq ordering, attempts, skipped,
                                                     not-reported), TraceAccumulator (Last-Event-ID), SseParser,
                                                     audit Trace[] fallback
src/Covenant.Ui/Logic/ShellStatus.cs            new  mode badge mapping, Live / "Stale · n s" label, single announcement
src/Covenant.Ui/Logic/RequestSnapshot.cs        PlaygroundForm gains ApplicationId/ProfileId (streaming default on);
                                                     snapshot gains ApplicationId/ProfileId/ClientRequestId (GUID per Run);
                                                     new ComposerRules (selectable apps, granted profiles, run validation,
                                                     eligible model overrides from the active config)
src/Covenant.Ui/Logic/ScenarioPresets.cs        presets A–D rewritten to spec §8 (ticket-priority JSON task; B/C carry
                                                     the synthetic SSN 078-05-1120; D fresh nonce); populate-only, picks a
                                                     granted Public-baseline profile; IsReady for "Run preset"
src/Covenant.Ui/Logic/OutcomeModel.cs           fallback-aware texts, partial-cancel title, 404 (endpoint missing), 409
                                                     DuplicateSubmission; "there is no fallback model" wording removed
src/Covenant.Ui/Logic/ModelFacts.cs             Destination (serving attempt + Local/Cloud from config), AttemptFacts
src/Covenant.Ui/Logic/CostText.cs               BasisCost for per-attempt cost
src/Covenant.Ui/Logic/LaunchLink.cs             admin token only; appkey= ignored
src/Covenant.Ui/Services/ConsoleInferenceApi.cs new  POST /admin/playground/requests on the admin HttpClient (+ InferenceResult)
src/Covenant.Ui/Services/InferenceApi.cs        DELETED (no remaining references)
src/Covenant.Ui/Services/TraceClient.cs         new  trace SSE follower (Last-Event-ID resume, bounded reconnect, 404/410 →
                                                     snapshot → expired)
src/Covenant.Ui/Services/WorkspaceConfig.cs     new  GET /admin/applications, /admin/config, /admin/budgets (shared, per connection)
src/Covenant.Ui/Services/RunText.cs             new  run display text (duration/tokens/cost/evidence/announcement)
src/Covenant.Ui/Services/PlaygroundState.cs     rewritten: shared composer state, runs + per-run trace + evidence, history (≤30)
src/Covenant.Ui/Services/AdminApi.cs            + applications/config/budgets/trace snapshot/trace stream, ReportUnauthorized
src/Covenant.Ui/Services/AppState.cs            application key removed
src/Covenant.Ui/Services/Interop.cs, wwwroot/js/interop.js   comment only (copy = operator-chosen text)
src/Covenant.Ui/Program.cs                      admin HttpClient timeout 5 min; ConsoleInferenceApi/TraceClient/WorkspaceConfig
src/Covenant.Ui/Components/Icon.razor           new  original inline SVG icons (no icon set)
src/Covenant.Ui/Components/RequestComposer.razor new shared composer (Playground + Overview)
src/Covenant.Ui/Components/TraceBar.razor       new  horizontal trace + stage popover
src/Covenant.Ui/Components/RunSummary.razor     new  compact outcome line
src/Covenant.Ui/Components/RunDetails.razor     new  drawer content: request, outcome, baseline vs final class, attempts, cost, evidence
src/Covenant.Ui/Components/BoundaryTag.razor    new  Local / Cloud / "Boundary not reported" tag (text + icon + colour)
src/Covenant.Ui/Layout/SideNav.razor            icons, active bar, "Audit & evidence", theme moved to header
src/Covenant.Ui/Layout/StatusHeader.razor       env, mode badge, freshness, health chip, pause + scope popover, theme, live region
src/Covenant.Ui/Layout/MainLayout.razor         pause banner wording
src/Covenant.Ui/Pages/Playground.razor          rewritten (two panes, action bar, response/raw/copy, outcome line, trace,
                                                     drawer, history, presentation mode)
src/Covenant.Ui/Pages/Overview.razor            rewritten (setup checks, composer, metrics, recent outcomes, small chart)
src/Covenant.Ui/Pages/Evidence.razor            title "Audit & evidence", + @page "/audit"
src/Covenant.Ui/Pages/Connect.razor             no application key; copy updated
src/Covenant.Ui/Pages/Settings.razor            "Application key: never held"
src/Covenant.Ui/Pages/Connections.razor         governed test runs as a chosen test application via console inference
                                                     (minimal change; the editor is T08)
src/Covenant.Ui/wwwroot/css/tokens.css          + --local/--cloud (+bg), --shadow (both themes)
src/Covenant.Ui/wwwroot/css/app.css             nav/header, composer, trace, response, outcome line, Playground, Overview,
                                                     presentation grid, responsive rules; old Playground CSS removed
tests/Covenant.Tests/Covenant.Tests.csproj      links Models/*.cs (was Contracts.cs only)
tests/Covenant.Tests/ConsoleLogicTests.cs       preset/snapshot tests rewritten; + ComposerRulesTests, SafeMarkdownTests,
                                                     TraceModelTests, ShellStatusTests, AttemptAndDestinationTests;
                                                     launch-link test now asserts appkey is ignored
```

### What is wired to which endpoint

| UI | Endpoint | Status |
|---|---|---|
| Run (Playground, Overview composer, Connections governed test) | `POST /admin/playground/requests` (admin token, buffered or SSE, `X-Covenant-Request-Id`, `client_request_id`) | integration pending until backend lands |
| "Run as" select, profile select, Overview "Test applications" check | `GET /admin/applications` | integration pending |
| Profile names/baselines, eligible model overrides, Local/Cloud boundary of destinations | `GET /admin/config` (redacted) | integration pending; without it the UI shows profile ids, falls back to the legacy catalog for overrides and says "Boundary not reported" |
| Overview budget check | `GET /admin/budgets` (falls back to `status.budget`) | integration pending |
| TraceBar (live) | `GET /admin/requests/{id}/trace/stream` (SSE, `Last-Event-ID`, `event: end`) | integration pending |
| TraceBar (fallback) | `GET /admin/requests/{id}/trace`, then audit v3 `Trace[]` | integration pending |
| Evidence Pending → Verified (record found) / Unavailable | `GET /admin/requests/{id}` (existing EvidenceTracker, bounded) | existing endpoint; v3 fields integration pending |
| Header mode badge | `GET /admin/status` `mode` (accepts local-only/LocalOnly, hybrid, legacy-cloud/LegacyCloud, cloud) | existing field; v2 values pending |
| Pause / resume | `POST /admin/kill-switch` (unchanged) | existing |

A 404 from any v2 endpoint is shown as "not available on this Host yet (endpoint missing — integration pending)",
never as an empty success.

### Decisions

- **No application key anywhere in the console.** `AppState.ApplicationKey`, the Playground key field and the
  `#appkey=` launch parameter are gone; `InferenceApi` is deleted. The Connections governed test was switched to
  console inference ("Run as" select) so the page keeps working without a key.
- **One composer, one state.** `RequestComposer` + `PlaygroundState` back both Playground and the Overview compact
  composer. Presets only populate; the action bar label becomes "Run preset X" only while the form still holds a
  READY preset. Opening a page, `/playground/{A-D}`, applying a preset or picking a history entry never submits.
- **Model override value** is `connectionId/modelId` (the backend's `PolicyV2` override matcher accepts
  `{AdapterKey}/{ModelId}` or a bare model id). Free text is labelled "test input; does not grant permission".
- **Trace** starts as soon as the request id is known (streaming: at the response headers; buffered: when the
  response arrives, i.e. replay). Unreported stages stay "Pending" while open and "Not reported" once complete —
  never inferred as passed/skipped. Provider attempts are grouped by `attempt_id`.
- **Markdown**: the whole input is HTML-encoded first; only the renderer's own tags are emitted; links must be
  absolute http(s) and get `target="_blank" rel="noopener noreferrer"`. Server/provider error text is never
  rendered as markup.
- **Live/stale**: "Live" (green dot) while fresh; "Stale · 12 s" only when stale; exact time in the tooltip. The header
  re-renders each second only while stale. One visually hidden polite region announces connected/paused/stale; the
  Playground has one for run state changes (running → outcome), not for streamed text.
- **Presentation mode** (`?present=1`): prompt + Run/Cancel, response, outcome facts, trace. A fixed layer with
  `overflow:auto` (one scroll at most) and a grid that collapses to one column under 1100 px; the response scrolls
  intentionally. Esc exits and focus returns to the "Presentation mode" button.
- **Icons**: original inline SVG paths written for this repo (`Components/Icon.razor`), no third-party asset — no
  PROVENANCE entry needed beyond noting they are original (docs/PROVENANCE.md not edited by this task).

### Risks / known gaps

1. Not compiled. Highest-risk spots: Razor templates in `@code` (`ResponseBody`), pattern variables inside
   `@switch` cases (`RunDetails`), `@bind:after` on inputs, `Nav.GetUriWithQueryParameter("present", (string?)null)`,
   source-generated names `UiJsonContext.Default.ListApplicationSummaryDto` / `ListBudgetStateDto`.
2. Wire-name assumptions: the Host's `ConfigJsonContext` uses `SnakeCaseLower`, which may name
   `PriceConfig.InputPer1M` `input_per1_m` rather than `input_per_1m` (not used by T07; T08 must check).
   Enums are expected as C# names (`LocalOnly`, `Local`, `Public`) — the UI also accepts kebab/snake spellings.
3. Audit v3 `Trace[]` shape is read leniently (objects or `stage:state:ms`); `Attempts[].Outcome` success words
   assumed: served/success/succeeded/ok/passed/completed.
4. Trace SSE in the browser relies on fetch streaming (`SetBrowserResponseStreamingEnabled`) like the status
   feed; if the browser buffers, events arrive at the end (snapshot fallback still applies).
5. Buffered runs have no request id until the response arrives, so their trace is a replay, not live.
6. 401 from console inference is treated as an admin-token rejection (disconnect), per contract.

### Verifier steps (not executed)

```
dotnet build Covenant.sln -c Release --disable-build-servers
dotnet test tests/Covenant.Tests -c Release --disable-build-servers --filter "FullyQualifiedName~Covenant.Tests.SafeMarkdownTests|FullyQualifiedName~Covenant.Tests.TraceModelTests|FullyQualifiedName~Covenant.Tests.ComposerRulesTests|FullyQualifiedName~Covenant.Tests.ShellStatusTests|FullyQualifiedName~Covenant.Tests.SnapshotAndPresetTests|FullyQualifiedName~Covenant.Tests.AttemptAndDestinationTests"
dotnet test tests/Covenant.Tests -c Release --disable-build-servers     # full suite
```

Browser (isolated port, demo profile; both themes; 1280×720 and 1920×1080; then a ~900 px width):
- [ ] Sidebar: 8 items with icons; active bar on the current page; `/audit` and `/evidence` both highlight "Audit & evidence".
- [ ] Header: environment, mode badge (Local-only / Hybrid / Legacy cloud; "Mode not reported" against an old Host),
      health chip, "Live" with exact time in the tooltip; stop the Host → "Stale · n s" counts, screen reader hears
      "Status is stale" once; pause/resume labels and the ⓘ popover (Esc closes); theme button label flips.
- [ ] Playground: no key field anywhere; "Run as" shows disabled/non-test apps greyed with reason; profile list =
      granted profiles only; Run disabled with the reason until valid; nothing runs on page open, on `/playground/B`,
      on preset click, on reload or on history selection (check the Host log / Requests list).
- [ ] A/B/C/D per spec §8 from the Playground; each Run has a distinct `client_request_id` (DevTools → Network).
- [ ] Streaming: text appears in the right pane, TraceBar chips change as events arrive, provider ×2 on fallback,
      skipped stages dashed; click a chip → popover with state/reason/duration/attempts; Esc closes it.
- [ ] Kill the trace connection mid-run (DevTools offline toggle) → "reconnecting", no duplicated chips afterwards.
- [ ] Cancel mid-stream → partial text kept, "Cancelled — partial response", explanation mentions possible charge.
- [ ] Details drawer: baseline vs final classification, attempts with Local/Cloud tags and costs, evidence
      Pending → "Verified — record found" (or Unavailable + Retry); Tab trapped, Esc closes, focus returns.
- [ ] Markdown: a reply containing `<script>`, `<img onerror>` and `[x](javascript:…)` renders inert text.
- [ ] Presentation mode: everything visible at 1280×720 without page-level clipping; response scrolls; Esc exits
      and focus lands on "Presentation mode".
- [ ] Overview: setup checks each link to the right page; compact composer runs the same state (result appears in
      Playground history); "Run preset A" only when ready.
- [ ] Connections governed test runs as the selected application without a key.
- [ ] No horizontal page scroll at any size; long model ids and errors wrap.

---

## T02/T03 backend — configuration repository, snapshots, classification/profiles, policies v2

**Status: NOT EXECUTED — no .NET SDK.** The authoring sandbox has no `dotnet` and no network access to install one,
so nothing below has been compiled or run. The code was written conservatively for `Nullable=enable`,
`TreatWarningsAsErrors=true`, `IsAotCompatible=true` (all new (de)serialized types are on source-generated contexts;
no reflection, `dynamic` or `Activator`). Treat every claim here as *implemented, unverified* until the verifier
commands at the end pass.

### What is implemented

- **T02 repository (ADR-0011)** — `ConfigRepository`: `versions/v000001.json …` + `active.json {version, sha256}`
  under `Config:Path` (default **`<Audit:Path>.config/`**, so every test with a unique audit path gets its own
  repository). Write ordering under one `SemaphoreSlim`: If-Match check → stamp + validate → compile the snapshot
  (before anything is persisted) → `vM.json.tmp` + fsync + rename create-new → `active.json.tmp` + fsync + rename
  over → swap in memory → `config.activated` audit event. M = max(active, highest file) + 1, so an orphan version
  file from a crash is never reused or overwritten. Boot: missing pointer with version files / corrupt pointer /
  missing or hash-mismatched active file ⇒ error list ⇒ `FailIfConfigErrors` (Program is the only place that exits);
  orphans and leftover `.tmp` files ⇒ warnings. Missing activation event for the active version ⇒ boot appends one
  marked "recovered at boot". File-system seam `IConfigFileSystem` for fault injection.
- **Bootstrap precedence** — existing repository wins; else `Config:Seed=demo-local-only|demo-hybrid`; else
  **legacy import** (`LegacyImport.Build`) with connections named `openai` / `anthropic` / `local` so pre-v2
  `adapter:model` registry keys keep resolving. Imported policies: `legacy-general` (Public+Internal, length
  heuristic, targets openai/public → [anthropic mid-list] → openai/strong, `Routing:ComplexityTokenThreshold`) and
  `legacy-sensitive` (PII+PHI → `local/<tag>` or the `local/local-model-not-configured` placeholder, which stays a
  candidate in `LegacyCloud` mode only so the provider stage refuses it exactly as before); local-only ⇒
  `legacy-local-only` (all classes → local). Prices = the pre-v2 price map + `Pricing:*`; budgets = global (period
  None) + `team:<name>`; applications from `Auth:Keys` (id = `Application` ?? `Principal`; invalid ids stay unbound =
  legacy behaviour); profile `legacy-internal`; cache/rate/preview from the legacy keys (preview clamped to 500, noted
  in the change summary). `StartupValidation` still applies, but only to a legacy import: a repository or seed
  without an enabled OpenAI connection no longer needs `OpenAI:ApiKey`, and `Budget:GlobalCapUsd` is only required
  for import/seed.
- **Egress / secrets** — allow-list = `Providers:ProbeAllowList` ∪ startup endpoint keys (Local/OpenAI/Anthropic,
  or the provider default authority when a cloud key is set without an endpoint); recomputed from startup config
  every boot, never console-editable. Secret refs restricted to `Secrets:<name>` and the three legacy keys;
  availability only (never values) in every payload.
- **`ConfigValidator` / `ImpactAnalyzer` (Governance, pure)** — all contract codes plus `invalid_id`, `required`,
  `invalid_endpoint`, `endpoint_required`, `invalid_boundary`, `invalid_secret_ref`, `invalid_price`,
  `invalid_retry`, `invalid_setting`, `invalid_budget_cap`, `missing_global_budget`, `invalid_savings_baseline`,
  and (validate endpoint only) warning `stale_base_version`. `unknown_connection` is a warning when the candidate's
  mode is `LegacyCloud` (see decisions), an error otherwise. Impact = before/after effective route per
  classification × profile (+ `changed`), applications affected, unavailable destinations, pricing gaps, newly
  refused.
- **`RuntimeSnapshot` + `SnapshotHolder` (`IRuntimeConfig`)** — immutable compiled view implementing
  `Covenant.Core.IRequestConfiguration`; ref-counted (`TryAcquire`/`Release`); a retired snapshot disposes its
  `ProviderClientSet` when the last request releases it. `SdkProviderClientFactory` builds clients keyed
  `connectionId:modelId` (OpenAI SDK for OpenAi/OpenAiCompatible/Ollama, Anthropic SDK for Anthropic) and refuses
  clients for disabled / non-allow-listed / secret-less / cloud-in-local-only connections.
- **Request binding** — both `/v1` endpoints acquire the snapshot at entry (`SnapshotLease`), set
  `ctx.Configuration` + `ctx.ConfigVersion`, release when the handler (including the SSE stream) finishes.
  `ProviderCallStage` resolves against the request's snapshot clients when the Host's `SnapshotChatClientRegistry`
  is the DI registration; a test-registered `IChatClientRegistry` always wins (all existing host tests keep working).
- **Stages read `ctx.Configuration` when present, else their legacy constructor config** — Auth (applications,
  profiles, `PreAuthenticated` console identity), Classify (max(baseline, detected); no-match = no escalation),
  Policy (v2 engine), Cache (TTL/size + key scoped to version/app/profile/class/connection), RateLimit, Budget
  (hard global + team caps; application scope is T04), Attribution + failed-call charging (connection-scoped prices),
  Audit (preview chars + v3 fields). Every pure pipeline test constructs stages without a configuration and is
  unaffected.
- **T03 identity** — `ApiKeyRecord.ApplicationId` (`Auth:Keys:N:Application`, default Principal); disabled
  application ⇒ governed 403 at `auth`; `X-Covenant-Profile` must be granted ⇒ else 403 "profile not granted";
  none ⇒ application default ⇒ `legacy-internal` (Internal). Anonymous callers cannot select a profile.
  `InferenceRequest.PreAuthenticated` (record `PreAuthenticatedIdentity`) lets the console endpoint (T05) skip key
  lookup while auth still enforces enabled + consoleTesting + grant; team comes from the application.
- **Policy engine v2 (`PolicyEngineV2`, pure)** — precedence (profile-specific beats classification-only;
  same-tier overlap ⇒ refusal), paused policy ⇒ refusal, eligibility (route paused, connection known + enabled,
  model permitted, local-only boundary, PII/PHI never cloud without `allow_sensitive_cloud`, unpriced cloud under a
  hard budget), `fixed` (primary + ordered fallback) and `length-heuristic` (named honestly), model override must be
  an eligible target and pins it (accepts `model` or `connection/model`). `PolicyOutcome` gained `PolicyId`,
  `Candidates`, `Excluded`, `Explanation` ("Public → openai/gpt-4o-mini (fallback: ollama/qwen3:8b) · policy public v1").
  Fallback *execution* is still T06 (provider stage uses `Route` only).
- **Audit v3 (additive)** — `AuditEntry` + `EventType, ConfigVersion, EntryPoint, OperatorRef, ApplicationId,
  ProfileId, BaselineClassification` (nullable, at the end). `FinOps.Build`, `EvidenceExport`, `LedgerReplay`,
  `/admin/requests` (list + lookup) exclude events; segment and evidence gain an `events` count.
  `FileAuditSink` implements `IAuditEventWriter.TryWrite`. `SchemaVersion` stays **2** (an existing test asserts it).
- **Status/catalog** — `/admin/status.mode` = `local-only|hybrid|legacy-cloud` (was `cloud|local-only`) +
  `config_version`; routes/budgets/threshold/cache/preview from the snapshot. `/admin/catalog` keeps every field and
  adds `config_version`, `connections[]` (id, display name, kind, boundary, enabled, endpoint summary,
  `endpoint_allow_listed`, `secret_ref`, `secret_available`, models with `key`/price/registered/health) and
  `egress_allow_list`; each model gains `connection_id`, `boundary`, `key`.

### Endpoints (docs/ui-api-contract.md v2)

`GET /admin/config` (ETag) · `POST /admin/config/validate` · `PUT /admin/config` (If-Match; 428/400/412/422/200) ·
`GET /admin/config/versions?limit=` · `GET /admin/config/versions/{n}` · `POST /admin/config/rollback` (412 when the
active version moved; activates as a NEW version) · `GET /admin/applications` · `POST /admin/policies/simulate`
(dry run: real classifier + engine against the active or a supplied, validated candidate; no provider, no audit,
no spend). Wire vocabulary: snake_case properties; `mode`/`strategy`/`period`/`enforcement` kebab-case
(`local-only`, `legacy-cloud`, `length-heuristic`, `warn-only`); `kind`/`boundary`/classifications C# names
(`OpenAi`, `Local`, `Pii`); prices `input_per_1m` / `output_per_1m` (explicit names on `PriceConfig`).

### Files

```
src/Covenant.Core/RequestConfiguration.cs        new  IRequestConfiguration (what stages read)
src/Covenant.Core/InferenceContext.cs             Configuration, ConfigVersion, EntryPoint, OperatorRef, ApplicationId, ProfileId, BaselineClassification
src/Covenant.Core/CanonicalModel.cs               InferenceRequest.RequestedProfile / PreAuthenticated; PreAuthenticatedIdentity
src/Covenant.Core/Decisions.cs                    PolicyOutcome v2 members, ExcludedTarget, AuditEntry v3 fields, AuditEntryKinds.IsRequest()
src/Covenant.Core/Configuration.cs                [JsonPropertyName] on PriceConfig per-1M fields only (additive)
src/Covenant.Governance/RuntimeSnapshot.cs        new  RuntimeSnapshot, IRuntimeConfig, SnapshotHolder
src/Covenant.Governance/PolicyV2.cs               new  PolicyEngineV2 (match/eligibility/strategies/explain/summary)
src/Covenant.Governance/ConfigValidation.cs       new  ConfigValidator, ValidationEnvironment, ImpactAnalyzer
src/Covenant.Governance/{Auth,Classification,Policy,Cache,RateLimit,Budget,Attribution,Audit}.cs   snapshot-aware stages;
                                                       ProfileResolution, ClassificationRules, ConfigPrices
src/Covenant.Adapters/ProviderLayer.cs            ProviderCallStage(preferRequestClients)
src/Covenant.Host/ConfigRepository.cs             new  repository + IConfigFileSystem
src/Covenant.Host/ConfigContracts.cs              new  ConfigJsonContext, API DTOs, KebabEnumConverter, catalog connection views
src/Covenant.Host/ConfigBootstrap.cs              new  StartupEgress, LegacyImport, DemoSeeds, ConfigAuditEvents
src/Covenant.Host/ProviderClients.cs              new  ProviderClientSet, IProviderClientFactory, SdkProviderClientFactory, SnapshotChatClientRegistry
src/Covenant.Host/ConfigAdminApi.cs               new  SnapshotLease, /admin/config*, /admin/applications, /admin/policies/simulate
src/Covenant.Host/Program.cs                      bootstrap/repository wiring, snapshot per request, status/catalog/probe/requests v2
src/Covenant.Host/{Evidence,StatusContracts,ConsoleBackend,FileAuditSink}.cs   event exclusion, events counts, catalog fields, IAuditEventWriter
tests/Covenant.Tests/ConfigV2TestKit.cs           new  builders, GatedChatClient, HybridSeedFactory (Config:Seed=demo-hybrid), V2Admin helpers
tests/Covenant.Tests/ConfigRepositoryTests.cs     new
tests/Covenant.Tests/ConfigApiTests.cs            new
tests/Covenant.Tests/SnapshotIsolationTests.cs    new
tests/Covenant.Tests/ClassificationProfileTests.cs new (+ ProfileHeaderHostTests)
tests/Covenant.Tests/PolicyEngineV2Tests.cs       new
tests/Covenant.Tests/LegacyImportTests.cs         new
tests/Covenant.Tests/SimulatorTests.cs            new
```

### Tests (what each guarantees; each fails if its guarantee is removed)

- `ConfigRepositoryTests` — monotonic versions + reopen; stale expected version; invalid leaves disk unchanged;
  crash between version file and pointer (fault-injecting FS) ⇒ v1 kept, orphan reported, never reused; activation
  only after both renames; corrupt pointer / missing pointer / hash mismatch ⇒ boot errors; rollback = new version;
  two concurrent applies with the same expected version ⇒ one Applied, one Stale; ETag parsing; persisted wire names.
- `ConfigApiTests` — GET ETag/secrets/allow-list; admin-only; validate never mutates (+ impact newly-refused /
  unavailable); PUT 428 / 412 / 422 (active keeps serving) / 200 + `config.activated` event (excluded from
  requests/evidence counts) + versions list; rollback 412 then new version; 400/404; the literal secret value never
  appears in GET/validate/catalog/status/applications/versions payloads, repository files or the audit log;
  status/catalog mode + connections.
- `SnapshotIsolationTests` — request blocked in the provider on v1 while v2 (×100 price + paused policy) is applied:
  it completes with v1 price and `ConfigVersion=1`; the next request is refused by v2; refcount disposal ordering;
  concurrent acquire/swap never hands out a disposed snapshot.
- `ClassificationProfileTests` / `ProfileHeaderHostTests` — Public only via granted Public profile; prompt text and
  headers cannot downgrade; detector escalates Public→PII/PHI and stays local; cloud override on escalated data
  refused; ungranted/unknown profile 403 at auth (audited, no provider call); disabled app 403; unbound key = legacy;
  anonymous cannot pick a profile; console identity enforces enabled/consoleTesting/grant; Combine never lowers.
- `PolicyEngineV2Tests` — demo defaults; precedence; ambiguous overlap (validation error + runtime refusal);
  disjoint profiles; paused policy; disabled policy; paused route skip / all paused refuse; local-only exclusion;
  sensitive never cloud without ack; override pinning; length heuristic name + threshold; unpriced cloud under a
  hard budget; disabled/unknown connections; validator contract codes; Summary.
- `LegacyImportTests` — 5 legacy shapes × 4 classes × 5 prompt lengths × 10 requested models: v2 decision ==
  verbatim pre-v2 `PolicyEngine` decision (effect, route, denial text); imports validate; prices; modes/placeholder;
  identities/budgets/settings; invalid legacy values reported; demo seeds restrictive defaults.
- `SimulatorTests` — dry run never calls a provider (tripwires), writes no audit line (FIFO proof with a later real
  request), records no spend; escalation + exclusion; ungranted/unknown refused; candidate config used but not
  applied; admin-only.

### Decisions / deviations

1. Default `Config:Path` = `<Audit:Path>.config/` (lead's instruction for test isolation) rather than ADR-0011's
   `covenant-config/`; ADR text should be updated by whoever owns docs/adr.
2. `unknown_connection` is a warning whenever the candidate's mode is `LegacyCloud` (not only for origin
   `legacy-import`): otherwise every console edit/rollback of an imported configuration would be forced to delete
   the pre-v2 placeholder. The engine keeps unknown-connection targets as candidates only in `LegacyCloud` (the
   provider stage refuses them, preserving pre-v2 evidence); in hybrid/local-only they are excluded.
3. `/v1` key authentication keeps the team from the key (unchanged semantics); the application contributes enabled
   state and grants. The console identity takes its team from the application.
4. `SchemaVersion` stays 2 on new records (existing assertion); v3 fields are presence-detected.
5. `/admin/status.mode` now reports `legacy-cloud` for imported cloud configurations (was `cloud`).
6. Imported policies use `retry.max_attempts = 1` (pre-v2 had no fallback); seeds use the default 2.
7. A model override also accepts `connection/model`; pre-v2 rejected that form (only bare ids are pre-v2 inputs).
8. Budget scopes accept free-text team names (≤128 chars) so legacy `Budget:TeamCapsUsd` keys never become
   unenforced or refuse boot.
9. Boot validates the active version against the current startup environment; e.g. a removed secret refuses start.

### Risks / deferred

- **Unbuilt.** Highest compile risk: STJ source-gen with `required` records + `KebabEnumConverter<T>` subclass of
  `JsonStringEnumConverter<T>`; RDG on the new minimal-API lambdas; xUnit analyzers under warnings-as-errors.
- Application-scope budgets, UTC periods, reservations and the ledger are T04 (BudgetStage enforces hard global +
  team caps only). Fallback execution, attempts and trace are T05/T06; `RetrySettings` is validated but not used yet.
- The `config.activated` event is enqueued on the async audit channel after the pointer rename (same durability as
  request records); a crash in between is repaired by the boot "recovered" event. After `/admin/reset` (archive) the
  next boot re-records the active version's activation in the new segment.
- Connection `timeout_seconds` is validated/persisted but not yet applied to SDK clients.
- The probe allow-list is unchanged (`Providers:ProbeAllowList` ∪ `Local:Endpoint`); config validation uses the
  wider egress list (adds startup OpenAI/Anthropic endpoints).

### Verifier commands (not executed)

```
dotnet build Covenant.sln -c Release --disable-build-servers
dotnet test tests/Covenant.Tests -c Release --disable-build-servers --filter "FullyQualifiedName~ConfigRepositoryTests|FullyQualifiedName~ConfigApiTests|FullyQualifiedName~SnapshotIsolationTests|FullyQualifiedName~ClassificationProfileTests|FullyQualifiedName~ProfileHeaderHostTests|FullyQualifiedName~PolicyEngineV2Tests|FullyQualifiedName~LegacyImportTests|FullyQualifiedName~SimulatorTests"
dotnet test tests/Covenant.Tests -c Release --disable-build-servers --collect:"XPlat Code Coverage" --settings tests/coverlet.runsettings   # full suite: every pre-existing host test must stay green on the legacy import
dotnet test tests/Covenant.Tests -c Release -p:SkipConsolePublish=true -o <fresh-temp-output>
```
Expected: 0 warnings/errors; all new suites green; the pre-existing suites (LocalOnlyModeTests, ServedPathTests,
CorrelationAndEvidenceTests, HttpMappingTests, AnthropicIngressTests, WireShapeTests.Ingress, ProviderProbeTests,
ConsoleAssetsTests, …) unchanged and green. Manual: start with `Config:Seed=demo-hybrid` on a fresh `Audit:Path`,
`curl -H "X-Covenant-Admin-Token: …" :PORT/admin/config` → `etag "v1"`; PUT with `If-Match: "v1"` → v2; restart →
v2 still active; corrupt `active.json` → refuses to start with the diagnostic.
