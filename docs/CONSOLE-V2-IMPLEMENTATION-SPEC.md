# Covenant console v2 implementation specification

Owner: Alex Kaziuka. Prepared 16 October 2026.
Baseline: `main`, PR #6 merge `9371dbe` (310 local tests passing at review).
Audience: Claude Code implementing the next iteration, and Codex reviewing it.

## 1. Assignment and scope

Turn Covenant into a usable governance control plane and a credible conference demonstration. Implement the work below; do not deliver another static prototype or a visual reskin over read-only configuration.

The intended operator journey is:

Connect once → configure approved local/cloud models → define routes and budgets → run a request as an application → watch the actual decision and execution → inspect cost and evidence.

Read root and area-specific `CLAUDE.md`, existing ADRs, `docs/ui-api-contract.md`, and the current implementation first. This specification supersedes the read-only UI requirements in the earlier UI brief/tasks. Update the old documents to point to this specification so contradictory instructions do not persist. Preserve the existing .NET/Blazor architecture and embedded single-appliance packaging. Do not migrate to React or add an external database/service solely for this redesign.

The root statement 'we are not building a gateway' means avoid unrelated provider breadth. It does not exclude the governed routing, fallback, cost attribution and request inspection explicitly required here. Update stale product-status wording and record the clarified scope.

Create a new feature branch from current main. Preserve unrelated user work. Make small conventional commits. Do not automatically merge this iteration: deliver a reviewable branch with executed verification results.

Required now: editable connections, routing policies, budgets and operational settings; a usable request workspace; authenticated test-application selection; real processing traces; approved fallback; exact cache visibility; cost and evidence workflows; visual and responsive improvements.

Not required in this iteration: general-purpose enterprise IAM, additional providers beyond OpenAI/Anthropic/Ollama, model training, autonomous agents, semantic caching, RAG ingestion, or automatic prompt rewriting/compression. Do not imply these exist. Quality-preserving prompt optimization cannot be promised without task-specific evaluation. Provide the cost/route/cache explanations and evaluation fixtures described below instead.

## 2. Non-negotiable behavior

- No match, invalid active configuration, or no approved destination means refusal. Never silently send sensitive data to cloud.
- Keep administrator, application and provider credentials distinct internally. Fix the workflow rather than treating an admin token as an application key.
- Provider secrets stay server-side. Existing environment/vault references are sufficient; adding a browser secret editor is not required. UI can choose a configured secret reference and show whether it is available, never its value.
- Configuration changes are authenticated, validated, versioned, auditable and reversible. A failed update must leave the last valid configuration active.
- Every request binds to one configuration version. No mixture of old policy/new pricing/new provider settings during one request.
- Preserve legacy audit verification. New fields are additive; absent historical data is 'Not recorded', never invented.
- No fabricated stage timing, savings, model health, evaluation scores or fallback demonstrations. Simulation and replay must be labeled.
- Never clear existing accounting or audit data to make a demonstration work. Use an isolated demo profile and files.
- Preserve NativeAOT-safe serialization and existing project dependency direction.

## 3. Product decisions for this implementation

### Classification and egress

Current `RegexDataClassifier` never emits Public. Fix this deliberately; absence of a regex match does not establish that content is public.

Use an application-bound, server-configured workload/data profile with an explicit baseline classification. Requests may select only profiles granted to their application. Content detection may escalate classification but may never lower the trusted baseline. Arbitrary request headers, prompt instructions or a client-side Public dropdown cannot bypass this rule. Missing profile defaults to Internal for compatibility and receives the restrictive default policy below.

Default demonstration policy:

- Public: approved economical cloud model, with an explicitly configured approved fallback.
- Internal: approved local model.
- PII and PHI: approved local model; never a cloud fallback in the demo policy.
- No eligible destination: refuse and explain why.

A deliberately Public demo profile uses synthetic public content. The regex classifier remains a limited demonstration detector; describe its coverage honestly. Do not claim it prevents all sensitive-data leakage. Keep the detector behind its existing interface.

