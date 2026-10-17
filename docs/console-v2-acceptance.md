# Console v2 — handoff and acceptance record (spec §9)

Branch `feat/console-v2` from `main` 9371dbe. Not merged. **The author's environment cannot run `dotnet`.** Every
"Observed" entry below was executed by the independent verifier (Codex) on macOS arm64 against the commit named,
with logs/screenshots in the git-excluded `docs/verification-2026-10-16/v2/` folder. "Claimed" = implemented, not
yet independently observed. Final source checkpoint: `9e71d66`; subsequent verifier changes affect only the
launcher and documentation and have their own executed checks below. No source patches were required for the
780-test run.

## 1. Commits by task

| Task | Commits |
|---|---|
| T01 architecture | f1ebd12 (ADR-0011..0014, v2 contract, migration notes, config model), 79ec4b0 (review corrections) |
| T10 CI | 2f2a28a (single `test` check; docs-only vs code decided in one job) |
| T02/T03 config, profiles, policies | a929d84, 8bb8923 (review round 1 incl. torn journal) |
| T04/T05/T06 budgets, console inference + trace, fallback/cache | a16bed8, 1ad07c4 (review round 2) |
| T07/T08 UI | 3d579fe, 96deed5, 959ce13 (UI round 2), 2a7d5e5 (UI round 3) |
| T09 demo | 45760e9 (launcher, explicit hybrid seed prices, runbook) |

Docs: ADR-0011..0014 (+ ADR-0001 status update), `docs/ui-api-contract.md` (v2), `docs/migration-v2.md`,
`docs/demo-runbook.md`, `docs/console-v2-progress.md` (per-round defect → fix → test), this file.

## 2. Executed verification (observed by the verifier)

| Commit | Observation |
|---|---|
| 2f2a28a | Release build 0 warnings/errors; 310 tests pass (baseline) |
| 8bb8923 | Release build + 491 tests pass incl. torn-journal regression; osx-arm64 self-contained single-file publish; `/ui`→302 `/ui/`; `/ui/`, nested route, Blazor JS → 200 |
| 1ad07c4 (+2 test-only name fixes, applied in 959ce13) | Release build + 723 tests pass; HTTP stream suite: partial-then-error ⇒ Deny/no fallback; length finish relayed + Truncated; post-content stall ⇒ timeout, partial kept, no fallback; empty completion ⇒ 200/Allow. Cache collision probes (delimiter, output-bound) ⇒ calls=2, no false hit. 503/429/timeout fallback with two charged attempts. Ledger migration crash window ⇒ refuses start; clean import preserves spend. Stable pagination; early trace (provider Running before first token) |
| 959ce13 | Release build + **761 tests pass**, no verifier patches; osx-arm64 single-file publish pass; coverage measured at an earlier checkpoint: line 95.57 % / branch 83.24 % |
| browser (959ce13 era) | connect once; budget edit → preview → apply (v10) with spend preserved; policy split/save (v31); pause primary + approved slow fixture (v32); cache TTL edit (v33) + repeated request = cache hit, no provider call, $0, skipped stages; live provider Running before first token; Ollama discovery → select `qwen2.5-coder:7b` → save → real synthetic sensitive request: Pii (US SSN pattern), served by local Qwen, zero configured provider charge, evidence found; presentation fits 1280×720; Escape restores focus; header local health correct. Screenshots: `qwen-real-{1280,1920}-{dark,light}.png`, `dashboard-1280-dark.png`, `presentation-1280-dark.png`, `round2-*.png`, `budget-applied-v10.png` |
| 9e71d66 + verifier launcher corrections | Full solution Release build: **0 warnings, 0 errors**. Embedded-console test run: **780 passed, 0 failed/skipped**; coverage **95.59% line / 83.65% branch**. Fresh-output `SkipConsolePublish=true`: **780 passed**. Self-contained single-file publishes: **osx-arm64 and linux-x64 passed** (Linux cross-publish only, not executed). Published Mac routes: `/ui/`, nested route and Blazor JS all 200. Launcher Local-only starts, real Qwen synthetic request returns 200, restart retains audit/config; no credentials in normal stdout, private files 0600. Spaced JSON catalog, exact model matching, occupied-port and missing hybrid key/price guards pass. |
| UI round 3 (2a7d5e5, included in 9e71d66) | Created Public profile, granted existing application, previewed and saved v34; real Public and PII Qwen requests succeed. Final classification/escalation visible; previous-result marker and cache original-model provenance verified. Presentation screenshots at 1280×720 and 1920×1080 in light/dark; all trace stages and timings visible. At 900×700 no horizontal page overflow. Evidence: `round3-presentation-*.png`, `final-cache-presentation.png`. |
| security probes | invalid enums / null settings ⇒ validation errors (no 500); removed application's key ⇒ 403; app key cannot read admin APIs; admin token cannot authorize inference; browser disconnect ⇒ Cancelled, one estimated charged attempt, no fallback, reservation released |
| NativeAOT | **Fails**: IL2104/IL3053 from Anthropic 12.8.0 and Neo4j.Driver 6.2.1 — documented, not suppressed (ADR-0001 status update, runbook §1a). Supported artifact: self-contained single file |

