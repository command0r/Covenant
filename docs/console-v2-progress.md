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

## UI test fixes + round-trip

Reviewer build: Host + UI compiled, `tests/Covenant.Tests` did not (stale `ScenarioPresets.NormalPrompt` /
`SensitivePrompt` and `LaunchLink.ApplicationKey`). In the current tree `ConsoleLogicTests.cs` already used the v2
API (`TicketPrompt` / `SensitiveTicketPrompt`, `Credentials(AdminToken)`); every remaining call was re-checked
against `src/Covenant.Ui/Logic` + `Models`, and the v2 behaviour is now pinned explicitly.

### Files

- `tests/Covenant.Tests/ConsoleLogicTests.cs` — presets: every preset populate-only (operator settings untouched,
  `StillMatches`), A detects nothing (no signal = no escalation, Public baseline kept), B/C carry the synthetic SSN
  `078-05-1120` → `Pii` / "US SSN pattern", D fresh nonce + no forced profile + nonce never sensitive. LaunchLink:
  `#token=` accepted, `#appkey=` ignored, `Credentials` exposes only `AdminToken`, query-string tokens rejected
  (even beside a fragment), empty / >512-char tokens rejected. Eligible overrides use the wire mode spellings
  (`hybrid`, `local-only`) and still accept the enum names.
- `tests/Covenant.Tests/ContractRoundTripTests.cs` (new) — Host `ConfigGetResponse` (ConfigJsonContext) → UI
  `ConfigEnvelopeDto` (UiJsonContext) → UI `ConfigPutRequestDto` → Host `ConfigPutRequest`; the Host re-serialization
  of the result is byte-identical to the original, plus field-by-field checks (mode ×3, kinds ×4, boundaries,
  strategies, periods, enforcement, classifications, `input_per_1m`/`output_per_1m`, target order, paused flags,
  retry) and the exact wire strings. The UI DTOs were already linked (`src/Covenant.Ui/Models/*.cs`, no Blazor
  types), so the csproj is unchanged. Alias is `UiModels`, not `Ui`: inside `namespace Covenant.Tests` the simple
  name `Ui` binds to the `Covenant.Ui` namespace before any compilation-unit alias.
- `src/Covenant.Ui/Models/ConfigContracts.cs` — removed `PolicyDto.AllowSensitiveCloud` (PII/PHI never route to
  cloud destinations). The UI DTO spellings already matched the Host vocabulary; no other DTO change.
- `src/Covenant.Ui/Logic/ShellStatus.cs` — Hybrid badge text: "PII/PHI never route to cloud destinations."
- `src/Covenant.Ui/Pages/Policies.razor` — "In plain words" list states "PII/PHI never route to cloud destinations."

### Verifier (not executed — no SDK here)

```
dotnet build Covenant.sln -c Release --disable-build-servers
dotnet test tests/Covenant.Tests -c Release --disable-build-servers --filter "FullyQualifiedName~ContractRoundTripTests|FullyQualifiedName~SnapshotAndPresetTests|FullyQualifiedName~ConnectionLogicTests|FullyQualifiedName~ComposerRulesTests"
```
Dependency: the round-trip asserts the kebab wire spellings and passes only while `PolicyConfigV2` serializes
`allow_sensitive_cloud` identically on both Host passes (it does: false) or after the backend removes it.

---

## Review fixes round 1 (backend — CODEX-REVIEW.txt + corrected ADR-0011..0014)

**Status: NOT EXECUTED — no .NET SDK in the authoring sandbox.** Written for Nullable + TreatWarningsAsErrors +
IsAotCompatible (source-gen JSON only; generic `Enum.IsDefined<T>`). SDK option names were verified against the
referenced assemblies' metadata (OpenAI 2.12.0, System.ClientModel 1.14.0, Anthropic 12.8.0 from the local build
output), not compiled. Supersedes T02/T03 "Decisions / deviations" #2 (unknown_connection as a warning in
legacy-cloud) and the "unbound key = legacy behaviour" rule.