Preserve explicit local-only mode, where cloud configuration cannot execute. Display that mode prominently. Add a clear hybrid mode for local and approved cloud routes. Migrate legacy cloud mode explicitly: show its existing effective rules in the migration preview rather than silently changing production routing. Fresh demo configuration uses the defaults above. Do not require OpenAI credentials for an Anthropic-only setup.

### Scope of policies

Each policy has a stable ID, name, classification/profile applicability, enabled state, ordered eligible targets, routing strategy, and explanation. Application/profile grants further restrict eligibility. Use clear precedence and reject ambiguous overlaps; do not silently choose an arbitrary matching rule.

In v2, support fixed-primary routing plus ordered fallback. Preserve the existing length-threshold strategy as an explicitly named 'Length-based heuristic', with configurable threshold and targets; do not call it quality-aware routing. No automatic switch to a supposedly 'best' model based on invented quality rankings.

A paused policy denies its matching traffic. A paused route removes that target from eligibility; the next explicitly approved target may be used. A global provider-call pause retains its existing defined semantics, including cache behavior, and the UI must explain them. Do not turn a pause into a governance bypass.

## 4. Backend design and contracts

Write ADRs before implementing these changes: runtime configuration lifecycle; authorized console inference; routing/fallback and request trace; budget reservations/accounting periods. Use the next available ADR numbers. Document updates to earlier decisions explicitly.

### 4.1 Versioned runtime configuration

Implement a single-appliance configuration repository with immutable version snapshots and atomic persistence. Avoid introducing an external database. Persist only non-secret configuration and secret references. The ADR must define crash recovery, write ordering, concurrency, bootstrap precedence and how the activation event is durably auditable.

Core entities:

- Connection: ID, display name, provider kind, endpoint, enabled state, secret reference, permitted models, transport settings and probe status.
- Model: stable composite identity using connection ID plus exact model ID; display name, context/output constraints, pricing reference. Bare model names are not globally unique.
- Application/test identity: stable ID, team, enabled state, granted workload profiles and whether console testing is allowed. Never expose its API credential.
- Workload profile: ID, name, trusted baseline classification and application grants.
- Policy and route: applicability, active/paused state, ordered destinations, strategy and bounded retry settings.
- Budget: scope, currency USD, cap, period, warning thresholds and enforcement mode.
- Operational settings: cache TTL, permitted preview retention, trace retention, presentation preferences where appropriate.
- Pricing: connection/model identity, input/output rates, effective version/date and source/basis. Unknown price must remain unknown.

Proposed API surface (finalize in `docs/ui-api-contract.md`; reuse existing equivalents rather than duplicate them):

- `GET /admin/config`: redacted active configuration, version and ETag.
- `POST /admin/config/validate`: candidate plus base version; returns field errors, warnings and impact summary, without mutation.
- `PUT /admin/config` with `If-Match`: atomically apply the validated candidate; return new version. Stale edit returns 409/412; validation failure 422. Require version preconditions.
- `GET /admin/config/versions`: bounded history and change summaries.
- `POST /admin/config/rollback`: target historical version plus expected active version; validate and activate as a NEW version.
- Existing catalog/probe/status endpoints extended compatibly for multiple connections.

Impact preview must identify affected classifications/applications, unavailable destinations, pricing gaps and requests that would now be refused. Applying a configuration must not reset spend or audit history. Existing in-flight calls finish using their captured snapshot; global emergency pause remains separately defined. Dispose retired provider clients only after their in-flight use ends.

Probe configured/allow-listed endpoints only, with bounded timeout/body size and no redirects. A policy edit must not silently expand the probe/egress allow-list. Revalidate at apply and execution; never forward Covenant credentials to providers. Endpoint validation must cover scheme, destination and redirect behavior.

### 4.2 Console inference without a second pasted key

Add an authenticated console-inference endpoint, for example `POST /admin/playground/requests`, accepting application ID, allowed workload profile and the canonical inference input. The backend resolves the authorized test application and invokes the same governed pipeline as normal inference. Do not call providers directly from the UI or create an ungoverned shortcut.

A shared admin credential can currently represent only a shared console operator; record that honestly. Do not fabricate individual operator attribution. Record operator/session reference, application, team and entry point independently. Disabled/non-test applications and ungranted profiles must be refused. Preserve normal `/v1` application-key authentication unchanged.

