# Covenant in 7 minutes: three demonstrations

Synthetic prompts only. Each step names its setup; when the setup is missing, the console shows the example as **Needs
setup** with a link, never as runnable. Prices are configured by the operator from the provider's price list. Covenant
never assumes a price. Charges shown are estimates: reported usage × configured price.

## Before the audience (configured, versioned, once)

| For | Configure (Policies / Connections / Settings) |
|---|---|
| Demo 1 | Workload `public-local-research` (Public, web search allowed) → policy with a **local** model as primary; grant it to the research application. Tavily ready (Settings → Web search). |
| Demo 2 | Workload `public-coding` (Public) → route: primary the local coder, fallback the stronger external model (for example Anthropic `claude-opus-5-5`, price entered explicitly). Grant it to the coding application. |
| Demo 3 | Workload `support-records` (baseline **PII**) granted to one application → PII-only route for that workload to the external model with **Sanitized external route** on (types: email, phone, SSN; tasks: triage, reply outline). |
| Cache | Settings → Operational → cache TTL > 0 (for example 300 s). |

Use **Models & connections → List models from Anthropic** to copy the exact model ID. Discovery is not approval and not
pricing.

## 1. Public research: local inference + governed web search (≈2 min)

1. Playground → **Send a request as** the research application, workload *Public local research*.
2. Choose the example **Public research: local model + governed web search**, then press **Preview route**. It shows the
   local primary. Press **Run**. The prompt asks one targeted question of the official .NET support-policy page (".NET 10
   support end date"), answered in one sentence from the retrieved sources only. Check it against the cited page.
3. Point at the chips:
   - **Public (workload label)**;
   - **Local · <model>**;
   - **External retrieval · tavily**, the one external call;
   - the search charge, shown separately from inference.
4. Say it: retrieval and inference are separate decisions. Sources are retrieved, not verified. Search is granted per
   workload, not to every team.

## 2. Public coding: local first, explicit escalation (≈2 min)

1. Same Playground, the coding application, workload *Public coding*.
2. Recommended example **Compare local coding with <external model>** → **Run**. The steps shown before Run say local first,
   then an explicit, paid comparison. It is served on the local coder.
3. Press **Compare with <external model>** under the result. This is an operator choice on an approved target of the same
   policy, recorded as a second request.
4. Compare the two runs (destination, duration, charge). Say it: Covenant measures these. It does not decide which
   answer is better, and it has no automatic quality routing.
5. Optional: show that an **Internal** coding result offers external comparison only through an explicit "Compare
   externally as Public…" confirmation, and only when a Public workload is already granted.

## 3. Sensitive record: denied, then an explicit sanitized route (≈2.5 min)

1. **Local, then denied:** choose **Demo application**, then **Internal workload: sensitive data served locally**.
   Use example selects Internal. Run it, switch to Public, and run the identical prompt. The synthetic SSN raises
   classification to PII; the Public workload rejects it at Classify, before cache, search or any model call.
   The paired **Public workload: the same sensitive data is rejected** example is recommended too.
   Configure the Public workload to **Reject the request** on detection before the session.
2. **Explicit policy:** Policies → Routes → the `support-records` route. Show the **Sanitized external route** section:
   - only the dedicated PII workload uses it;
   - only the listed identifier fields and tasks are accepted;
   - prose and PHI are refused.
3. **Preview:** workload *Support records*, example **Sensitive record: sanitized external call** → **Preview route**.
   The payload shows `[EMAIL_1]`, `[PHONE_1]` and `[SSN_1]`, the fixed task instruction, and no original value. Tick
   **I reviewed this payload**, then press **Run**.
4. Point at the chips:
   - **Sensitive · PII (workload label)**, because the classification is never lowered;
   - **Sanitized payload · 3 identifiers replaced**;
   - **External · <model>**.

   Then open Details & evidence. It shows counts by type and the policy version, never the values.
5. Optional: add a field such as `"notes": "call Jane"` and preview again. It is refused as an unsupported field, and
   nothing leaves.

## 4. Cache (≈30 s)

Run **Repeat a request: exact cache hit** twice. The second run is a **Cache hit** at $0, with no provider call, still
governed and audited. Cached answers are scoped to the policy, version, application and workload. Web-search requests
never use the cache.

## Close

Open **Usage**. It groups requests by team, application or workload:

- requests and refusals;
- inference and search charges;
- local and external calls;
- cache hits;
- sensitive detections, separately from sensitive workload labels;
- sanitizations.

Then **Audit & evidence → Check audit integrity**. Integrity means the records were not altered; it is not a statement
about answer correctness or regulatory compliance.
