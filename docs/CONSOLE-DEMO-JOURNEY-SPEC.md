# Covenant: request journey and conference demo iteration

User approved implementation on November 1, 2026. Continue the current dirty checkout; preserve all existing edits. Codex supervises, runs builds/tests/browser/live provider verification. Claude owns implementation. No commits, pushes, subagents, credential access, runtime configuration edits or server restarts by Claude.

## Outcome

A newcomer can explain who requested work, what data may leave, where it ran, why, and who pays. Deliver actual working UI and backend, not simulated outcomes. Keep existing presentation/replay, versioned configuration, audit compatibility, strict policy/budget checks and focus/dirty-dialog behavior.

## 1. Vocabulary and request journey

Rename composer caller selector to “Send a request as”. Application = authenticated consuming system, owned by a team; Playground exercises an authorized test identity. Explain once through an accessible info/popover, not paragraphs. Keep owner/team visible in compact chips.
Separate data classification (Public/Internal/Sensitive), workload/task (coding/research/writing) and actual destination (Local/External). Public permits external processing, does not require it. Show short route preview before Run and actual route/reason after. External uses consistent amber/purple distinct from local teal; red means sensitive detected/blocked. Icons/text required, not color alone. Examples must not silently change caller or bypass permissions; filter by grants. Mixed internal/public workloads on same application supported, with one visible selection and clear defaults.

## 2. Focused policy and provider configuration

Retain separate Routes / Applications / Workloads tabs and focused dialogs. Make primary route + fallback plain language. Team ownership prominent on application cards/list. Hide advanced conditions in disclosure. Preserve concurrency/validation protections.
Cloud model catalog discovery for existing Anthropic and OpenAI adapters, server-side credentials only, fixed/allow-listed provider endpoint, bounded timeout/pagination, safe errors; no arbitrary URL proxy/SSRF. Provider-specific parsing stays in adapters where feasible. Model discovery is NOT approval or pricing. Catalog selection populates model ID; operator explicitly adds permitted model and configured price. Retain manual entry on discovery failure. Never invent prices or model IDs. Tests fake HTTP endpoints/handlers.

## 3. Usage and evidence

Add Usage navigation/page, backed by authenticated server aggregation of recorded evidence/accounting, with team/application/workload grouping and period/time-window filter. Clearly identify included time window/segments and limitations; avoid unbounded rescanning hot paths. Requests, estimated inference/search/total charge, cache hits, external calls, sensitive detections, redactions and refusals. Refused/zero-cost requests count. Unknown costs are unknown, not zero. Do not call local compute free. Do not double-count retries/search/ledger/audit. No raw prompts/PII in aggregation. Team trend is a signal, not employee misconduct proof.
Result/request detail hierarchy: outcome + destination/reason + owner/team + cost split + expandable evidence. Colored semantic chips, concise labels, long technical text in details. Audit integrity explicitly not correctness/compliance.

## 4. Explicit redaction policy (new ADR BEFORE code)

Default behavior stays fail closed. Add opt-in restricted structured-data redaction demonstration using synthetic data, not generic guaranteed anonymization. Original classification is never rewritten as Public to bypass policy. Dedicated approved policy authorizes only a transformed payload; retain original classification plus redaction decision/counts/types, policy version and transformed classification/egress decision separately in audit. Configure via focused UI, with preview of transformed request before external execution for demo. Original sensitive values must never appear in routine logs/audit preview/cache or tool query. No reverse mapping stored; no reinsertion into response. Sanitize entire canonical message set, not last user message only. Re-check transformed payload. Unsupported/unresolved sensitive fields, conflicting instructions, PHI/free-text cases not safely covered, missing policy => refuse external. Search must not receive original payload; initially disable search for redacted requests if needed. All external attempts/fallbacks use transformed payload. Cache partition includes policy/version/transformation identity; no unsafe cross-context reuse. Cost/attribution/refusal/cancellation audited. Explicit opt-in does not silently change all PII/PHI routing. Document detector limits and that de-identification cannot be guaranteed. Synthetic examples use known structured fields (e.g. email/phone/SSN) with no real person data. Tests prove raw values never reach external adapter, web search, audit previews or cache; prove unsupported and PHI cases fail closed.
The user's explicit request supersedes older blanket 'PII never public' prose only for a dedicated sanitized-payload policy. Update affected invariant docs precisely; never weaken the default or silently lower classification.

## 5. Coding escalation and public local search

Implement explicit approved coding escalation via a workload/route policy and visible user action after a local result (not fabricated automatic difficulty/quality detection). Public synthetic coding can compare or escalate to the configured stronger external model; Internal must not gain external permission because it is difficult. Require explicit reclassification/confirmation before external submission of previously internal content and re-run detection/policy; do not silently change caller grants. State what is measured vs operator choice. If existing routing strategy supports deterministic complexity, document heuristic honestly, never present as quality guarantee.
Public research can use local inference + approved Tavily web search independently of model boundary; show external retrieval badge/query destination and separate search charge. Do not grant all teams search automatically. Existing cache demonstration remains, policy-scoped, fresh/time-sensitive web results bypass cached stale answers as existing rules require.

## 6. Docs and three-demo plan

Update README with two compact, aligned inline Mermaid diagrams: deployment trust boundaries (callers -> Covenant -> approved providers + Ollama; credentials/network controls; direct SaaS browser bypass outside Covenant scope) and request journey. Name actual patterns: canonical proxy, policy middleware, approved model routing/fallback, retrieval augmentation, exact cache, attribution/audit. Do not imply prompt rewriting or learned quality routing exists.
7-minute demo: (1) public research local inference + Tavily, (2) synthetic public coding local then permitted stronger external escalation, (3) sensitive structured input denied then explicit redaction policy preview/approved external sanitized call. Repeat safe request for cache in ~30s. Synthetic prompts only. Missing setup must have actionable setup links, not misleading runnable examples.
Simple user guide begins 4-5 sentences; deep details separate. Scope excludes automatic prompt rewriting/quality-preservation claims and endpoint/device-management implementation.

## Acceptance and handoff

Provide meaningful tests for governance, catalog security, scoped usage accounting, redaction and UI state transitions; no live providers in automated tests. Keep source-generated JSON/AOT compatibility. Report exact files/remaining checks honestly. Implement in stages and report when ready for Codex compile review; continue through full scope. Screens must work at1280x720 and1920x1080, both themes, keyboard dialog navigation. Response may scroll; caller/destination/cost/evidence/trace remain visible in presentation mode. No secrets in logs/screenshots.

### Reviewer notes

Redaction must apply in the shared canonical pipeline for authenticated `/v1` consumers as well as console requests. Preview is never authorization; execution independently validates the bound configuration and transformed request. Review all provider attempts/fallbacks, web queries, prompt previews, request fingerprints and cache keys for original-value leakage. Existing `/v1` search support is limited; document capabilities honestly.

Live discovery verified by Codex: saved Anthropic credential lists `claude-opus-5-5`, `claude-sonnet-5-5`, and other model IDs. Do not hardcode this list as discovery. Opus standard global list-price estimate verified on November 1: $4 input / $20 output per 1M tokens, https://platform.claude.com/docs/en/models/opus-5-5/overview . Codex will configure the demo and verify actual calls after implementation; do not mutate runtime state yourself.