## 3. Status by task (claims vs observations)

| Task | Implemented (claimed) | Executed verification | Remaining gap |
|---|---|---|---|
| T01 | ADRs 0011–0014, contract, migration | reviewed by verifier | — |
| T02 | repository, journal, validation, impact, rollback, legacy import | tests + HTTP | — |
| T03 | profiles, baseline escalation, policy engine v2, simulator, modes | tests + browser split/save | — |
| T04 | ledger, reservations, periods, settle/release, migration | tests + HTTP (concurrency 1×200/5×403, archive preserves spend) | — |
| T05 | console endpoint, submissions, trace hub/stream | tests + browser live trace | — |
| T06 | fallback, attempt evidence, cache identity, savings, archive/export | tests + HTTP fixture | real cloud fallback not exercised (see §5) |
| T07 | shell, composer, Playground, presentation | browser (both themes, 1280/1920) | round 3 independently verified (see above) |
| T08 | editors, requests archive, evidence export | browser (budget/policy/settings/connections) | profile/grant editor independently verified |
| T09 | launcher, seeds, runbook | seed tests pass; actual Local-only launcher/restart + Qwen request; credential/model/port guards verified | real cloud smoke remains opt-in and not run |
| T10 | single CI check; this record | final full build/test/coverage, clean skip-console tests, Mac publish/run, Linux cross-publish | GitHub CI and Linux runtime execution not run (branch not pushed) |

## 4. Verifier commands for HEAD

```
dotnet build Covenant.sln -c Release --disable-build-servers
dotnet test tests/Covenant.Tests -c Release --disable-build-servers --collect:"XPlat Code Coverage" --settings tests/coverlet.runsettings
dotnet test tests/Covenant.Tests -c Release -p:SkipConsolePublish=true --artifacts-path <fresh-temp>
dotnet publish src/Covenant.Host -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o <fresh-temp-publish>
scripts/run-local-demo.sh            # isolated port 5210, own state; then runbook §2–§3 in the browser
```
Route checks with GET (not HEAD): `curl -sS -D - -o /dev/null http://localhost:5210/ui` → 302 `/ui/`; `/ui/` and
`/ui/requests/x` → 200 text/html.

## 5. Known limitations (stated, not hidden)

- **Detector coverage:** `RegexDataClassifier` recognises a US-SSN pattern and MRN/diagnosis/patient-id keywords only.
  It is a demonstration detector, not DLP; Public is reachable only through a trusted, granted Public profile, and
  detection can only escalate. It cannot prove content is non-sensitive.
- **Price and usage uncertainty:** costs are configured-price × provider-reported usage. Unreported usage and
  failed attempts are charged a conservative estimate (UTF-8 byte bound); a hard budget is an *estimated*
  enforcement bound, not an invoice ceiling. Local "$0" is zero configured provider charge, infrastructure excluded.
  Savings are shown only with a configured priced baseline and are not evidence of equal answer quality.
- **Answer quality:** Covenant governs routing, cost and evidence; it does not evaluate answers. "Audit integrity
  verified" means the hash chain is internally consistent, nothing about correctness.
- **Identity:** console actions are attributed to the shared admin credential (`OperatorRef = shared-admin-token`);
  no individual operator attribution until real IAM.
- **Real cloud smoke:** BLOCKED / NOT RUN — needs owner-supplied credentials, synthetic prompts and a small cap.
- **NativeAOT:** blocked by dependencies (above). **Linux-x64:** cross-publish passed on macOS; the artifact has not been executed on Linux.

## 6. Final verifier handoff

Claude completed through `9e71d66`. While the Mac was locked and Claude was idle, Codex corrected the launcher
and runbook directly, then exercised those changes. Launch with `scripts/run-local-demo.sh` (port 5210);
`OPEN_BROWSER=0` keeps it headless. The existing instance on port 5201 was not changed.

Local implementation and verification are complete within the stated limits. This is a reviewable feature branch,
not a production certification: no merge/push, real paid cloud call, Linux runtime run or NativeAOT success is claimed.
The scope does not include semantic caching, prompt rewriting, answer-quality evaluation or individual-admin IAM.
