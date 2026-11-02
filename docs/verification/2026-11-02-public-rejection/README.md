# Sensitive workload and diagram verification

Verified on 2026-11-02.

- Release suite: 1,274 passed, zero failed or skipped.
- Six live cases: hyphenated SSN, compact SSN and identifiable PHI, each accepted on the approved Internal local route and rejected on Public before any provider attempt. See live-results.json.
- Additional live checks: ordinary Public text reached Anthropic; sensitive Public content with required web search was rejected before search or inference.
- Final normal launcher: scripts/run-local-demo.sh on port 5210, existing saved state and audit history preserved. Public demo workload configured through the validated, audited configuration API to reject detected sensitive data (version 4). Internal routing unchanged.
- Actual browser: selected Demo application and the Internal sensitive example, filled and ran it successfully on Ollama. Switched the unchanged prompt to Public. The paired example label updated, and the next run returned HTTP 403 at classification, with evidence recorded and no provider call. Screenshot: public-rejected.png.
- Both architecture diagrams exported with Draw.io to SVG and PNG and visually inspected. Editable .drawio sources are included.

Detection remains pattern-based and cannot guarantee detection of every possible sensitive input. Reject is an explicit workload setting, not an inherent meaning of the word Public. Existing profiles default to routing unless configured otherwise.