| # | Defect | Fix | Regression test(s) |
|---|---|---|---|
| 1 | Validation not total (mode=999 accepted, settings=null → 500) | `ConfigValidator.Structural`: every required object/list/element/id non-null (`required`) and every enum (`ProviderMode`, `ProviderKind`, `Boundary`, `RoutingStrategy`, `BudgetPeriod`, `BudgetEnforcement`, `DataClassification` in profiles/policies) defined via `Enum.IsDefined<T>` (`invalid_enum`), with field paths; semantic pass and impact run only when structurally sound (`ConfigValidationResult.StructurallySound`). Kebab converters accept numbers on READ so 999 reaches the validator. validate → 200 `{valid:false, impact:null}`, PUT → 422 | `ConfigValidationTotalityTests.Every_out_of_range_enum_is_an_invalid_enum_error_with_its_path` (8 enums), `…Null_required_objects_lists_and_elements_are_required_errors_never_exceptions` (24 paths), `…Null_ids_and_required_strings_…`, `…Undefined_numeric_enum_and_null_settings_deserialize_…`; Host: `ConfigApiTests.Structurally_broken_candidates_are_200_valid_false_on_validate_and_422_on_put` (settings=null, mode=999, kind=999, …) |
| 2 | Ambiguous bare model override → FirstOrDefault | `PolicyEngineV2.ResolveOverride`: `connection/model` exact; bare id only if exactly one ELIGIBLE target has it, else deny "model id 'X' is ambiguous across connections a, b — use connection/model". Simulator uses the same engine; cache key and prices are connection-scoped; FinOps' bare-id savings map takes the highest price for an id on several connections (never overstates savings) | `PolicyEngineV2Tests.Ambiguous_bare_model_override_is_refused_and_connection_slash_model_resolves_exactly` |
| 3 | Snapshot aliased caller lists | `ConfigFreezer.Freeze` (RuntimeSnapshot ctor): every list copied into a new array wrapped in `ReadOnlyCollection<T>`, recursively; refuses null instead of defaulting | `ConfigValidationTotalityTests.Snapshot_is_a_deep_copy_unaffected_by_later_mutation_of_the_callers_lists`, `…Snapshot_refuses_a_structurally_unsound_configuration_instead_of_defaulting` |
| 4 | Unknown connection eligible in LegacyCloud | Bypass removed (engine + validator; `ValidationEnvironment.LegacyCompat` deleted): unknown → never eligible, `unknown_connection` always an error; disabled → never eligible ("connection 'x' is disabled (not configured)" when it has no endpoint). Legacy import without `Local:*` adds the DISABLED placeholder `local` ("Local model — not configured (import placeholder)", OpenAiCompatible, Local, endpoint null, model `local-model-not-configured`, zero-marginal-local price) | `PolicyEngineV2Tests.Disabled_or_unknown_connection_targets_are_ineligible_in_every_mode`, `LegacyImportTests.Modes_connections_and_the_unconfigured_local_placeholder_are_explicit`, `LegacyImportTests.Imported_configuration_routes_exactly_like_the_pre_v2_policy` (see changed assertions) |
| 5 | AllowSensitiveCloud escape hatch | Removed from `PolicyConfigV2`, validator, engine, test kit (UI DTO removed by the UI agent). PII/PHI policy with a Cloud target → error `sensitive_cloud_target`; engine excludes cloud for PII/PHI in every mode | `PolicyEngineV2Tests.Sensitive_data_never_reaches_cloud_and_a_sensitive_policy_with_a_cloud_target_is_invalid` |
| 6 | Boundary self-declared | `Boundary.Local` only if the endpoint authority ∈ `Providers:LocalEndpoints` (comma-separated host:port or URLs) ∪ legacy `Local:Endpoint` authority (`StartupEgress.LocalEndpoints`, `ValidationEnvironment.LocalEndpoints`); `OpenAi`/`Anthropic` always Cloud; else `boundary_not_approved`. Labels only — egress is still the allow-list | `PolicyEngineV2Tests.Local_boundary_requires_a_startup_approved_endpoint` (allow-listed cloud host labelled Local → error) |
| 7 | Missing application after import fell back to legacy | AuthStage with a configuration: key whose application is absent → 403 Governance at `auth`, "application not configured: …". Legacy import (and seeds) create one application for EVERY key via `ApplicationIds.FromKey` (valid id verbatim, else `<sanitized>-<sha256[..8]>`), the same normalization `ApiKeyRecord.Application` uses | `ConfigApiTests.Key_whose_application_was_removed_is_403_at_auth`, `ClassificationProfileTests.Key_without_a_configured_application_is_refused_at_auth`, `LegacyImportTests.Identities_budgets_and_operational_settings_are_imported` |
| 8 | Pre-authenticated identity settable via /v1 | Removed from `InferenceRequest` (the wire-derived record); now `InferenceContext.PreAuthenticated` with a private setter, bound only by `BindConsoleIdentity(identity, operatorRef)` (also sets EntryPoint/OperatorRef, now private-set). /v1 handlers never call it | `PublicIngressIdentityTests.Pre_authenticated_identity_is_not_part_of_any_wire_derived_request_type`, `PublicIngressIdentityTests.V1_ignores_every_attempt_to_claim_a_console_identity_and_requires_a_key` (both dialects; admin token + spoof headers + body fields → 401, audit EntryPoint v1) |
| 9 | Paused policy fall-through | Already correct (Match returns the most specific tier, paused ⇒ deny); now pinned | `PolicyEngineV2Tests.Paused_specific_policy_never_falls_through_to_a_less_specific_policy` |
| 10 | Repository lineage (orphan in history) | `activations.jsonl` journal (`ActivationRecord`: version, base, rolled_back_from, utc, origin, summary, recovered), appended + fsync after the pointer rename (`IConfigFileSystem.AppendDurable`); boot appends `recovered: true` when the active version is missing; torn last line tolerated, corrupt earlier line refuses start. History / `GET versions/{n}` / rollback read the journal only (orphan → 404); next = max(files, journal, active)+1. Compile (provider clients) happens in `prepare` before any write, `ConfigCompileException` ⇒ Invalid, nothing written; Program compiles v1 before `Bootstrap` and refuses to start on compile failure | `ConfigRepositoryTests.Crash_between_version_file_and_pointer_keeps_the_old_version_and_reports_the_orphan` (repaired: ListVersions = [3,1], survives restart, next = 4), `…Crash_between_pointer_and_journal_is_recovered_at_boot`, `…Compile_failure_writes_nothing_durable`, `…Journal_corruption_before_the_last_line_refuses_to_start_but_a_torn_last_line_is_tolerated`; Host: `ConfigApiTests.Orphan_version_is_404_refused_for_rollback_absent_from_history_and_never_reused` |
| 11 | SDK transports/retries/timeouts default | Per connection: `SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout ≤ 30 s }`, `HttpClient.Timeout = Infinite`; OpenAI/compatible/Ollama: `OpenAIClientOptions { Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = timeout_seconds }`; Anthropic: `new AnthropicClient { ApiKey, BaseUrl, HttpClient = http, MaxRetries = 0, Timeout = timeout_seconds }` (all init properties exist in 12.8.0 — no limitation to document). HttpClients are owned and disposed by the `ProviderClientSet`. Enabled connection referenced by an enabled policy without a client ⇒ `client_unbuildable` (validate endpoint, apply → 422, boot refuses) | `ProviderClientConstructionTests` (real SDKs against loopback sockets: 503 → exactly one request; 302/307 → redirect target never contacted; 1 s timeout enforced; `Unbuildable_referenced_connection_is_a_validation_error_not_a_silent_skip`) |
| 12 | Enum spelling contract | Documented in `ConfigContracts.cs` and `docs/ui-api-contract.md` v2 "Wire spellings": mode/strategy/period/enforcement kebab, kind/boundary/classification C# names, `input_per_1m`/`output_per_1m` | `ConfigValidationTotalityTests.Wire_spellings_are_the_documented_contract` (+ the UI agent's `ContractRoundTripTests`) |
| 13 | Unbounded/unredacted summaries | `ConfigRepository.Bound`: value redaction (`StartupEgress.Redactor`: OpenAI/Anthropic/Local keys, `Secrets:*`, `Auth:Keys` values, `Admin:Token`) + pattern redaction (`sk-…`, `Bearer …`, 40+ char token runs) → `[redacted]`, control chars stripped, ≤ 500 chars (499 + "…"); same text in version file, journal and `config.activated` audit reason | `ConfigRepositoryTests.Change_summaries_are_redacted_and_bounded`, `ConfigApiTests.Change_summary_is_redacted_and_bounded_everywhere_it_is_recorded` |
| 14 | Test isolation | `CorrelationTestKit.IsolatePaths(builder, auditPath)` sets `Audit:Path`, `Config:Path = <audit>.config`, `Budget:LedgerPath = <audit>.ledger.jsonl` in EVERY WebApplicationFactory (HybridSeedFactory, Correlation×2, LocalOnly, ServedPath, ConsoleAssets, HttpMapping, AnthropicIngress, WireShape.Ingress, ProviderProbe); `DeleteWithSiblings` now also removes the derived config directory. No test relies on a shared /tmp default | all host suites |

### Existing tests changed to the stricter behaviour (intent kept: refused, no provider call, no cloud call)

- `CorrelationAndEvidenceTests.Missing_local_adapter_403_selected_but_never_attempted` → renamed
  `Missing_local_model_403_refused_at_policy_never_selected_or_attempted`: `StoppedAtStage` `provider` → `policy`;
  `SelectedModel` `local-model-not-configured` → null; reason now contains "connection 'local' is disabled (not
  configured)"; added: provider call count unchanged.
- `LegacyImportTests.Imported_configuration_routes_exactly_like_the_pre_v2_policy`: for shapes without `Local:*`,
  PII/PHI now must be a policy Deny (pre-v2 "allowed" the placeholder then refused at provider); where pre-v2 already
  denied, the reason is still identical. `Modes_connections_…`: cloud import now has connections `[openai, local]`
  (placeholder). `Imported_configuration_validates_…` uses `LocalEndpoints`, asserts no `unknown_connection`.
- `ClassificationProfileTests.Key_without_a_configured_application_keeps_legacy_behaviour` → renamed
  `…_is_refused_at_auth` (403 auth, audited, no classify, no provider).
