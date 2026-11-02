# ADR-0019 — Per-workload action on detected sensitive data: route or reject

**Status:** Accepted
**Date:** 2026-11-02
**Relates to:** ADR-0012 (trusted baseline; detection may only escalate), ADR-0013 (PII/PHI never external; no match ⇒
refuse), ADR-0016 (sanitized structured-record exception), ADR-0018 (local inspection; incomplete ⇒ refuse).

## Context
Detection escalates a request's classification (ADR-0012): final = max(workload baseline, detected). The escalated
request is then routed by whatever policy covers PII/PHI. In the demo seed that is `sensitive` (PII & PHI → local
model), a policy with no profile scope. So a request sent under the **Public** workload that contains an SSN is
served by the local model.

That is safe: nothing leaves the perimeter. The evidence also records it: the audit record and the console show the
escalated classification and the detector signal. But the request still succeeds on a destination the caller did not
declare for: they said "this is public content", the content contradicts that, and the gateway serves it elsewhere
instead of refusing. Two things follow:

- a workload cannot say "never accept sensitive data" — the most it gets is an after-the-fact evidence entry;
- the demo cannot show the difference between "an Internal workload that is approved to process sensitive data
  locally" and "a Public workload that must never receive it". Both end up on the same local model.

A UI-side fix (pinning a model, special-casing an example name) would not change what the gateway does for a raw API
caller, so it would be theatre. The decision has to be server-side, per workload, versioned and audited.

## Decision
1. **A workload profile states what happens when detection finds PII or PHI.** New field
   `WorkloadProfileConfig.SensitiveDataAction`, wire name `sensitive_data_action`:
   - `route` (default): today's behaviour. The classification is escalated and the policy for PII/PHI decides.
     ADR-0013 still applies, so that policy can only use local targets — except the ADR-0016 sanitized policy.
   - `reject`: when detection raises the classification to PII or PHI, the request is **refused at the classify
     stage**, before policy, sanitization, cache, rate limit, budget, web search and provider. Nothing is sent
     anywhere and nothing is reserved.

   The name is `route`, not `route_local`: the profile does not choose the destination; the versioned policy does,
   and the existing invariants keep PII/PHI local.

2. **Default `route`; existing configurations are unchanged.** A version without the field reads as `route`. No
   stored version is rewritten. The dedicated sanitized-record workload (baseline PII, ADR-0016) keeps working.

3. **Validation.** `reject` is valid only on a Public- or Internal-baseline profile (code
   `sensitive_action_requires_non_sensitive_baseline`). On a PII/PHI baseline every request is sensitive by
   declaration, so rejecting "detected" sensitive data would reject everything. An undefined value is `invalid_enum`.

4. **One rule, three callers.** `SensitiveDataRules.Rejects(cfg, profileId, finalClassification)` decides;
   `SensitiveDataRules.Explain(...)` builds the reason and `SensitiveDataRules.Suggestions(...)` the suggested
   workloads. The rule is called by:
   - the classify stage (runtime);
   - `POST /admin/policies/simulate` (the preview shows the same refusal and reason);
   - `PolicyEngineV2.Evaluate` (defence in depth). This also means the impact preview and route summaries show
     "refused" for PII/PHI under a `reject` workload.

   Incomplete inspection (ADR-0018) is still checked first and still refuses with `inspection_incomplete`.

5. **Evidence and feedback.** The refusal is a governance denial at stage `classify` with reason code
   `sensitive_data_rejected`. The audit record keeps:
   - the escalated classification (PII/PHI);
   - the detector signal label (never the matched value);
   - the application, team and workload.

   The reason names the workload and the detected kind. When the same application is granted another workload under
   which the engine would route that classification (action `route`, an eligible route), the reason also suggests it
   by id. The simulate response returns the same suggestion as `suggested_profiles`. The suggestion is computed by
   the policy engine, never hard-coded.

6. **Demo seed.** `public-demo` is seeded with `reject`; `internal-default` keeps `route`. Seeds only create a first
   version; an existing runtime is not changed (see Migration).

## Migration (existing saved configurations)
Nothing changes until an operator saves a new version. To get the new demo behaviour on a runtime created before
this ADR, set `sensitive_data_action` to `reject` on the Public workload. This creates version N+1, audited as
`config.activated`. Either:

- **Console:** Policies → Workloads → *Public demo* → Edit → "When sensitive data is detected" → **Reject the
  request** → Save.
- **API:**
  1. `GET /admin/config`; note `etag` (`"vN"`).
  2. In `config.profiles`, on the entry with `"id": "public-demo"`, add `"sensitive_data_action": "reject"`.
  3. `POST /admin/config/validate` with `{"base_version": N, "config": …}`. Expect `valid: true`. The impact lists
     PII and PHI under `public-demo` changing to `refused: …`.
  4. `PUT /admin/config` with `If-Match: "vN"` and `{"config": …, "change_summary": "public-demo rejects detected
     sensitive data (ADR-0019)"}`.

Rollback is the normal configuration rollback to vN.

## Consequences
- A Public workload can now say "never accept sensitive data". The caller gets an explicit refusal with the reason
  and a suggested workload, instead of a local answer to a request it declared public.
- The console offers the Internal-local / Public-rejected example pair only to an application whose workloads show both
  halves, so applications without that set-up are not shown sensitive-data examples.
- The audit still shows what was detected, so Usage still counts "sensitive detected".
- One more profile field in the configuration contract, the UI DTO and the workload editor.
- Detection quality limits still apply (ADR-0018): `reject` acts on what the local inspector detects. It does not
  guarantee that a Public request is free of sensitive data.

## Alternatives considered
- **Policy-level flag** ("this PII policy refuses for Public"). Rejected: the policy is chosen by the escalated
  classification, so it no longer knows which workload the caller declared, unless every sensitive policy is
  profile-scoped. It is also the wrong owner: the declaration belongs to the workload.
- **Always reject sensitive data under Public.** Rejected: a breaking change for existing deployments, and some
  operators want "serve locally" as the safe default.
- **UI pin / example special-casing.** Rejected: it does not govern raw API callers.
- **`route_local` as a second action.** Rejected: the profile would then pick a destination, bypassing the
  versioned policy that owns routing.
