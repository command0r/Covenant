# ADR-0018 — Local sensitive-data inspection: bounded, normalized, fail-closed

**Status:** Accepted
**Date:** 2026-11-02
**Relates to:** ADR-0012 (trusted baseline; detection may only escalate), ADR-0013 (PII/PHI never external),
ADR-0015 (web search only for Public), ADR-0016 (sanitized structured-record exception, PHI never external).

## Context
The first-slice classifier (`RegexDataClassifier`) matched two things:

- one hyphenated SSN shape;
- three PHI keywords. The keyword "diagnosis" alone escalated a request to PHI.

So it missed SSNs written with spaces or dots, with full-width or other Unicode digits, with zero-width characters
inside, or masked (`xxx-xx-3456`, "SSN ending 3456"). It also over-flagged generic health questions. Detection
decides where a request may run: a miss can send sensitive text to an external model (fail open), and a false
positive needlessly keeps public work local. Whatever replaces it must stay inside the perimeter, cost little on the
hot path, never echo the matched values and never be presented as comprehensive data-loss prevention.

## Decision
1. **One local inspector behind `IDataClassifier`.** `LocalSensitiveDataInspector` (Governance) replaces the toy
   rules; `RegexDataClassifier` remains as its compatibility name. Inspection runs in-process. Request text is never
   sent anywhere to be inspected, and no external provider is ever used for detection.

2. **Normalization before matching** (per message, every role: system, user, assistant, tool):
   - Unicode NFKC (full-width digits and letters);
   - any Unicode decimal digit mapped to ASCII;
   - zero-width characters and soft hyphens removed;
   - dash, dot and space variants mapped to `-`, `.` and space.

   Matching runs on a lower-case copy. The original text is unchanged; only the classification and a bounded signal
   label are kept.

3. **Bounds and failure: refused, never guessed.** Inspection is bounded per request:
   - at most 512 KiB of characters;
   - an aggregate 250 ms work budget, checked before every pattern (each pattern also has its own 200 ms timeout);
   - at most 512 matches per pattern per message, with nearby-context checks done by binary search (no nested scans);
   - at most 3 passes of escape decoding.

   Exceeding any bound, or a normalization or decoding failure, makes the result **Incomplete**. The classify stage
   then **refuses** the request (`inspection_incomplete`): the classification is reported as *not evaluated*,
   nothing is sent to any provider (local or external) or tool, and the evidence is recorded. The web-search query
   check, the ADR-0016 re-check of the built payload and the console preview all treat Incomplete as a refusal too.
   Unknown is never treated as "PHI, so it may go to the local model".

4. **What is detected.** Each message, of every role, is JSON-escape-decoded (bounded, including nested or
   double-encoded strings) and normalized. Structured keys are read as words (`us_ssn` reads as "us ssn",
   `date_of_birth` as "date of birth"). Each detector returns a fixed label and never the value.

   PII:
   - **US SSN, full:** three digits, two digits and four digits separated by hyphens, spaces or dots, also after
     normalization. Synthetic `000-…` values are flagged as well.
   - **US SSN, masked or partial:** `xxx-xx-1234` or `***-**-1234` on its own. "Ending 3456" or "last four 3456"
     counts with an SSN word nearby; "Last four of SSN: 3456" names the SSN itself.
   - **A standalone nine-digit number** is a *possible identifier*. It is not asserted to be a valid SSN, and it is
     labelled as an SSN only with nearby context. Digits inside longer numbers, decimals, grouped figures, hex ids or
     words are not counted. Ordinary short quantities (four digits and so on) stay benign.
   - **Labelled identifier fields**, written as `name: value` or as a structured key:
     - SSN or taxpayer id;
     - e-mail;
     - phone;
     - date of birth;
     - passport, licence, bank account, IBAN or card number.

     Placeholder values such as `[EMAIL_1]` never count, so the ADR-0016 built payload still passes.
   - **Payment card numbers:** Luhn-valid. Ungrouped numbers need a card word nearby.

   PHI (always the stricter class):
   - **Medical record or patient identifier** with a value (MRN, medical record number, patient id, health plan or
     member id).
   - **Health information about an identifiable person.**
     - *Identifiable* means any PII detection above, or any of:
       - a titled or "Patient" name;
       - a lower-case patient narrative ("patient jane roe has …");
       - a self-identification ("My name is Jane Roe");
       - a labelled name field (`name: Jane Roe`, `patient_name`);
       - a date of birth.
     - *Health detail* means any of:
       - a medical term;
       - a labelled medical field (`diagnosis:`, `medication:`);
       - a self-disclosure ("I was diagnosed with …", "my medication …");
       - a dose;
       - an ICD-10-shaped code.
     - Identity and health detail are combined **across the messages of one request**, so splitting them into separate
       turns does not bypass the rule. Each message is still matched on its own, and no text is joined across a
       message boundary. A generic health question or quantity with no identifiable person is **not** PHI.

5. **No match is not proof of safety.** No match means no escalation. The workload's trusted baseline still decides,
   and a Public baseline permits external processing; it does not require it. A Public request with no match can
   still contain sensitive facts this inspector cannot see.

6. **Every egress honours the classification before sending.** The rules below are unchanged and re-verified by
   tests against the new inspector:
   - PII/PHI policies never make external targets eligible;
   - the per-attempt egress guard re-checks every primary and fallback attempt;
   - web search requires a Public request, and its query is inspected separately;
   - the exact-cache key includes the final classification.

   ADR-0016's structured-record exception is unchanged, and PHI is never external.

7. **Evidence.** The audit `Signal` is one of the fixed labels (the strongest one when several match). Matched
   values never enter logs, errors, signals or traces.

8. **Optional local entity service (proposed, not implemented).** A later `IEntityInspector` seam may call a
   locally hosted named-entity service (for example a Presidio-style analyzer in the same perimeter, on a startup
   allow-listed local endpoint). Three requirements for it:
   - it adds to the deterministic layer and never replaces it;
   - an unreachable service, a timeout or a malformed answer fails closed as in §3;
   - there is no cloud fallback.

   It needs its own ADR, covering deployment, latency budget and evidence, before any code.

## Limits (stated in the docs and the console)
Pattern inspection cannot see the following:

- unlabelled free-text names (other than the listed name contexts) and addresses;
- non-US national identifiers other than the listed labelled fields;
- images and attachments (these are refused at the shape stage);
- paraphrased or encoded sensitive facts;
- health information about a person who is not identifiable by these rules.

It is a **routing safeguard, not comprehensive DLP**. Operators keep sensitive work on workloads with an Internal or
sensitive baseline.

## Consequences
- (+) Common real-world SSN formats, masked forms and Unicode tricks no longer slip through. Generic health questions
  stay Public.
- (+) Failure to inspect cannot fail open: it is a refusal, and nothing is sent anywhere.
- (−) More false positives in some cases (for example a labelled phone field in public marketing text). The cost is
  a local route, never a leak.
- (−) The rules are heuristics that need maintenance; the test corpus is the contract.

## Alternatives considered
- Keep the toy rules: rejected (fail-open misses).
- Send text to a cloud DLP or LLM classifier: rejected (it is egress of the very data being protected).
- A large in-process ML or NER dependency now: deferred to the §8 seam. Dependency, provenance and AOT costs need
  their own decision.