- `PolicyEngineV2Tests`: `Sensitive_data_never_reaches_cloud_without_an_explicit_acknowledgement` → no acknowledgement
  path exists; `Disabled_or_unknown_connection_targets_are_ineligible_outside_legacy_cloud` → `…_in_every_mode`.
- `LegacyImportTests.Identities_…`: the `alice@example.com` key is now bound to `alice-example.com-<hash>` (was
  unbound). `PipelineSliceTests` / `TelemetryTests` keep "no adapter registered" — they run the pure pipeline with
  the legacy engine (no configuration), which is unchanged.

### Notes / residual risks

- Repositories written by the previous build have no `activations.jsonl`: at first boot only the active version is
  recovered into the journal; older version files then show as orphans (not history). Pre-release only.
- A previously persisted legacy-cloud v1 without the placeholder (unknown `local` target) no longer validates ⇒ boot
  refuses; delete the test/demo repository (it is beside the audit log) to re-import.
- `/v1/models` still lists the placeholder model id for an imported cloud configuration (unchanged listing rule).
- Highest compile risks: local functions `Defined<T>`/`NonNullList<T>` in `ConfigValidator.Structural`; Anthropic
  object initializer (`HttpClient`, `MaxRetries`, `Timeout` are init-only); `ClientRetryPolicy(0)`; the
  `GeneratedRegex` in `SecretRedaction`.

### Verifier commands (not executed)

```
dotnet build Covenant.sln -c Release --disable-build-servers
dotnet test tests/Covenant.Tests -c Release --disable-build-servers --filter "FullyQualifiedName~ConfigValidationTotalityTests|FullyQualifiedName~ProviderClientConstructionTests|FullyQualifiedName~ConfigRepositoryTests|FullyQualifiedName~ConfigApiTests|FullyQualifiedName~PublicIngressIdentityTests|FullyQualifiedName~PolicyEngineV2Tests|FullyQualifiedName~LegacyImportTests|FullyQualifiedName~ClassificationProfileTests|FullyQualifiedName~SnapshotIsolationTests|FullyQualifiedName~SimulatorTests|FullyQualifiedName~CorrelationAndEvidenceTests"
dotnet test tests/Covenant.Tests -c Release --disable-build-servers     # full suite
```
Manual: `POST /admin/config/validate` with `"settings": null` → 200 `valid:false` path `settings`; `"mode": 999` →
`invalid_enum` at `mode`; same bodies via PUT → 422; drop a fake `versions/v000002.json` into the config dir →
`GET /admin/config/versions/2` 404, next apply becomes v3.

---

## T04/T05/T06 backend — budget periods + reservations, console inference + real trace, approved fallback / cache / cost / archive

**Status: NOT EXECUTED — no .NET SDK in the authoring sandbox.** Nothing below has been compiled or run. Written for
Nullable + TreatWarningsAsErrors + IsAotCompatible (source-generated JSON only; every new minimal-API async lambda is
`async Task<IResult> (…) =>`). SDK exception types were verified against the referenced assemblies' metadata in the local
build output (Anthropic 12.8.0: `Anthropic.Exceptions.AnthropicApiException.StatusCode : System.Net.HttpStatusCode`,
`AnthropicIOException`; System.ClientModel 1.14.0: `ClientResultException.Status : int`), not compiled. Treat every claim
as *implemented, unverified* until the verifier commands below pass.

### What is implemented

- **T04 budgets (ADR-0014).** `BudgetLedger` (Governance, pure) over `ILedgerStore`: append-only reserve / settle /
  release / settle-recovered / legacy-import records keyed (requestId, attemptId) with the charged SCOPES (`global`,
  `team:<t>`, `application:<a>`) and a UTC timestamp — never a budget id or period id. Spend per budget = settled amounts
  of its scope in its current UTC window (none ⇒ all time), computed at read time from per-(scope, period-bucket) totals,
  so renaming a budget / changing its period or cap never resets accrual. One lock; `spent + reserved + bound ≤ cap` for
  every applicable HARD budget, all-or-none; the record is appended durably before memory changes. `FileLedgerStore`
  (Host): `Budget:LedgerPath` (default `<Audit:Path>.ledger.jsonl`), JSONL, fsync per append, torn-tail quarantine
  (`.torn-<utc>`) + atomic rewrite before any append, refuses to append onto an unterminated line, corrupt earlier line ⇒
  refuse to start, failed append ⇒ `Environment.FailFast` (like a failed audit write). Boot: replay; open reservations ⇒
  `settle-recovered` at the reserved amount + a `budget.recovered` audit event; no ledger file ⇒ the current audit
  segment's spend is imported once (`legacy-import`, global + team scopes) and the file is created. The audit-replay
  in-memory ledger is gone from the Host; `/admin/reset` no longer touches spend (an archive never reopens a budget).
  `BudgetStage(killSwitch, BudgetLedger)` (configured requests): installs the per-request `BudgetAttemptGate` and reserves
  attempt 1 for the primary candidate (refusal ⇒ 403 at stage `budget`, code `budget_exceeded`); the legacy constructor and
  path are unchanged for pure compositions. Bound per attempt = (UTF-8 bytes of all contents + 16/message) × input price +
  min(request max_tokens, application max_output_tokens, model max_output_tokens) × output price; that output bound is
  now sent as `ChatOptions.MaxOutputTokens` (`RequestBounds`, Core). Settle exactly once with the attributed cost; usage
  unreported ⇒ the reservation bound (`estimated_from_text`); reported cost above the reservation ⇒ settled in full,
  `Overrun` in the attempt evidence and the ledger record; release of the remainder in the same durable write; failed /
  cancelled attempts settle at their bound (`estimated_failed_call`); an admitted attempt that never started is released;
  the budget stage's `finally` closes anything left open. Explicit $0 local prices reserve and write nothing. Warn-only
  budgets never block; warnings are status only. Unpriced paid (cloud) targets under a hard budget: verified ineligible in
  `PolicyEngineV2.Ineligibility` and a validation error (`unpriced_paid_target_under_hard_budget`) — no change needed.
  APIs: `GET /admin/budgets`, `GET /admin/budgets/{id}/requests`; `/admin/status.budget` reads the ledger.
- **T05 console inference (ADR-0012).** `POST /admin/playground/requests` (`PlaygroundApi`): admin auth; 400 before the
  pipeline (no audit) for malformed/invalid bodies; `client_request_id` claimed atomically (`ConsoleSubmissions`, 10 min,
  bounded) ⇒ 409 `duplicate_submission` with the original `request_id`; the application is resolved server-side and bound
  with `ctx.BindConsoleIdentity(…, "shared-admin-token")`; the response is produced by `ChatCompletions.RunAsync`, the
  former `/v1/chat/completions` handler body (now shared — `/v1` calls the same function); `X-Covenant-Request-Id` on every
  answer; `RequestAborted` propagates. `GET /admin/applications` already existed (T03).