Return the existing request ID/correlation semantics and support streaming/cancellation. A browser reload must not replay inference. Authorization failures and validation failures need clear UI outcomes; distinguish rejection before the pipeline from governed refusal.

### 4.3 Actual request trace

Introduce canonical stage/attempt events with request ID, configuration version, monotonic sequence, stage ID, attempt ID when relevant, state, server timestamp, duration and bounded reason code. States: pending, running, passed, refused, failed, cancelled, skipped. Do not put prompt/response content or credentials in trace events.

Expose an authenticated per-request event stream and bounded snapshot/replay lookup. Specify how very early events are retained so subscribing after submission does not miss them. Handle reconnects, deduplication, retention expiry and out-of-order delivery. Preserve existing OpenAI/Anthropic response compatibility; internal trace belongs in admin APIs.

Persist a compact final trace/attempt summary with audit evidence. Detailed transient event retention must be bounded. Audit persistence may lag response completion: show 'Evidence pending', retry for a bounded interval, then 'Evidence unavailable' if necessary. Never display verified evidence before it exists.

### 4.4 Routing and fallback

Select only policy-approved, enabled, application-authorized destinations within the classification boundary. Explain primary selection and each attempted fallback. Availability is separate from policy eligibility.

Allow bounded fallback for explicitly configured transient errors such as connection failure, timeout, 429 or selected 5xx responses. Do not retry on policy denial, client cancellation, global pause or arbitrary provider 4xx errors. Do not fail over after response content has already been emitted to the client. Record partial output and terminal failure accurately.

Recheck relevant budget/pause conditions before each attempt. Account for potentially chargeable failed attempts. Cap total attempts and total deadline. Never interpret an ordered allow-list alone as authorization for unbounded retries.

### 4.5 Budgets and cost

Support global, team and application scopes with atomic reservation across all applicable caps. Define period boundaries in UTC and persist the period identity independently of audit segments. Audit rotation must not reopen a budget. Preserve historical spend across configuration updates and restarts.

Estimate a conservative reservation using configured prices and enforced input/output bounds. Settle against reported usage, or retain an explicit conservative estimate when usage is unavailable. Release unused reservation exactly once on completion/cancellation. Handle provider failures, fallback attempts, process crashes and concurrent requests without free capacity or double charging. If a provider cannot support a reliable bound, label enforcement as estimated rather than promising an exact invoice cap.

Unpriced paid routes must not silently consume $0 under a hard monetary budget: reject eligibility until priced or use a clearly documented explicit conservative rate. Local zero marginal provider charge remains distinct from infrastructure cost.

Changing a cap below current spend does not erase spend; block new paid work. Raising a cap is audited. Use configurable warnings, default 80% and 95%. Show actual monetary precision appropriate to tiny charges rather than rounding all requests to $0.00.

Savings: label 'Estimated provider charge avoided', define the counterfactual approved baseline and calculation, and distinguish cache reuse from route selection. Hide the metric when the baseline is missing or incomparable. It is not total business savings and not evidence of equal answer quality.

### 4.6 Exact caching

Retain exact caching and explain it. Scope entries to effective authorization, application/team isolation as appropriate, classification, relevant configuration version, connection/model identity, request parameters and content. Configuration changes must not expose results authorized only under prior settings. Never cache denials, failures or partial streams. A cache hit still requires current authentication/policy checks and appropriate audit attribution. Document its interaction with global pause and budget enforcement.

## 5. UI specification

Use the existing Blazor components and CSS; improve them rather than building an unrelated second console. Existing mockups are visual references, not authority over these operating requirements.

### Shell and shared patterns

Sidebar: Overview, Playground, Requests, Models & connections, Policies, Budgets, Audit & evidence, Settings. Each has a consistent small icon and text label; active page is obvious. Use locally bundled/licensed icons or original SVGs, and update provenance for reused assets. No external icon/font CDN.

Header: environment, prominent Local-only/Hybrid mode, compact health state, global pause/resume with icon and explicit label, theme sun/moon with accessible action label. Replace the 0/1-second counter with 'Live'; show age only when stale and expose exact timestamp in details. Do not announce every timer tick via aria-live.

