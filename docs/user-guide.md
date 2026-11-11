# Covenant user guide

Covenant sits between your applications and the AI models they use. Every request says **who** sends it (an
application owned by a team), **what kind of work** it is (a workload such as coding or research) and **how sensitive**
the data is (Public, Internal or Sensitive). A versioned policy then decides **where** it may run: a local model inside
your perimeter, or an approved external model. Every request, including refused ones, is recorded with its owner, its
route, the reason and its cost. From **Overview**, click **Open Playground**, choose **Send a request as**, pick a
workload and one of the recommended examples, preview the route, and press Run.

---

## Concepts, one line each

| Term | Meaning |
|---|---|
| Application | An authenticated consuming system (portal, assistant, batch job). It belongs to a team, and that team pays. The Playground sends as an authorized test identity of it. |
| Workload | The declared kind of work (coding, research, writing). It carries a trusted baseline classification and selects the policy. |
| Classification | Public, Internal or Sensitive (PII/PHI). Detection can only raise it. Public *permits* external processing; it does not require it. |
| Destination | **Local** (teal, inside the perimeter) or **External** (purple, an approved provider). Red means sensitive data was detected or blocked. Every tag also has text and an icon. |
| Policy (route) | One match per workload + classification: an approved primary and optional fallback. No match means refused. |

## The sensitive-data comparison

For **Demo application**, apply **Internal workload: sensitive data served locally** and run. Internal is selected automatically. Switch to Public and run the unchanged prompt: this workload rejects detected PII/PHI before cache, search or models. Both outcomes are audited. Public is still a data label, not a promise to use a cloud model.

This behavior is a workload setting, not a special case for the example. Under **Policies → Workloads**, the Public workload must use **When sensitive data is detected → Reject the request**. Internal retains **Route by policy**. Existing saved setups keep their old behavior until this setting is saved; new demo seeds set it explicitly.

## What detection can and cannot see

Covenant inspects every message locally; no text leaves the perimeter for inspection (ADR-0018). It recognises:

- US SSNs, including spaced, dotted, full-width, zero-width-obfuscated, masked (`xxx-xx-1234`) and partial forms near
  "SSN", plus any standalone nine-digit number as a possible identifier;
- JSON-escaped and structured fields such as `us_ssn`, `date_of_birth` and `patient_id`;
- labelled identifier fields (SSN, e-mail, phone, date of birth, account and document numbers);
- Luhn-valid payment cards;
- medical record and patient identifiers;
- health information about an identifiable person, also when the name and the health detail are in different messages.

A generic health question is not escalated. It does **not** see free-text names or addresses, paraphrased facts or
most non-US identifiers. It is a routing safeguard, not complete DLP: no detection is not proof that text is safe to
send externally. If inspection cannot finish within its bounds, the request is refused and nothing is sent.

## Reading a result

Before Run, **Expected route** shows three boxes: **Sender → Covenant → Destination**. It comes only from the server's
current preview. Switching the workload keeps the prompt and previews again at once, so the scheme shows the new
workload's outcome for the same request. When you change other inputs or the configuration, it says *stale* instead of
showing an old answer. A
blocked preview says the destination would not be contacted. Web search adds a separate *external retrieval (Tavily)*
badge, even when inference is local.

After Run, **Actual route** uses the same three boxes, built only from that run's own audit record:

- **Local** (teal) or **External** (purple) for the model that served the answer;
- **Sanitized payload** (amber) when only a sanitized record went out;
- **Exact cache · no provider call** for a cache hit;
- **Blocked — model not contacted** (red) when refused. If a web search or a failed earlier attempt already happened,
  the scheme lists it; it never claims nothing external was contacted.

Below the scheme:

1. The outcome (served, refused, failed) and the cost split between inference and search.
2. The trace and replay of the stages.
3. **Details & evidence**, which holds the full record.

## Escalating a result

After a result, **Compare with <model>** re-sends the same request — the run's own prompt, caller and settings, not
whatever is in the composer now — to another target that the *same policy* already approves.
It is pinned to that target and recorded as a separate request. This is your choice. Covenant measures where a request
ran, how long it took and what it cost; it does not judge answer quality.

Internal content never becomes external because it is hard. To compare it externally you must explicitly resend it as
Public, under a Public workload the application is already granted. That requires a confirmation, and detection and
policy run again.

## Sanitized structured records (opt-in)

A dedicated workload with baseline PII can be given a route that sends an external model **only** a payload Covenant
builds from a `covenant.record.v1` JSON record:

- **Identifier fields** (`email`, `phone`, `us_ssn`) are replaced by placeholders.
- **Attribute fields** are fixed enumerations or bounded numbers:
  - `service`, `issue`, `channel` and `priority` (enumerations);
  - `days_open` (whole number 0–365);
  - `amount_usd` (0–1 000 000, at most 2 decimals).
- **The instruction** is Covenant's fixed text for the chosen task (`triage_case`, `reply_outline` or
  `compliance_checklist`).

Prose, unknown fields, PHI and anything still detected after the build are refused, and nothing leaves. The Playground
shows the exact payload and asks you to confirm before Run. The classification stays PII in the evidence. This is a
restricted demonstration, **not anonymisation**; the remaining attributes can still help identify someone. See ADR-0016.

## Usage

**Usage** groups recorded requests by team, application or workload over a time window. For each group it shows:

- requests and their outcomes;
- the estimated charge, split into inference and search;
- local and external calls, and web retrievals;
- cache hits;
- sensitive detections, kept separate from sensitive workload labels;
- sanitizations.

Unknown charges are counted separately, never as $0. Local calls are not free: infrastructure is excluded from the
charge. Team usage is an operational signal, not evidence about any individual.

## Models

**Models & connections → Add cloud provider → List models from …** shows the model IDs that the server's credential can
see. Picking one only fills the ID field; you still add the model, enter its price from the provider's price list and
save a new version. Manual entry always works.

## Deeper references

- [7-minute demo](demo-7-minute.md)
- [Demo guide](demo-guide.md)
- [Console quickstart](console-quickstart.md)
- [Web search setup](web-search.md)
- ADRs: [0016 sanitized egress](adr/ADR-0016-sanitized-egress-redaction-policy.md),
  [0017 model discovery](adr/ADR-0017-cloud-model-catalog-discovery.md)