- **T05 trace (ADR-0013).** Core: `PipelineTraceEvent`, `InferenceContext.TraceSink` / `Trace()` / `CompleteTrace()` /
  `CompactTrace()`; `InferencePipeline.RunStageAsync` emits running → passed (Stopwatch duration at hand-over) / refused /
  failed / cancelled with bounded reason codes (`ctx.Deny(…, code:)`), and `skipped` (`not_reached` | `cache_hit`) for
  stages never entered; the audit stage is not traced. Provider attempts emit attempt events (`att_N`, target), fallback
  pause/budget re-checks emit attempt-level `pause`/`budget` events. Host: `TraceHub` singleton (≤ 64 events/request,
  ≤ 2 000 requests, retention `trace_retention_minutes` after completion, remembered expired ids ⇒ 410), buffer created
  BEFORE the pipeline for `/v1/chat/completions`, `/v1/messages` and console inference; `GET /admin/requests/{id}/trace`
  and `/trace/stream` (SSE `id:`/`data:`, replay then live, `Last-Event-ID`, `: keep-alive`, `event: end`, 404/410/400).
  New `PauseStage` (Governance) between rate limit and budget gives the global pause its own trace stage (the budget
  stage keeps its own check for legacy compositions).
- **T05/T06 audit v3.** `AuditEntry` + `Attempts[]` (`AuditAttempt`: id, connection, model, outcome, error class,
  duration, cost, basis, reserved, overrun), `Trace[]` (`AuditTraceStep`), `ReservedUsd`, `SettledUsd` (additive,
  nullable, written only when a configuration is bound). `Usage.CostUsd` = total settled for the request.
- **T06 fallback (ADR-0013).** `ProviderCallStage` iterates `PolicyOutcome.Candidates` when a configuration and a policy
  retry block exist (legacy compositions: one attempt on `Route`, unchanged). Before EACH attempt the gate re-checks the
  pause and reserves. Failure classes via `IProviderFailureClassifier` (Adapters interface, BCL default
  `BclProviderFailureClassifier`; Host `SdkProviderFailureClassifier`: `ClientResultException.Status`,
  `AnthropicApiException.StatusCode`, `AnthropicIOException` ⇒ connect, HttpRequestException status/connect,
  SocketException ⇒ connect, TimeoutException / provider-side cancellation ⇒ timeout; 429 ⇒ `429`; 500/502/503/504 ⇒
  `5xx`; anything else ⇒ `non_retryable`). Fallback only for classes in `retry.on`, within `max_attempts` and
  `deadline_ms`, never after content was emitted, never on caller cancellation (rethrown), pause, policy denial or
  non-retryable errors. Health per `connection:model` from every real attempt. `ServedTarget` drives attribution/pricing.
  PII/PHI: verified cloud candidates are excluded by the engine in every mode (test added).
- **T06 cache.** v2 key = SHA-256 over application, team, profile, FINAL classification, config version, the ordered
  approved candidates (connection/model), the enforced output bound and the messages; hits after auth/shape/classify/
  policy, before rate-limit/pause/budget/provider (a hit is served during a global pause), `no_call`, `CacheHit`, original
  served model; only successful complete responses are stored.
- **T06 savings.** `FinOps.Build(…, savingsBaseline)` + `FinOpsSummary.Savings` (`SavingsSummary`): present only when the
  configuration's `savings_baseline` is a priced connection/model; route-avoided and cache-avoided reported separately;
  estimated/unknown records excluded and counted. Without a baseline every savings figure is 0 and `savings` is null.
  The 4-argument `FinOps.Build` keeps its pre-v2 behaviour (FinOpsTests unchanged).
- **T06 archive.** `AuditSegments` + `ArchiveApi`: `GET /admin/audit/segments` (current + `<audit>.yyyyMMdd-HHmmss.archived`
  siblings, opaque ids `current` / `a-<16 hex>`), `GET /admin/requests?limit=&cursor=&segment=` (moved out of Program;
  same `RequestsPage` + `segment.id/label`, `next_cursor`; unknown segment / malformed cursor ⇒ 400),
  `GET /admin/requests/{id}/export` (no `PromptPreview`, attempts, trace, config version, integrity with line + hashes).

### Files

```
src/Covenant.Core/Execution.cs                 new  TraceStates, TraceReasons, PipelineTraceEvent, AttemptRecord, IAttemptGate,
                                                    AttemptAdmission, AttemptCharge, RequestBounds
src/Covenant.Core/InferenceContext.cs               DenialCode + Deny(code:), AttemptGate, Attempts, ServedTarget, Active/Reserved/
                                                    SettledUsd, TraceSink, Trace(), StageOrder, CompleteTrace(), CompactTrace()
src/Covenant.Core/Pipeline.cs                       per-stage trace instrumentation, TracedStages, CompleteTrace in ExecuteAsync
src/Covenant.Core/Decisions.cs                      AuditEntry v3 Attempts/Trace/ReservedUsd/SettledUsd; AuditAttempt; AuditTraceStep;
                                                    AuditEntryKinds.BudgetRecovered
src/Covenant.Adapters/ProviderLayer.cs              IProviderFailureClassifier, BclProviderFailureClassifier, fallback loop
src/Covenant.Governance/BudgetLedger.cs        new  LedgerOps, LedgerRecord, ILedgerStore, InMemoryLedgerStore, BudgetCheck,
                                                    BudgetPeriods, BudgetLedger
src/Covenant.Governance/BudgetGate.cs          new  BudgetScopes, ReservationEstimate, BudgetAttemptGate
src/Covenant.Governance/Budget.cs                   PauseStage; BudgetStage v2 constructor + reservation path (legacy path kept)
src/Covenant.Governance/Attribution.cs              served target; unreported usage ⇒ reservation bound under a v2 gate
src/Covenant.Governance/Cache.cs                    v3 key (candidates + enforced output bound)
src/Covenant.Governance/Audit.cs                    CompleteTrace before writing; Attempts/Trace/Reserved/Settled; Usage = settled
src/Covenant.Host/BudgetLedgerStore.cs         new  FileLedgerStore, LedgerJsonContext, BudgetStateView & co, BudgetViews, BudgetApi
src/Covenant.Host/Trace.cs                     new  TraceEventView/SnapshotView, TraceJsonContext, TraceBuffer, TraceHub, TraceApi,
                                                    TraceCompletion
src/Covenant.Host/InferenceEndpoints.cs        new  ChatCompletions (shared OpenAI wire + DenialResult), PlaygroundRequest,
                                                    ConsoleSubmissions, PlaygroundApi
src/Covenant.Host/AuditSegments.cs             new  AuditSegments, SegmentView, RequestExport, ExportIntegrity, ArchiveApi
src/Covenant.Host/FailureClassifier.cs         new  SdkProviderFailureClassifier
src/Covenant.Host/Program.cs                        ledger boot (open/legacy import/recovery), PauseStage + BudgetStage v2 + classifier,
                                                    shared /v1 handler, /v1/messages trace, status from the ledger + savings,
                                                    reset without ledger reset, /admin/requests moved, new Map* calls
src/Covenant.Host/StatusContracts.cs                SavingsSummary, FinOpsSummary.Savings, FinOps.Build overload
src/Covenant.Host/ConsoleBackend.cs                 RequestsPage.NextCursor, SegmentInfo.Id/Label
src/Covenant.Host/OpenAiContracts.cs                CovenantJsonContext += PlaygroundRequest, List<BudgetStateView>, BudgetRequestsView
src/Covenant.Host/FileAuditSink.cs                  AuditJsonContext += List<SegmentView>, RequestExport
src/Covenant.Host/Evidence.cs                       LedgerReplay doc (no longer used by the Host)
tests/Covenant.Tests/RuntimeV2TestKit.cs       new  SettableClock, StubChatClient, Failures, V2Rig (pure v2 pipeline), RestartableHybridFactory
tests/Covenant.Tests/FallbackTests.cs          new  (+ ProviderFailureClassifierTests)
tests/Covenant.Tests/BudgetLedgerTests.cs      new  (+ BudgetPipelineTests)
tests/Covenant.Tests/CacheIsolationTests.cs    new
tests/Covenant.Tests/ConsolePlaygroundTests.cs new  (+ Playground helpers)
tests/Covenant.Tests/TraceTests.cs             new  (+ TraceHubTests)
tests/Covenant.Tests/BudgetHostTests.cs        new
tests/Covenant.Tests/ArchiveExportTests.cs     new  (+ SavingsTests)
docs/ui-api-contract.md                             v2 "Implementation details" for console inference, trace, budgets, archive,
                                                    audit v3; new "Status FinOps savings"
```

