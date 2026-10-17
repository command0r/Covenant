# ADR-0012 — Console inference as an authorized test application; trusted workload profiles

**Status:** Accepted
**Date:** 2026-10-16
**Spec:** `docs/CONSOLE-V2-IMPLEMENTATION-SPEC.md` §3, §4.2.

## Context
The console asked operators to paste an application key, conflating roles, and `RegexDataClassifier`
returned Internal on no match, so Public was unreachable. A client cannot be allowed to declare its own data
public.

## Decision
**Applications** are configuration entities: `id`, `team`, `enabled`, `consoleTesting`, `grantedProfiles`,
`defaultProfile`. `/v1` keys (`Auth:Keys:N:Key`, startup secrets) bind to an application via
`Auth:Keys:N:Application` (default: the key's `Principal`, preserving existing keys). Legacy import creates
one application per existing key **once**. After that, a key whose application is missing from the active
configuration (removed, renamed, never added) is **refused** (403, stopped at `auth`, "application not
configured") — fail-closed; there is no implicit legacy fallback.

**Workload profiles** carry a **trusted baseline classification** and are granted to applications. A
request selects a profile (`X-Covenant-Profile` on `/v1`, `profileId` on the console endpoint); an ungranted
profile is a governed refusal (403, stopped at `auth`). No profile ⇒ the application's `defaultProfile`;
none ⇒ `legacy-internal` (baseline Internal). **Final classification = max(baseline, detected)**: the
detector can only escalate. `RegexDataClassifier` keeps its interface and its narrow coverage (US SSN
pattern; MRN/diagnosis/patient-id keywords) — documented as a demonstration detector, not DLP. Its
no-match result means "no escalation", never "public". No header, prompt or UI control can lower the
baseline. Evidence records `ProfileId`, `BaselineClassification`, detected escalation and final class.

**Console inference.** `POST /admin/playground/requests` (admin token) with `applicationId`, `profileId`,
canonical input, optional model override / max tokens / stream / workflow / use case / `clientRequestId`.
The Host resolves the application server-side and runs **the same `InferencePipeline`** with a
pre-resolved identity (a server-side field the `/v1` ingress can never set); the auth stage enforces `enabled`, `consoleTesting` and the profile grant (governed
refusals, audited). Body validation failures are 400 **before** the pipeline (no audit record; the response
says so). Evidence records `EntryPoint = console`, `OperatorRef = shared-admin-token` (honest: one shared
admin credential cannot attribute an individual), `ApplicationId`, `Team`. Application credentials are never
sent to or returned from the browser. `clientRequestId` makes a resubmission within 10 minutes return 409
with the original request id — a reload never replays inference. `/v1` authentication is unchanged.

## Consequences
- (+) One token for the operator; applications stay the unit of identity, team and budget.
- (+) Public becomes reachable only through a trusted, granted profile.
- (−) Console actions are attributable to "the shared admin credential", not a person, until real IAM.

## Alternatives considered
- Console holds application keys — rejected (credential sprawl into the browser).
- Treat "no regex match" as Public — rejected (absence of detection is not evidence of public data).