Use one accent plus consistent semantic colors: success, warning, failure, informational/local/cloud distinctions. Always pair meaning with text/icon. Consistent field widths, aligned labels/actions, readable typography and compact spacing. Avoid giant empty cards and repeated instruction paragraphs. Move API paths, ADR IDs, raw config keys and diagnostic prose into expandable details.

Editable pages use clear View/Edit state, Save/Cancel, inline validation, unsaved-change warning, visible applied version and stale-edit conflict handling. No success toast before the backend confirms activation. Loading/error/empty/stale/disabled states must all be implemented.

### Overview

Compact health/readiness summary, useful spend/requests metrics, recent outcomes and modest activity chart. Missing setup should link to the exact action needed. Do not equate 'no recent errors' with 'demo ready'.

Provide a compact preset/prompt composer with application/profile selection and an explicit Run action. The same component/state should be reused with Playground. Alternatively a 'New request' shortcut opens a focused composer; do not label a navigation-only link 'Run'. One click runs a selected ready preset; merely opening a page never submits a request.

### Playground

Primary two-pane workspace: prompt on the left, streamed response on the right. Always-visible Run/Cancel action area. Above prompt: 'Run as' application and allowed workload profile. Show the resulting classification separately; do not confuse trusted profile with detected classification.

Advanced disclosure: model override, workflow/use case, output cap and streaming controls. Overrides remain policy constrained. No permanent application-key field in the normal console workflow.

Below/alongside the response: compact outcome, actual destination, duration, token usage and cost basis. A horizontal/compact trace animates real stage events. Clicking a stage opens its reason and timing. Skipped stages are visibly skipped. Put full request facts and evidence in a drawer; do not repeat all information in three panels.

Safely render Markdown without executable HTML. Copy response and inspect raw output as secondary actions. Cancel preserves the partial response and truthful terminal status. Session history selection never resubmits a request.

Presentation mode shows only prompt, response, route/trace and key cost/outcome facts. It must be usable at 1280x720, not a normal page with overflow hidden. Escape exits; focus returns sensibly. Respect reduced-motion preferences.

### Models & connections

List existing connections first. Each shows provider icon/name, local/cloud boundary, enabled state, endpoint summary, configured models and health. Actions: Add, Edit, Test, Disable/Enable. Editing prefills actual values. Discovery never silently approves every discovered model.

Ollama setup: endpoint → bounded discovery/test → select exact model tags → review affected routes → save/apply. No seven-step wizard that ends by telling users to edit a file. Show an actionable error if host access is denied or Ollama is unreachable. Never assume Open WebUI itself is the inference server.

Cloud setup: provider, endpoint where supported, configured secret reference, model IDs and prices. Explain missing server-side secret configuration with concise instructions; never present an unusable Save as success.

Distinguish last connection probe from last successful inference. One clear summary, expandable supporting timestamps; no contradictory 'ready' and 'never succeeded' without explanation.

### Policies

Use a colored route view plus a compact editable list, not repeated paragraphs and tables of the same facts. Show classification/profile → primary destination → approved fallback(s), state and strategy. Label cloud/local explicitly.

Actions: edit targets, reorder fallback, pause/resume route, pause/resume policy, preview impact, save. Explain the difference between paused policy and paused route in context. A policy simulator returns a clearly labeled dry-run decision without calling a provider or recording fake spend. It must use the real policy engine.

Replace 'Public requests may use local:qwen…' with definite effective state, e.g. 'Public → Qwen · Local-only mode' or 'Public → configured cloud model · fallback: …'. Refusal conditions belong in the detail panel.

### Budgets

Show period, spend, reserved amount, remaining amount, cap and warning state by scope. Edit caps/thresholds/period settings with impact preview. Drill into contributing requests. Show provider cost versus optional local infrastructure estimate separately.

Do not make historical charts from fabricated seed values. Empty local-only spend gets an explanatory state and a connection/configuration link, not an enormous flat chart. Budget period rollover and audit archive are different operations.

### Requests and Audit & evidence