### Endpoints

`POST /admin/playground/requests` · `GET /admin/requests/{id}/trace` · `GET /admin/requests/{id}/trace/stream` ·
`GET /admin/budgets` · `GET /admin/budgets/{id}/requests` · `GET /admin/audit/segments` ·
`GET /admin/requests?limit=&cursor=&segment=` (extended) · `GET /admin/requests/{id}/export`.

### Tests (each fails if its guarantee is removed)

- `BudgetLedgerTests` — 16 parallel contenders behind a `Barrier` for the last allowance ⇒ exactly one reserves;
  all-or-none across global/team; settle/release exactly once (double settle ignored, release after settle ignored,
  journal = reserve/settle/release); overrun settled in full + flagged; per-scope UTC periods (Sep vs Oct vs all-time,
  daily) and a renamed budget sees the same accrual; month rollover; restart replay + `settle-recovered` + no double
  recovery; torn tail quarantined and repaired, then two more appends + restarts; corrupt middle line refuses start;
  legacy import once.
- `BudgetPipelineTests` — bound = UTF-8 bytes + 16 and enforced output bound; reported settle by scope (global, team,
  application); unreported ⇒ bound; overrun flagged in attempt evidence; hard cap ⇒ 403 at `budget` before any provider
  call and zero-priced local still runs; warn-only never blocks; application-scope budget enforced; cancellation ⇒
  reserve/settle/release exactly once, nothing open; unpriced cloud excluded under a hard budget (+ validator code).
- `FallbackTests` — fallback on 429 / timeout / 500 / 502 / 503 / 504 / connect, both attempts charged (failed one at its
  bound, `estimated_failed_call`), settled total = audit `Usage.CostUsd` = ledger spend, health per connection:model,
  attempt-level pause/budget trace; no fallback on 400/401/403/404/422, on a class outside `retry.on`, beyond
  `max_attempts`, past `deadline_ms` (hanging attempt ⇒ `timeout`), after the first streamed chunk (partial preserved,
  failure recorded), on a pinned model override; streaming failure before any chunk may fall back; pause tripped between
  attempts ⇒ refused at `pause`, first attempt still charged; pause / policy refusal ⇒ no attempt; caller cancellation ⇒
  `cancelled`, no fallback, no health penalty, settled once; sensitive request whose local primary fails never reaches
  cloud (even with a cloud target smuggled into the policy); enforced output bound sent to the provider.
  `ProviderFailureClassifierTests` — status/transport/timeout/inner-exception mapping.
- `CacheIsolationTests` — hit: no provider call, `no_call`, $0, `CacheHit`, original served model, no attempts, ledger
  unchanged, later stages skipped `cache_hit`; isolation across applications/teams, profiles, configuration versions and
  output bounds; hit served during a pause while a miss is refused at `pause`; current policy enforced before a hit;
  failures and partial streams never stored.
- `ConsolePlaygroundTests` — buffered run as demo-app (same wire as /v1, `EntryPoint=console`,
  `OperatorRef=shared-admin-token`, application/team/profile/version, trace, attempts, reserved/settled, no key/admin
  token/provider secret in the payload); streaming SSE; disabled / not-console-testable / ungranted / unknown application
  ⇒ governed 403 at `auth` with audit and no provider call; 7 invalid bodies ⇒ 400 with no audit record (FIFO proof);
  duplicate `client_request_id` ⇒ 409 with the original id and no second call; admin token required; `/v1` still needs a
  key (admin token ⇒ 401) and records `EntryPoint=v1`; submission window/bound.
- `TraceTests` — replay after completion (monotonic seq, all ten stages running→passed, attempt event with target, no
  content), snapshot complete, compact audit `Trace[]`; `Last-Event-ID` resume; refusal at auth ⇒ every other stage
  skipped `not_reached` (and in the audit summary); a subscriber joining mid-request gets seq 1 (`auth running`) then live
  events until `event: end`; 404 unknown, 400 malformed, 401 without token. `TraceHubTests` — 410 after retention vs 404
  unknown; 64-event and request-count bounds (oldest completed evicted ⇒ 410); completed buffer immutable, waiters woken.
- `BudgetHostTests` — `/admin/budgets` + `/{id}/requests` (+404/401) and status from the ledger; `POST /admin/reset`
  keeps spend; rename + period + cap-below-spend keeps accrual, blocks paid work at `budget`, local still served;
  restart replays and recovers an open reservation (`budget.recovered` event); no ledger ⇒ one-time legacy import, not
  repeated on the next boot.
- `ArchiveExportTests` — segments current + archived (opaque ids, no paths), paging an archived segment by cursor,
  current-segment default; unknown ids / paths / traversal rejected (requests and export); malformed cursors rejected;
  export excludes the opted-in prompt preview and the response, carries version/attempts/trace/integrity and still works
  after an archive. `SavingsTests` — hidden without a baseline; route vs cache split; estimates excluded.

### Existing assertions changed

None. No existing test file was edited. Behaviour changes that existing tests could observe (all spec-mandated):
- Host pipeline gains a `pause` stage (`PauseStage`); the kill-switch denial text and `StoppedAtStage = "pause"` are
  unchanged.
- Configured requests now send `max_tokens` = the enforced output bound (default 1024) to providers.
- `/admin/reset` no longer clears spend (ADR-0014 — archive never reopens a budget).
- `Usage.CostUsd` of a configured request = total settled (equal to the old value for single-attempt requests; for
  failed calls the estimate is now the reservation bound instead of chars/4 — both `estimated_failed_call`, both > 0).
- `/admin/status.finops` savings figures are 0 (and `savings` null) when no priced `savings_baseline` is configured.

### Decisions / deviations from the contract

1. **Trace 404 vs 410:** the earlier v2 text said "404 unknown/expired"; implemented 404 = never traced, 410 = expired or
   evicted (as the task and ADR-0013 require). Contract updated.
