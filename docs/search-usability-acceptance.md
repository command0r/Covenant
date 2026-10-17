# Search and console usability verification

Verified on 2026-10-17, macOS arm64, .NET SDK 10.0.107. Branch: `feat/console-search-usability`, based on console v2 PR #7.

## Executed checks

- Release solution build: passed, no warnings or errors.
- Final full regression suite with coverage: **889 passed**, none failed or skipped. Measured coverage: **95.70% line / 83.68% branch** (the repository's coverage settings).
- Clean `SkipConsolePublish=true` build/test in a separate output directory: 888 passed. This preceded the final additional test for local-model synthesis from web sources; production code is identical.
- Self-contained single-file `osx-arm64` publish: passed. Ran that executable on isolated loopback port 5297.
- Published GET routes: `/ui` → 302 `/ui/`; `/ui/` and `/ui/requests/example` → 200 HTML. HEAD is not supported by these routes.
- Real Ollama `qwen2.5-coder:7b` request through the published appliance: answered READY, recorded the local destination, charge basis and audit evidence. Search Off was correctly traced as skipped.
- Browser search attempt with search disabled: refused at Web search; Provider and Attribution skipped, no external call.
- All eight main pages checked at 800px width without document-level horizontal overflow. Main pages also checked at 1280×720 in light and dark themes.
- Playground at 1280×720: prompt, Web sources, location and Run button visible with main scroll position 0. Run button bottom was 655px.
- Presentation mode: 1280×720 light/dark has two trace rows; 1920×1080 dark has one row of eleven stages. The short-response panel has no overflow or overlap.
- Checked cloud editor defaults (OpenAI → Anthropic updates its default credential reference), Settings/header theme synchronization in both directions, route navigation scroll reset, dashboard answer display, and Enter-key access to the Requests drawer.
- Browser error log: no errors in the checked final session.

## Defects corrected during verification

- Missing search-stage name mapping prevented its events appearing under the expected trace identifier.
- Search test compilation errors and outdated trace expectations.
- Connection resets after dispatch incorrectly treated as definitely unbillable; now conservatively estimated. DNS failures remain no-call.
- Already-cancelled requests could reach search reservation; now checked before reservation and client dispatch.
- Default provider secret selector empty on fresh installations.
- Theme header and Settings selector disagreed after changes.
- Dropdown padding/chevrons, stretched segmented controls, checkbox dimensions, field/button alignment and grid shrinkage.
- Requests table forced unnecessary horizontal scrolling; budget Requests button split across lines.
- Navigation preserved the old page's scroll position.
- New search controls did not fit the initial Playground viewport and were hidden in the dashboard composer.
- Demo presets reused previous web-search settings; applying a preset now turns search Off and clears location.
- Added durable search/tool evidence to the Requests drawer as well as Playground details.
- Eleven trace stages created an extra presentation row and squeezed result facts; responsive columns corrected.

## Verification limits

No live Tavily credential or paid OpenAI/Anthropic credential was configured in the verification process. Search wire format, local/cloud synthesis paths, source handling, failures, cancellation, permissions, cache bypass and budget accounting were exercised using test transports/adapters. This does **not** constitute a live Tavily acceptance test or a live paid-provider test.

Search is explicit and off by default. The implementation grounds a model using retrieved web snippets; it is not a dedicated weather API or a guarantee of answer accuracy. Classification recognises configured patterns and trusted profile baselines; it is not comprehensive data-loss prevention.

The user's running instances on 5201/5210 and the Ollama server were not stopped or reconfigured. Changes require rebuilding/restarting the intended Covenant instance. NativeAOT and Linux runtime validation were not repeated in this follow-up.

Local build logs and screenshots are in the git-ignored `docs/verification-2026-10-16/usability/` directory. Usage: [Console quickstart](console-quickstart.md); search setup: [Web search](web-search.md).
