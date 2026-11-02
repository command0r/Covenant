# November 1 verification

Executed against the local checkout, not merely reviewed source. Changes remain uncommitted alongside earlier work.

## Build and automated checks

- Release solution build with warnings as errors: passed, zero warnings/errors.
- Full .NET suite: 1,033 passed, none failed or skipped.
- Launcher tests: 2 state-migration tests and 6 credential-loading tests passed; shell syntax passed.
- Single-file macOS ARM64 publish and startup passed.
- A final presentation-only change uses the existing short cost formatter and retains the detailed cost in its tooltip; the solution was rebuilt successfully after this change.

## Runtime and browser checks

Isolated browser verification used port 5212, a private copy of the original demo state, and real configured providers. Test-only budget/cache changes were not copied back to 5210.

- Internal coding went to Qwen coder; switching the same form to Public selected and called Anthropic. The example label and old route preview cleared correctly.
- Internal research called local Qwen 27B with application/team attribution.
- Public research/weather used the supplied Toronto location, Tavily sources and Anthropic. Missing weather location disabled Run with an actionable message. Retrieval succeeded; this is not a guarantee of factual answer accuracy.
- Request history refreshed after navigation and showed newly completed runs.
- An isolated application budget refused work before a provider attempt, with attribution and evidence retained.
- Route dialog dirty dismissal required explicit discard; focus returned to the originating Edit button.
- A scoped budget save updated only the selected budget. A concurrent configuration update caused an open settings editor to refuse its stale save; discard/reopen loaded the current version.
- Route preview responded to form edits and discarded stale results. Configuration-version invalidation also has a regression test.
- Replay advanced through recorded trace events. Audit integrity checks returned fresh timestamps and valid chains.
- At 1280×720: Overview Run remained visible; route actions aligned; both local and cloud connections were visible. Presentation mode retained caller, destination, charge, evidence and trace while the answer scrolled. Light and dark themes were inspected. Full-HD presentation layout was also inspected.

## Actual launcher state

The normal launcher migrated legacy `/tmp/covenant-v2-demo-local-only` to the Git-ignored `.covenant-local/demo-local-only` directory without deleting the legacy copy. All six existing audit/configuration files matched their pre-migration SHA-256 values. The restarted 5210 instance retained configuration v2 in Hybrid mode, seven original/current verification requests and two configuration events, and a valid audit chain. A final browser request on the migrated 5210 instance also completed through Anthropic and Tavily with Toronto supplied, three sources, attribution, cost and evidence visible at 1280×720. This appended an eighth request. Both verification servers were then stopped to leave the ports free for the normal launcher.

The directory's `local-only` suffix identifies its initial seed; it does not override the saved Hybrid configuration. Normal restart must not use `FRESH=1` or switch `PROFILE` to a different state directory.

## Limits

No Linux or NativeAOT publish was performed in this pass. No forced provider-outage fallback rehearsal was performed in this pass. Cloud charges shown are estimates based on reported usage and configured prices. Audit verification checks recorded chain integrity, not answer correctness or regulatory compliance.