2. **Every request is traced** (`/v1` too), not only console requests — same bounded hub.
3. **Failed / cancelled attempts are charged their full reservation bound** (input bound + full output bound), the
   conservative "possibly billed" estimate the task specifies — higher than the pre-v2 chars/4 estimate.
4. **Deadline semantics:** no attempt starts after `deadline_ms`; an attempt still waiting for its response (or, streaming,
   its first content) at the deadline is cancelled as `timeout`; once streamed content flows the deadline stops applying
   (a delivered answer is not cut off; the connection timeout governs).
5. **Cache key** includes the ordered approved candidate list (stricter than "connection + model": a response is reused only
   for the same eligible route set). Keyed on the primary's enforced output bound.
6. **Ledger file failure** halts the process (`Environment.FailFast`), consistent with the audit sink.
7. `max_attempts` beyond the eligible candidates is simply bounded by the candidates (no repeated attempts on one target).
8. `SegmentInfo.path` is still returned by `GET /admin/requests` (it was already there for the current segment); segment
   ids in the archive APIs are opaque and caller paths are never accepted.

### Risks

- **Unbuilt.** Highest compile risk: RDG on the new lambdas (all typed `async Task<IResult>` or sync `IResult`);
  source-gen for positional records with `IReadOnlyList<>` members in `AuditEntry`; `Anthropic.Exceptions` type names;
  `ClientResultException(string, PipelineResponse?, Exception?)` in a test; collection expressions in tests.
- Host tests boot real Hosts with fsync'd ledger writes (slower on CI disks).
- The ledger keeps per-key idempotency for the last 200 000 closed attempts and the last 20 000 charges for drill-down.
- Streaming fallback uses the same SSE response: only possible before the first delta, so headers are not yet committed.

### Verifier commands (not executed)

```
dotnet build Covenant.sln -c Release --disable-build-servers
dotnet test tests/Covenant.Tests -c Release --disable-build-servers --filter "FullyQualifiedName~BudgetLedgerTests|FullyQualifiedName~BudgetPipelineTests|FullyQualifiedName~FallbackTests|FullyQualifiedName~ProviderFailureClassifierTests|FullyQualifiedName~CacheIsolationTests|FullyQualifiedName~ConsolePlaygroundTests|FullyQualifiedName~TraceTests|FullyQualifiedName~TraceHubTests|FullyQualifiedName~BudgetHostTests|FullyQualifiedName~ArchiveExportTests|FullyQualifiedName~SavingsTests"
dotnet test tests/Covenant.Tests -c Release --disable-build-servers     # full suite: all ~490 pre-existing tests unchanged and green
dotnet test tests/Covenant.Tests -c Release -p:SkipConsolePublish=true -o <fresh-temp-output>
```
Manual (isolated port, `Config:Seed=demo-hybrid`, fresh `Audit:Path`): `POST /admin/playground/requests` with
`{"application_id":"demo-app","profile_id":"public-demo","messages":[{"role":"user","content":"Reply OK."}],"max_tokens":5,"stream":true}`
→ SSE + `X-Covenant-Request-Id`; `GET /admin/requests/<id>/trace/stream` → events then `event: end`;
`GET /admin/budgets` → `spent_usd` > 0, `reserved_usd` 0; `POST /admin/reset` → budgets unchanged;
`GET /admin/audit/segments` → `current` + one `a-…`; `GET /admin/requests/<id>/export` → no prompt text; restart →
budgets unchanged; resubmit the same `client_request_id` → 409.

---

## T08 UI — editable admin pages and evidence workflows

**Status: NOT EXECUTED.** Written without a .NET SDK (none in the authoring sandbox), so nothing below has been
compiled, unit-tested or opened in a browser. The budget ledger, fallback/trace and audit-archive endpoints were being
implemented concurrently by the backend agent (uncommitted at the time of writing); every UI call to them is
**integration pending** until the verifier steps below pass. Do not cite T08 as complete before then (spec §7).

### Files

```
src/Covenant.Ui/Logic/Money.cs              new  USD with significant digits for sub-cent charges ($0.000184, $1.23,
                                                 "<$0.000001", never $0.00 for non-zero; null → "Not reported"); caps,
                                                 per-1M prices, shares
src/Covenant.Ui/Logic/ConfigDraft.cs        new  ConfigCopy (deep copy/canonical JSON via UiJsonContext), ConfigDraft (base
                                                 version + ETag + draft, content-based dirty tracking, Reset), ChangeSummary
                                                 (ids added/removed/changed per section — never values), EditOutcomes
                                                 (HTTP status+body → Applied | Stale | Invalid | PreconditionRequired |
                                                 Unauthorized | NotFound | Failed), IssueIndex (field-path lookup), IssueHelp
src/Covenant.Ui/Logic/ImpactText.cs         new  impact preview wording (changed routes, headline, needs-attention)
src/Covenant.Ui/Logic/PolicyRoutes.cs       new  PolicyText (labels, strategy names, pause semantics), PolicyRoutes
                                                 (structural eligibility, definite summary lines, refusal conditions,
                                                 add-target options, up/down reorder), HybridDefaults (legacy → hybrid draft)
src/Covenant.Ui/Logic/BudgetView.cs         new  BudgetText (period/scope/warning/enforcement/thresholds), BudgetRequests
                                                 (lenient parser for /admin/budgets/{id}/requests)
src/Covenant.Ui/Logic/SegmentScope.cs       new  segment labels/coverage sentence; EvidenceCompleteness (pending /
                                                 unavailable / recorded / verified / unverifiable)
src/Covenant.Ui/Logic/ConnectionDrafts.cs   new  ConnectionForm/ModelForm (add/edit, discovery merge, inline problems,
                                                 ToConnection/ApplyTo), ConnectionRules, ConnectionHealth (probe vs governed
                                                 call, one summary + timestamps)
src/Covenant.Ui/Logic/ConnectionLogic.cs         old wizard helpers removed (CheckApplied, ConfigBlock, RouteReview)
src/Covenant.Ui/Logic/CostText.cs                Usd → Money.Usd
src/Covenant.Ui/Logic/RequestFilter.cs           + Application filter / Applications(); time label "All loaded records"
src/Covenant.Ui/Models/ConfigContracts.cs        + ConfigErrorResponseDto, ImpactRow.changed, ValidationResult.active_version,
                                                 Simulate request.config / response dry_run, config_version, application_id,
                                                 profile, explanation, eligible[].boundary; BudgetState period_start/end;
                                                 BudgetRequestDto, AuditSegmentDto
src/Covenant.Ui/Models/Contracts.cs              + status.config_version; requests.next_cursor; segment id/label/events;
                                                 catalog config_version/connections[]/egress_allow_list (+ CatalogConnectionDto,
                                                 CatalogConnectionModelDto); model connection_id/boundary/key; attempt
                                                 ReservedUsd/Overrun; UiJsonContext registers ConfigErrorResponseDto,
                                                 List<AuditSegmentDto>
src/Covenant.Ui/Services/ConfigEditor.cs    new  shared editor service (load/version/ETag, draft, validate, save, conflict,
                                                 compare, history, rollback, reload after activation)
src/Covenant.Ui/Services/AdminApi.cs             + RawResponse/SendRawAsync, validate/put/rollback (raw), versions, version n,
                                                 simulate, budget requests (raw JSON), segments, requests page, export
src/Covenant.Ui/Services/RequestsViewState.cs    rewritten: one segment's pages, cursor, bounded (2 000), segments list
src/Covenant.Ui/Services/Fmt.cs                  Usd/UsdTotal delegate to Money
src/Covenant.Ui/Services/Interop.cs, wwwroot/js/interop.js   prefGet/prefSet for allow-listed console preferences
src/Covenant.Ui/Services/PlaygroundState.cs      StreamingDefault (browser-local preference)
src/Covenant.Ui/Layout/MainLayout.razor          applies the stored "stream-default" preference once
src/Covenant.Ui/Program.cs                       registers ConfigEditor
src/Covenant.Ui/Components/EditorBar.razor  new  View/Edit bar: "Applied version vN", history drawer + inline rollback
                                                 confirm, Preview impact / Save & apply / Cancel, 412 conflict (Reload /
                                                 Compare), 422 banner, discard dialog, unsaved-change guard
src/Covenant.Ui/Components/ImpactPreview.razor new Host validation + impact rendering
src/Covenant.Ui/Components/FieldIssues.razor new inline errors/warnings for a field path
src/Covenant.Ui/Components/ConnectionEditor.razor new add/edit form (Ollama and cloud) with probe/discovery
src/Covenant.Ui/Components/PolicySimulator.razor new dry-run simulator (applied or unsaved draft)
src/Covenant.Ui/Pages/Connections.razor          rewritten (7-step "edit a file" wizard removed)
src/Covenant.Ui/Pages/Policies.razor             rewritten (route view, editable list, mode, simulator)
src/Covenant.Ui/Pages/Budgets.razor              rewritten (ledger state, edit, drill-down)
src/Covenant.Ui/Pages/Settings.razor             rewritten (operational / preferences / diagnostics / maintenance)
src/Covenant.Ui/Pages/Requests.razor             rewritten (segments, cursor pages, application filter, drawer, export)
src/Covenant.Ui/Pages/Evidence.razor             rewritten (decisions + evidence completeness first, integrity separate)
src/Covenant.Ui/wwwroot/css/app.css              T08 block (editor bar, impact, connection cards, route view, budgets, …)
tests/Covenant.Tests/ConsoleLogicTests.cs        removed the three wizard tests; "$0.003" expectation; + MoneyTests,
                                                 ConfigDraftTests, EditOutcomeTests, ImpactTextTests, PolicyRoutesTests,
                                                 SegmentScopeTests, BudgetTextTests, ConnectionFormTests,
                                                 ConnectionHealthTests, RequestFilterApplicationTests
tests/Covenant.Tests/ContractRoundTripTests.cs   + validate request/response, write answers (412/422/200) → outcomes, version
                                                 history + rollback body, simulator (with/without draft) + dry-run response,
                                                 catalog connections, budget state + contributions, segments + cursor page
```

