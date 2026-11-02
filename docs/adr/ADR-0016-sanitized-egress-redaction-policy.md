# ADR-0016 — Opt-in sanitized-payload egress for structured PII (constrained redaction)

**Status:** Accepted
**Date:** 2026-11-01
**Amends:** ADR-0013 ("PII/PHI ⇒ cloud targets are never eligible, no escape hatch") for exactly one case, described
below. Everything else in ADR-0013 is unchanged.
**Relates to:** ADR-0011 (versioned configuration), ADR-0012 (profiles, trusted baseline), ADR-0013 (policy engine,
cache, trace), ADR-0014 (attempt gate), ADR-0015 (web search).

## Context
The demo has to show the fail-closed default. A request containing a structured identifier (e.g. a US SSN) is PII
and is never sent to an external model. The operator then shows an explicit, versioned alternative: for one named
workload, an external model may receive a transformed payload in which the supported structured identifiers are
replaced by typed placeholders.

This is easy to get wrong:

- "redaction" is often presented as anonymisation;
- the classification is quietly lowered to Public;
- only the last user message is sanitized;
- the original values leak into caches, previews, search queries or fallbacks.

De-identification of free text cannot be guaranteed by pattern matching, and Covenant must not claim it.

## Decision
**A dedicated, profile-scoped policy may authorize a cloud destination for PII, and only for a Covenant-built payload
derived from a bounded structured record. Arbitrary prose is never eligible, even after pattern replacement. Default
behaviour is unchanged and fail-closed.**

1. **Configuration (versioned, explicit opt-in).** `PolicyConfigV2.Redaction` (nullable, default absent) =
   `{ types: [email | phone | us_ssn], tasks: [<allow-listed task ids>] }`. Validation errors unless:
   - the policy's classifications are exactly `[PII]` (never PHI, never Public/Internal);
   - it names at least one workload profile (it is never a global PII rule);
   - every profile it names has **baseline PII**. The workload is dedicated: every request under it is PII by its trusted
     label, so the structured contract applies even when the detector finds nothing (e.g. an e-mail-only record).
     At runtime the engine also refuses a request below PII on a profile named by a redaction policy, so an unvalidated
     configuration cannot fall through to an ordinary (possibly external) policy;
   - `types` is non-empty, contains only supported types and includes every type the detector escalates to PII (today
     `us_ssn`);
   - `tasks` is non-empty and contains only task ids defined by the schema below.

   A PII/PHI policy without `Redaction` that lists a cloud target remains a validation error, as before.

2. **Bounded input contract (`covenant.record.v1`).** A request routed by such a policy must consist of **exactly one
   user message** whose entire content (≤ 4 096 characters) is one JSON object:
   `{"schema":"covenant.record.v1","task":"<task id>","fields":{…}}`. Nothing else is accepted: no system or
   assistant turns, no prose around the JSON, no nested objects or arrays, no duplicate keys, and no extra top-level
   keys.
   - **Identifier fields** (replaced, never sent): `email`, `phone`, `us_ssn`. Each value must match its format
     exactly (anchored), and the field's type must be in the policy's `types`.
   - **Attribute fields** (sent as-is; bounded, non-identifying, domain-neutral service support): `service` ∈
     {subscription, workspace, device, account, storage}, `issue` ∈ {login_failed, billing_question,
     service_unavailable, access_request, data_export}, `channel` ∈ {email, phone, portal, app}, `priority` ∈ {low,
     normal, high}, `days_open` integer 0–365, `amount_usd` number 0–1 000 000 with at most 2 decimals.
   - **No free-text field exists.** An unknown field, a wrong type, a value outside its enumeration or range, or a
     malformed identifier is refused with a specific, actionable reason.
   - **Task.** `task` must be one of the schema's fixed tasks (`triage_case`, `reply_outline`,
     `compliance_checklist`) **and** in the policy's `tasks`. The instruction sent to the model is Covenant's fixed
     text for that task. The caller's text is never forwarded.

3. **Classification is never rewritten.** The final classification stays **PII** for policy matching, the budget,
   the trace and evidence. The engine makes a cloud target eligible for PII **only** when the matched policy carries
   `Redaction`. In that case the outcome is marked *sanitized payload required*. Evidence also records a separate,
   informational **transformed classification**: the detector's own result on the built payload (Internal = nothing
   detected). It is never used to choose a policy.