Requests: useful outcome/model/team/time filters, clickable rows, compact readable costs, request details drawer with keyboard focus management. Clearly indicate current-segment/snapshot scope. Add bounded cursor pagination and archived-segment selection/search so old evidence is reachable; do not imply loaded-snapshot filtering searches all history.

Audit & evidence: lead with request decisions and evidence completeness. Explain who, which rule/version, selected/attempted/served destination, cost and why. Show 'Audit integrity verified' independently of answer quality. Put chain hashes and anchors behind details. Export a selected request/report with version, attempts, costs and integrity status; default export excludes prompt/response content. Do not accept arbitrary filesystem paths in archive APIs.

### Settings

Group editable operational settings, console preferences, diagnostics and maintenance. Theme/presentation preferences are browser-local; provider/policy/budget configuration belongs to the server. Explain restart-only settings explicitly. Save only changes that are actually supported.

Separate archive maintenance from accounting changes. Destructive or accounting-affecting maintenance requires a precise confirmation with current environment and impact; normal harmless preference edits do not need confirmation dialogs.

## 6. Layout and accessibility acceptance

Test both light/dark themes at 1280x720 and 1920x1080, 100% zoom. Test a narrower responsive layout as well. Core actions and outcome must remain visible without scrolling nested panels. One main page scroll is acceptable; long answer/detail content may have an intentional scroll area. Do not hide inaccessible content to pass a screenshot check.

No horizontal page scrolling, clipped controls or overlapping fields. Keyboard-accessible navigation, forms, menus and drawers; visible focus, Escape dismissal, focus restoration. Icons have accessible names; color is not the sole signal. Dynamic announcements report meaningful state changes only. Long model IDs/errors wrap without breaking layout. Sanitize server/provider errors before rendering.

## 7. Implementation tasks and dependencies

T01 — Architecture and contracts
Read current source, write ADRs and migration plan, update API contract and stale instructions. Record field schemas, atomicity, auth, classification semantics, failure behavior and budget periods. Deliver reviewable design before dependent code; proceed within this specification without repeatedly asking routine design questions.

T02 — Configuration repository and migration (depends T01)
Implement immutable snapshots, validation, version conflicts, activation/rollback, redacted APIs and recovery. Preserve legacy startup configuration via explicit import/migration. Add tests for invalid edits, stale edits, interrupted persistence and request snapshot isolation.

T03 — Connections and classification/policies (depends T02)
Multiple approved connections/models, independent provider enablement, trusted profiles, restrictive demo defaults, clear mode migration and editable policies. Include simulator and route pause behavior. Tests must prove Public is reachable legitimately and clients cannot downgrade sensitivity.

T04 — Budget periods and reservations (depends T02)
Implement scope enforcement, persistence/recovery, pricing identity, settlement and audited cap changes. Include deterministic concurrency, failure/cancellation, restart and archive independence tests.

T05 — Console request execution and trace (depends T01/T03)
Authorized test-application endpoint, shared governed pipeline, event stream/snapshot, final evidence and disconnect/reconnect behavior. Application credentials never return to the browser.

T06 — Fallback/cache/cost explanations (depends T03/T04/T05)
Bounded approved fallback, attempt-level evidence and charges, cache isolation/invalidation, explicit selection reasons and defensible savings display. Test streaming failure and no cloud fallback for sensitive data.

T07 — UI shell and request workspace (contract-first; complete after T05)
Implement navigation/icons/theme/live status, dashboard composer, focused Playground, real trace, drawers and presentation mode. Remove nested scrolling and repetitive diagnostic prose. All actions use real APIs.

T08 — Editable admin pages and evidence (depends relevant backend tasks)
Connections, Policies, Budgets, Settings editors; validation/conflicts/apply feedback; request archive navigation and evidence export. Complete all loading/error/empty states. No disabled placeholder feature disguised as completion.

T09 — Demo scenarios, documentation and packaging
Update the local demo launcher and runbook without logging secrets unnecessarily. Add isolated Local-only and Hybrid demo profiles. Do not modify the user's active 5201 process/configuration. Make server-side credential setup clear. Verify embedded console packaging and deep links. Include screenshots from the implemented app, not mockups.