### Endpoint wiring

| UI | Endpoint | Status |
|---|---|---|
| Every editor page: applied version, draft base, secrets availability, egress list | `GET /admin/config` (ETag) | backend T02 (committed) |
| Preview impact | `POST /admin/config/validate` `{base_version, config}` (200 valid/invalid; `stale_base_version` warning → conflict banner) | backend T02 |
| Save & apply | `PUT /admin/config` + `If-Match` = the draft's base ETag; 200 → reload config, workspace, catalog, status; 412/409 → conflict; 422 → field errors; 428 → reported | backend T02 |
| Version history / rollback | `GET /admin/config/versions?limit=50`; `POST /admin/config/rollback {target_version, expected_active_version}` | backend T02 |
| Conflict "Compare" | `GET /admin/config` (latest) — nothing merged | backend T02 |
| Connections health, registration, allow-listing, secret availability | `GET /admin/catalog` (`connections[]`, model health = governed calls, `local.endpoint_health` = probes of an applied endpoint) | backend T02/T03 |
| Connection Test / Ollama discovery | `POST /admin/providers/probe` | existing |
| Policy simulator | `POST /admin/policies/simulate` (`config` = unsaved draft when chosen; 422 when it does not validate) | backend T03 |
| Simulator applications | `GET /admin/applications` | backend T05 |
| Budget state | `GET /admin/budgets` (404 → pre-v2 counters with an explicit note) | **integration pending (T04, uncommitted)** |
| Budget drill-down | `GET /admin/budgets/{id}/requests` → `{items:[…]}` | **integration pending (T04)** |
| Requests pages | `GET /admin/requests?limit=200&cursor=&segment=` (`segment.id` must echo an archived request, else nothing is shown) | **integration pending (T06)**; current segment works on the committed Host without paging |
| Segment selector / Audit segments | `GET /admin/audit/segments` (404 → "Current segment only") | **integration pending (T06)** |
| Request export | `GET /admin/requests/{id}/export[?segment=]` → raw JSON download | **integration pending (T06)** |
| Archive (Settings → Maintenance) | `POST /admin/reset` (wording from `GET /admin/budgets` presence: ledger ⇒ "budgets NOT reopened") | existing; ledger semantics pending T04 |

### Decisions

- **One shared draft.** `ConfigEditor` holds one draft of the whole configuration, owned by one page at a time; a clean
  draft is released when its page is left, a dirty one triggers the guard (in-app: `RegisterLocationChangingHandler` →
  "Discard unsaved changes?"; reload/close: `NavigationLock ConfirmExternalNavigation`).
- **Save needs a current preview.** "Save & apply" is enabled only when the last `validate` answer was for exactly the
  current draft content, valid, and no conflict is open. No optimistic UI: the success line "Applied version vN" appears
  only after the 200 carrying `version`, and after config/catalog/workspace/status were reloaded.
- **Never overwrite.** A 412 (or the validator's `stale_base_version` warning) shows "Configuration changed since you
  started editing (now vN)" with Reload (discard draft) and Compare (lists server-side vs draft changes). There is no
  force-save.
- **Field errors** are matched by the Host's paths (`connections[i]…`, `policies[i].targets[j]`, `budgets[i]…`,
  `settings.*`) on member/element boundaries; anything no field claims stays visible in the preview list.
- **Connections**: discovered tags start unselected; local prices default to an explicit, editable `zero-marginal-local`
  0/0; cloud prices are never assumed (unpriced cloud ⇒ blocking problem). Secret references are chosen from the refs the
  Host reports (availability only); a known-unavailable ref blocks the form with the server-side fix
  (`Secrets__name` env var + restart). Not-on-allow-list probes explain `Providers:ProbeAllowList` /
  `Providers:LocalEndpoints` under Technical details. Connection ids are immutable on edit. No delete action (not required;
  avoids `unknown_connection` drafts). Cloud default endpoints are not probed (the probe is OpenAI-compatible GET /models).
- **Health**: summary prefers the governed-call fact (newest of success/failure), then the probe; a reachable probe
  without a governed success reads "Endpoint reachable … · no governed call observed yet" (never "ready").