4. **Redact stage.** Order: `auth → shape → classify → policy → redact → cache → rate_limit → pause → budget →
   search → provider → attribution` (audit wraps). The stage runs in the shared canonical pipeline, so it applies
   equally to authenticated `/v1` callers (profile via `X-Covenant-Profile`) and to console requests. When the outcome
   requires a sanitized payload, it does the following, and refuses before anything costs money or leaves the
   perimeter if any step fails:
   - refuses unless the final classification is exactly PII;
   - parses and validates the envelope (§2);
   - **builds** a new message set: one fixed system message (the record holds placeholders, not real values; do not
     try to infer identities) and one user message with the fixed task instruction plus the rendered fields.
     Identifier values become typed placeholders (`[EMAIL_1]`, `[PHONE_1]`, `[SSN_1]`). No value→placeholder map
     survives the stage; no reverse mapping is stored;
   - re-runs the detector and the identifier patterns on the built payload; any remaining signal is refused
     (`redaction_incomplete`);
   - on success, sets the messages every attempt receives (primary and approved fallback, local or cloud) to the
     built set.

   The stage is reported as `skipped` (`redaction_not_required`) for every other request. A console preview is
   never authorization: execution always re-validates against the request's bound configuration version.

5. **Defence in depth (per attempt).** Before every provider attempt, the attempt gate refuses a cloud target for a
   request classified PII or higher unless the redact stage applied, and the model messages are exactly its built set
   (`sensitive_egress`). Removing the redact stage therefore fails closed instead of leaking.

6. **Web search.** Search requires a Public request (ADR-0015), so a sanitized PII request is refused at the search
   stage (`search_sensitive`). No query is built and no external call is made.

7. **Exact cache.** A sanitized request's key adds the redaction identity (`redact-v1`, policy id, types, tasks). It
   also hashes the **built** messages, never the original ones. Configuration version, application, team, profile,
   classification and candidates stay in the key.

8. **Response.** The response is returned as produced. Placeholders are never re-inserted.

9. **Evidence.** Audit v3 gains `Redaction` (additive, nullable): `{Transform: "redact-v1", PolicyId, Outcome:
   applied | refused, ReasonCode, Counts[{Type, Count}], OriginalClassification, TransformedClassification}`.
   - When a sanitized payload was built, the prompt fingerprint and the optional preview describe the **built**
     payload.
   - When the redact stage refused, no prompt fingerprint is recorded: a structured record's SHA-256 is guessable
     from a small value space.
   - For any request classified PII/PHI, and for any request on a dedicated sanitized workload that was not built,
     no preview and no fingerprint of the raw input is recorded. Pattern masking cannot remove unknown free-text
     identifiers, so nothing is masked; the raw input is simply not recorded.

   Refusals and cancellations are audited like any other request.

10. **Console preview.** `POST /admin/policies/simulate` returns, for a prompt that would be sanitized, the built
    messages, the counts, the would-be decision and any refusal reason (no provider call, no record, no spend). The
    Playground shows it before Run, lists the supported fields and tasks, and asks for confirmation before a sanitized
    payload is sent externally.

## Limits (stated wherever the feature is shown)
- This is a restricted structured-data demonstration, **not anonymisation and not guaranteed de-identification**.
  The non-identifier attributes, together with external knowledge, may still allow re-identification.
- Only the fields of `covenant.record.v1` are accepted. Free text, names, addresses and documents are never eligible
  for this route.
- PHI is never covered. Demo records use synthetic values only (e.g. SSN area `000`, `example.test` addresses,
  `555-01xx` numbers).
- Web search is not available for sanitized requests. On `/v1`, web search is not available at all (ADR-0015); the
  sanitized route works on `/v1` with the same envelope.

## Consequences
- (+) The demo shows refusal-by-default, then an explicit, versioned, evidenced alternative that never lowers the
  classification.
- (+) Raw values provably do not reach a provider adapter, web search, the cache key, the fingerprint or the evidence
  preview (tests).
- (+) Prose can never take this route, so undetected names or free-text identifiers cannot leak through it.
- (−) A new trace stage appears on every request (skipped when not required).
- (−) The schema is fixed in code. New fields or tasks need a code change and review, by design.

## Alternatives considered
- Regex replacement over arbitrary prose — rejected in review: names, addresses and free-text identifiers would pass
  undetected once the SSN no longer matches.
- Lower the classification to Public after redaction — rejected: it hides the original sensitivity and lets
  unrelated Public policies, search and caches apply.
- Global "redact all PII then route to cloud" switch — rejected: it would silently change every workload's routing.
- Reversible tokenisation with re-insertion into the response — rejected: it needs a stored mapping (a new sensitive
  store) and lets the response carry the original values.
- An external DLP or anonymisation service — out of scope for the appliance and for this demo; the seam
  (`IDataClassifier` / the redactor) stays swappable.
