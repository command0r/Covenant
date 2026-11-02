# Sensitive routing verification — 2026-11-02

Claude implemented; Codex reviewed, requested corrections, and verified the compiled application and browser.

## Executed against this change

- Final Release test build: **1,205 passed**, zero failures/skips. Includes host/embedded-console build. Log: `/tmp/covenant-sensitive-final-tests.log`.
- Independent classification probe: **23/23 passed**. Compact/separated/Unicode/partial/masked SSNs, structured fields, escaped values, identifiable health information across messages, and benign controls. Output included here.
- Normal launcher published a self-contained single file on isolated port 5212 using a copy of saved configuration v4. Existing user instance/state on 5210 was not modified. Launch log: `/tmp/covenant-sensitive-launch.log`.
- **10/10 live API checks passed**: six sensitive cloud-pin variants refused, three sensitive local requests served by Ollama, and a sensitive search attempt refused before search/model calls. Live audit outcomes included in `live-results.json`. Unit integration tests independently count adapter/search calls, including incomplete inspection and fallback refusal.
- Browser: Demo application → explicit external sensitive example → Use example → Preview route → Run example. Server preview and actual evidence both report a block. Switching to Internal removes the example-injected model pin, preserves the prompt, marks the old preview stale, and the next run is served locally. The previous result retains its original Public snapshot.
- Browser: expected and actual schemes each have exactly three boxes. Presentation mode tested at 1280×720 in light/dark and 1920×1080 in light. Route, outcome, evidence, and trace fit at 1280×720. Screenshots included.
- Overview measured Setup checks and Activity at identical **322.398px** heights at both desktop resolutions. Screenshot included. Responsive stacking is implemented but mobile browser coverage is not claimed.
- Final `git diff --check` clean.

## Corrections made during review

Independent probes initially found misses for compact identifiers, structured/escaped keys, partial SSN wording, lower-case patient names, and identity/health split across messages. These now have regression coverage. Incomplete inspection explicitly refuses before egress rather than pretending content was classified. Processing bounds are checked across the whole input. Invalid Unicode fails closed. Presentation mode gained the route scheme; redundant result fields and leaking legacy arrow CSS were corrected.

## Limits

This remains deterministic pattern inspection, not comprehensive DLP or clinical de-identification. Unlabelled names/addresses, arbitrary encoding (such as base64), paraphrases and identifiers beyond supported forms can be missed. A no-match result is not proof of public safety. Bare nine-digit numbers are conservatively treated as possible identifiers, which can produce false positives. The separately approved sanitized route only accepts its narrow structured schema; it does not sanitize arbitrary prose, and PHI remains prohibited from external processing.

The prior broader hybrid-model/search/cache/budget rehearsal is documented in the November 1 reports. Those paid cloud scenarios were not all repeated in this focused pass. Linux and NativeAOT publishing were not tested here.

## Start the updated version

Stop your current launcher, run `scripts/run-local-demo.sh` normally (without `SKIP_PUBLISH=1` or `FRESH=1`), and use its newly opened console tab. This rebuilds on 5210 and preserves saved configuration/history.

For a quick demonstration: choose **Demo application**, use **Sensitive data detected: served locally**, preview and run. Then open **More examples**, choose **Sensitive data: explicit external attempt is blocked**, apply and run. Changing its workload to Internal clears the example cloud pin and allows the same content to stay local. Public is a data label that permits approved external processing; it does not force a cloud model.