- **Policies**: the effective-route view and summary lines are computed from the configuration with the engine's
  structural rules (paused, connection enabled, model permitted, local-only boundary, PII/PHI never cloud) and labelled
  as configuration; the simulator (real engine) is the authority for a given request. Cloud models are offered disabled
  with "PII/PHI never route to cloud destinations" for sensitive policies. Reorder is ↑/↓ buttons with aria-labels.
- **Legacy cloud**: "Imported legacy rules" + "Move to Hybrid defaults…" builds a draft (Public → first enabled priced
  cloud model + local fallback; Internal, PII, PHI → first enabled local model) and previews it; refused (draft untouched)
  without a local model.
- **Budgets**: the console never computes spend; all numbers are the ledger's. Zero spend everywhere ⇒ explanatory state
  linking to Models & connections (no flat chart). Infrastructure cost is "not configured — none shown".
- **Archive wording** follows the backend contract: with a ledger, archiving the audit segment never reopens budgets;
  on a Host without `GET /admin/budgets` the dialog says archive ALSO resets pre-v2 counters. Typed environment name
  required.
- **Requests**: `segment=` is sent only when the Host listed segments, and an archived page is shown only if
  `segment.id` echoes the request (an older Host would silently answer for the current segment). Bounded to 2 000 loaded
  records. Rows of an archived segment open from the loaded page (the by-id lookup searches the current segment only).
- **Console preferences**: theme (existing) and the default response mode (`localStorage covenant.pref.stream-default`,
  JS-allow-listed key). Nothing else is persisted.

### Risks / known gaps

1. Not compiled. Highest-risk spots: Razor component parameter lambdas (`OnProbed="@(p => …)"`,
   `OpenChanged="@(open => { … })"`), deconstruction in `@foreach (var (t, i) in …)`, `@bind` on `int?`/`decimal?`
   DTO properties with `@bind:after="Editor.Touch"`, pattern variables in attribute expressions, collection expressions
   in conditionals (`cond ? list : []`), `UiJsonContext.Default.ListConfigVersionDto` / `ListAuditSegmentDto`.
2. The round-trip tests reference the backend's uncommitted T04/T06 types (`BudgetStateView`, `BudgetRequestsView`,
   `BudgetContributionView`, `SegmentView`, `RequestsPage.NextCursor`, `SegmentInfo.Id/Label`,
   `AuditJsonContext.Default.ListSegmentView`). If the backend renames them, fix the test, not the UI shape.
3. Diagnostics shows "Not reported by this Host" for `Config:Path` and the ignored legacy keys (no status field exists;
   the Host logs them at startup).
4. The effective-route view does not know runtime availability or the hard-budget pricing rule beyond "unpriced";
   the impact preview and the simulator (both Host-computed) are authoritative.
5. Rollback's resulting version is predicted as active+1 "or the next free number"; the actual number is shown from the
   Host's answer.

### Verifier steps (not executed)

```
dotnet build Covenant.sln -c Release --disable-build-servers
dotnet test tests/Covenant.Tests -c Release --disable-build-servers --filter "FullyQualifiedName~MoneyTests|FullyQualifiedName~ConfigDraftTests|FullyQualifiedName~EditOutcomeTests|FullyQualifiedName~ImpactTextTests|FullyQualifiedName~PolicyRoutesTests|FullyQualifiedName~SegmentScopeTests|FullyQualifiedName~BudgetTextTests|FullyQualifiedName~ConnectionFormTests|FullyQualifiedName~ConnectionHealthTests|FullyQualifiedName~RequestFilterApplicationTests|FullyQualifiedName~ContractRoundTripTests|FullyQualifiedName~CostTextTests|FullyQualifiedName~ConnectionLogicTests"
dotnet test tests/Covenant.Tests -c Release --disable-build-servers     # full suite
```

Browser (isolated port, `Config:Seed=demo-hybrid` on a fresh `Audit:Path`; both themes; 1280×720 and 1920×1080, then
~900 px):

**H — policy editing**
- [ ] Policies → Edit → on the Public policy press "Pause route" on the primary; summary line becomes
      `Public → <fallback> · no fallback`; Preview impact shows Public before → after; Save & apply → "Applied version vN+1".
- [ ] Playground preset A → served by the fallback; Requests drawer shows the attempts and config vN+1.
- [ ] Pause the remaining route (or "Pause policy") → preview shows Public newly refused → save → preset A refused
      at policy. Resume → save → new version vN+3; preset A served again.
- [ ] Simulator: same prompt against "Unsaved draft" vs "Applied vN" differ while a pause is unsaved; label "Dry run —
      no provider called, nothing recorded"; Requests shows no new record afterwards.
- [ ] A PII/PHI policy never offers cloud models (disabled option with the reason); ↑/↓ reorder works by keyboard.

**J — config recovery / conflicts**
- [ ] Open Budgets → Edit in tab A and tab B; save a cap in B; save in A → banner "Configuration changed since you
      started editing (now vN)"; A's draft kept; Compare lists both sides; Reload discards A's draft. Nothing overwritten.
- [ ] Settings → Edit → prompt preview 900 → Preview shows the `settings.prompt_preview_chars` error inline; Save stays
      disabled; `curl /admin/config` still reports the old version.
- [ ] Version history → Roll back to an older version → confirmation names target and new version → success line shows
      the new version number; history lists it as a rollback of the target.
- [ ] Edit something, click another nav item → "Discard unsaved changes?"; Keep editing stays; Discard navigates. Reload
      the tab with a dirty draft → browser leave-page prompt.
- [ ] Restart the Host → pages show the same "Applied version"; Budgets spend unchanged.

**K — evidence**
- [ ] Audit & evidence: session runs show Pending → "Audit integrity verified (record present, chain consistent)" (or
      Unavailable + Retry); integrity card separate from answers; recent decisions list who / rule+version /
      selected→attempted→served / cost and why; chain/anchors/segments only under the disclosure.
- [ ] Settings → Maintenance → archive (type the environment name) → dialog said budgets are NOT reopened → Budgets spend
      unchanged afterwards; Requests segment selector lists the archived segment; selecting it shows "Archived segment …
      read-only" and its records; "Load older" pages until "All records of this segment are loaded".
- [ ] Request drawer → Export evidence (JSON) downloads `covenant-request-<id>.json` with `content_included:false` and no
      `PromptPreview`.
- [ ] Legacy (pre-v2) audit lines still verify and render "Not recorded" fields.

**L — browser usability**
- [ ] Models & connections → Add Ollama → endpoint → Test (allowed/unreachable/not-allowed messages as specified) →
      tags unselected → select one → price shows zero-marginal-local 0/0 → Preview impact on routes → Save & apply →
      card shows the new connection, "Applied version" bumped. Edit prefills actual values; Disable… → preview → save.
- [ ] Add cloud provider with an unavailable secret ref → red "Secret not set on the server — saving is blocked".
- [ ] Budgets: tiny charges render like `$0.000184`; local-only demo shows the explanatory no-spend state; Requests
      drill-down links open the request drawer.
- [ ] Every page: loading skeleton, error banner with Retry (stop the Host), empty states, disabled controls with reasons;
      keyboard-only: Tab through editor bar, drawers trap focus, Esc closes, focus returns to the opener.
- [ ] No horizontal page scroll at 1280×720 / ~900 px; long model ids and errors wrap.