T10 — Acceptance and CI
Fix the duplicate CI gate: docs no-op and real build currently share a check name, and mixed code/docs changes can trigger both. Provide a single unambiguous required result that runs real tests whenever relevant code changes. Perform the acceptance matrix below and report evidence.

Do not declare T07 complete while actions depend on mocks. If UI/backend work is split, mark integration as pending until it actually works.

## 8. Demonstration and verification matrix

Use synthetic bounded tasks such as extracting an explicitly supplied ticket priority into a fixed JSON schema. Do not ask the model to invent medical notes. Each scenario has known expected governance behavior and an independently checkable answer condition.

A. Public/cloud: authorized Public profile; eligible economical cloud model; actual usage/cost attributed to correct team/application.
B. Sensitive/local: synthetic PII/PHI marker escalates Public baseline and reaches approved Qwen locally. Attempted explicit cloud override is refused.
C. Missing local: sensitive request with no eligible local destination is refused; no cloud call occurs.
D. Approved fallback: isolated stub provider fails before output; next approved route runs; both attempts and charges/estimates appear. Test 429, timeout, 5xx and non-retryable failures.
E. Streaming failure: provider fails after first content chunk; no second provider silently continues the answer; partial output/failure preserved.
F. Budget: deterministic requests reserve/settle across scopes; cap blocks correctly; changing cap does not lose spend. Concurrent requests cannot each claim the same remaining allowance.
G. Cache: repeat an exact eligible request; no provider call; current policy enforced; zero new provider charge and explicit cache provenance. Policy/model changes do not reuse incompatible results.
H. Policy editing: pause primary route and observe approved fallback; pause all eligible routes/policy and observe refusal. Resume restores eligibility with a new version.
I. Global pause/cancellation: correct existing pause semantics, cancelled request not mislabeled provider failure, exactly-once settlement and final evidence.
J. Config recovery: stale browser edit rejected; invalid configuration leaves active version unchanged; restart restores active configuration and accounting; rollback creates new version.
K. Evidence: old and new audit formats verify; archive search and export work; UI distinguishes pending, unavailable and verified evidence.
L. Browser usability: new operator connects once, selects an authorized app and runs without another token; edits a connection/policy/budget without a terminal; operates at both target sizes/themes with keyboard.

Unit/integration tests use mock adapters, never paid providers. Real Ollama/cloud smoke tests are a separate opt-in rehearsal using owner-supplied credentials, synthetic prompts and an explicit small spending cap. If unavailable, mark these BLOCKED/NOT RUN; do not fabricate results or claim full demo readiness.

Run the current equivalent of:

    dotnet build Covenant.sln -c Release --disable-build-servers
    dotnet test tests/Covenant.Tests -c Release --disable-build-servers --collect:"XPlat Code Coverage" --settings tests/coverlet.runsettings
    dotnet test tests/Covenant.Tests -c Release -p:SkipConsolePublish=true -o <fresh-temp-output>
    dotnet publish src/Covenant.Host -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o <fresh-temp-publish>

Verify /ui redirects once to /ui/, /ui/ serves the embedded app, nested route reloads work, static assets load, and publish output contains the current UI. Use an isolated port. Linux/NativeAOT checks run on a supported runner and are reported separately. A successful ordinary publish is not proof of NativeAOT compatibility.

If dotnet cannot execute in your environment, report that immediately and continue implementable work. Do not call an unbuilt branch complete. Provide exact commands and expected observations for the verifier, with a clear 'not executed' status.

## 9. Required handoff

Deliver:

1. Branch/commit list and changed files grouped by task.
2. Updated ADRs, API contract, migration notes and demo runbook.
3. Task status table: implemented / executed verification / remaining gap. Separate claims from observations.
4. Test and publish logs, browser acceptance results and actual screenshots at required sizes/themes.
5. Short demo walkthrough for A/B/C/D/F/G/H, including expected trace and cost/evidence behavior.
6. Known limitations, explicitly including detector coverage, price/usage uncertainty and answer-quality scope.

No generic 'all done' statement based on unit tests alone. The release gate is a working operator journey: edit → apply → request → visible decision → cost → evidence, including refusal and failure paths.
