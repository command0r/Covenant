# Final launcher verification — November 1, 2026 (Toronto)

Executed by Codex against the actual local build, not inferred from Claude's report.

- Release solution build: 0 warnings, 0 errors.
- Automated suite: 1,102 passed; 0 failed/skipped.
- Self-contained macOS arm64 single-file publish succeeded.
- Normal `OPEN_BROWSER=0 scripts/run-local-demo.sh` published and started on default port 5210 with the existing durable state. Only browser auto-opening was suppressed for controlled UI verification.
- Restarted with `SKIP_PUBLISH=1 OPEN_BROWSER=0 scripts/run-local-demo.sh`: Hybrid v3, 6 applications, 7 policies, 34 request records, and audit integrity retained. Original audit's 18 lines are an exact byte-for-byte prefix; original configuration versions 1 and 2 unchanged.
- No keys printed or committed. Saved provider credentials used server-side. Original history was not replaced by isolated test history.

## Actual normal-instance scenario checks

Synthetic requests through the normal Host's Playground API; actual Ollama, Anthropic and Tavily calls. Audit entries checked independently for effect, model, cache, cost, refusal/no attempt, sanitization metadata and retrieval. First verification harness used the wrong JSON casing; it was corrected and the existing records re-read without repeating paid calls.

| Scenario | Result | Served model | Recorded USD |
|---|---|---|---|
| internal-coding | PASS | qwen2.5-coder:7b | 0.0 |
| public-coding-local | PASS | qwen2.5-coder:7b | 0.0 |
| public-coding-opus | PASS | claude-opus-5-5 | 0.015428 |
| internal-writing | PASS | qwen2.5-coder:7b | 0.0 |
| public-writing | PASS | claude-sonnet-5-5 | 0.00238 |
| local-public-research | PASS | qwen2.5-coder:7b | 0.008 |
| weather-location | PASS | claude-sonnet-5-5 | 0.017804 |
| internal-research | PASS | qwen3.8:27b-mlx | 0.0 |
| sensitive-block | PASS | No provider call | 0 |
| sanitized-external | PASS | claude-sonnet-5-5 | 0.005652 |
| sanitized-refusal | PASS | No provider call | 0 |
| cache | PASS | qwen2.5-coder:7b | 0.0 |
| cache | PASS | qwen2.5-coder:7b | 0 |
| budget-refusal | PASS | No provider call | 0 |

The Toronto weather request included a separate location field and returned Toronto observations, timestamps and retrieved sources. The revised research prompt retrieved the official .NET support policy and answered November 14, 2028 for .NET 10. These are retrieval/answer observations, not a claim of general factual reliability.

## Browser checks

- Real Anthropic catalog discovery, model selection, explicit pricing, validation and save worked on the isolated review instance. 14 models returned; Opus 5.5 selected.
- Final normal instance: mixed Communications Public/Internal workloads and relevant writing examples; original-request comparison after changing both prompt and caller restored the original synthetic prompt and Communications payer.
- Final Usage filters align horizontally; team/application reports separate inference/search charges, local/external calls, cache hits, sensitive signals and sanitization.
- Focused policy dialog and visible application team ownership checked in light theme.
- Presentation mode at 1280×720 and 1920×1080; final light-theme output inspected. Dark-theme Opus and sanitization presentation checked on isolated build. Trace replay progresses with explicitly explanatory pacing and recorded durations.
- Audit integrity refresh checked in browser. Real application-key `/v1/chat/completions` returned 200 through the local route; an unapproved `auto` model pin was correctly refused before using policy selection.
- `/ui/` and nested request route reload returned HTML 200. `/ui` resolves to the console.
- `git diff --check` clean. No commit/push performed; existing edits preserved.

## Scope limits

Sanitization is restricted to the documented structured schema and identifier fields, not arbitrary prose or PHI and not anonymization. Model comparison is explicit, not automatic quality judgement. No automatic prompt rewriting. Search sources and generated claims still need evaluation. OpenAI catalog behavior has automated coverage; live verification used Anthropic credentials. Windows/Linux/NativeAOT were not verified in this pass.

Screenshots: [Usage](2026-11-01-final/usage-1920-light.png), [final presentation](2026-11-01-final/presentation-1280-light.png), [Opus isolated rehearsal](2026-11-01-final/opus-1280-dark.png), [sanitized isolated rehearsal](2026-11-01-final/sanitized-1280-dark.png).
