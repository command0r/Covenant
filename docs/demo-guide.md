# Covenant demo guide: who asked, where it went, who pays, what was enforced

**Start here.** For the conference run, use the [7-minute demo](demo-7-minute.md); this guide is the longer reference. Concepts: [user guide](user-guide.md).
1. Open the Playground, choose **Run as** (the calling application) and a **Workload**. The line under it shows where that workload is approved to run (Local or Cloud, model, policy) before you send anything.
2. Type a prompt, or pick an **Example** and click **Use example**, then press **Run**. The trace shows each governance stage, and the result shows who pays and what was charged.
3. Every run, including refusals, appears under **Requests** with its evidence. **Audit & evidence → Check audit integrity** re-checks the hash chain.
4. On the demo instance: `demo-app` Public → Claude with a local fallback, and Internal → the local coder. `research-assistant` has local Qwen 27B for internal research and Claude + web search for public research. `budget-demo` (team demo-controls) shows a budget refusal.
5. Change routes, applications and workloads under **Policies**, and budgets under **Budgets**. Each change is one dialog and one new configuration version. State survives a normal launcher restart (`.covenant-local/demo-local-only`; see `local-demo-launcher.md`).

---

A 10-minute walkthrough for a live audience. Every step uses real providers. The console shows only recorded facts:
measured durations, observed health and settled charges. A step marked **configured** depends on your setup; a step
marked **tested** was exercised on the demo machine. See "What has been tested" at the end; don't present a configured
capability as tested behaviour.

## The idea in one line

**Application → workload → classification → policy → route → evidence and cost.**

| Concept | Meaning |
|---|---|
| Application | The calling system, e.g. a support portal. It belongs to a team, and that team and its budgets pay. |
| Workload profile | The declared kind of work, e.g. *Internal coding*. It carries a trusted baseline classification. |
| Detection | Can only **raise** the classification (an SSN pattern turns Public into PII). It never lowers it. |
| Policy | Exactly one policy matches (workload profile + classification). It names the model and any approved fallback. Covenant does not route on prompt keywords or guess model quality. |
| Evidence | Every request is recorded, including refused ones: application, team, workload, policy, configuration version, cache outcome and cost basis. |

## 0. Before the audience arrives (once, versioned, nothing replaced)

### Server (restart required; the console cannot do these)

- **Anthropic:** `Anthropic__ApiKey` in the server environment, plus `api.anthropic.com:443` on `Providers__ProbeAllowList`.
- **Tavily:** `Tavily__ApiKey`, plus `api.tavily.com:443` on `Providers__ProbeAllowList`.
- **Local models:** installed in Ollama, e.g. `qwen2.5-coder:7b` and `qwen3.8:27b-mlx`. `GET /v1/models` listing a model is availability, not health.

### Console (each step is one Preview impact → Save & apply, i.e. a new configuration version)

1. **Connections → Anthropic:**
   - Add or verify the connection, the exact model id, and its input/output prices from the provider's current price list. Covenant never assumes a price.
   - **Test** it (connections with an explicit endpoint), or run the Public writing example below.
2. **Connections → Ollama:** add the stronger research model next to the coder model, from the connection's model list. Existing models stay.
3. **Route each workload with "Route a workload here…"** on the model's row:
   - `qwen2.5-coder:7b` → **Internal coding**
   - `qwen3.8:27b-mlx` → **Internal research**
   - the Anthropic model → **Public writing** (cloud models serve Public workloads only)

   - the Anthropic model → **Public research** (with web search allowed on that profile), for cloud research. This replaces nothing: a separate **Internal research** profile stays on the local model.

   Each action only **adds**: the workload profile if missing and a policy scoped to that profile. It grants the workload **only** to the calling applications you tick (none by default). Existing policies are untouched. A workload that already has its own policy is refused — edit that policy instead (on v7, `public-research` was re-pointed to Claude this way).
4. **Settings → Web search:** enable it, enter the price per credit, then allow it on a Public profile (Policies → Applications & workload profiles).
5. **Optional:**
   - Settings → Operational → cache TTL > 0, for the cache example.
   - A tiny application-scoped budget on a separate demo application, for the budget example.
6. **Research output bound:** set the research application's max output tokens to at least 2048. The research model reasons before it answers; a low bound truncates it, and the result says *Truncated*.

**Playground**:
- **Run as** picks the calling application first. **Examples** then lists only what that application's granted workloads and configured routes support. Applying one never switches the application. "Needs setup" names exactly what is missing, with a link.
- **Workload**: two granted workloads show as two buttons (globe = Public with web search, pen = Public, shield = Internal, code = coding; arrow keys move between them). One shows as a label; three or more as a list.

Demo applications on the current demo instance and their examples (read each from Policies → Applications; versions change):

| Application (team) | Workloads → route | Examples listed |
|---|---|---|
| `engineering-assistant` (engineering) | Internal coding → local coder | Internal coding |
| `research-assistant` (research) | Internal research → local Qwen 27B; Public research + search → Claude | Internal research, Public research, sensitive refusal, cache |
| `communications-assistant` (communications) | Public writing → Claude, no search | Public writing, sensitive refusal, cache |
| `demo-app` (platform) | Public → Claude (local fallback); Internal → local coder | Public examples on Claude, internal coding, cache |
| `budget-demo` (demo-controls, $0.0001 cap) | Public writing → Claude | Budget refusal (others say its budget is exhausted) |

## 1. Internal coding on the local coder model

1. Use the **Internal coding on the local coder model** example and press **Preview route**. It shows the policy, `ollama/qwen2.5-coder:7b`, local, and no fallback. **Run.**
   - The example asks for code only and sets max output tokens to 2048 (for this example only; custom requests keep 1024). To compare, switch the same prompt to a Public workload: the line under the workload names the new destination, and if that application or model caps output lower it says "2048 requested → 1024 enforced". The old dry-run result is replaced by "Inputs changed — preview again".
2. Point at:
   - The trace (*Identify caller* assigns the application and team before anything else).
   - The route: local, configured $0, infrastructure excluded.
   - The evidence row: application, team, workload, policy, configuration version.

## 2. Stronger local research (a different caller and payer)

1. Use **Internal research on the stronger local model**. Its *Details* name who runs it. Read the owner from the card; don't assume it is the coding caller.
   - On the demo machine (configuration v5) coding runs as `engineering-assistant` (team engineering) and research as `research-assistant` (team research).
   - Different calling application, different team paying, different workload profile, so a different policy and model (`qwen3.8:27b-mlx`).
2. The provider stage shows the real measured time.
3. If the output bound is below what the example requests, the card says so before you run.

## 3. Public writing through Anthropic (real cloud call)

1. Use **Public writing through Anthropic**. The preview shows the Anthropic model.
2. **Run.** The charge is **reported usage × configured price** and is settled against the team's budgets.

## 4. Fresh facts with governed web search

1. Run as **research-assistant**, workload **Public research** (Claude + search). Use **Public research with web sources** (Required). The query targets one official page (`site:dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core`, ".NET 10 support end date") and asks for one sentence from the retrieved sources only. Check the answer against the cited page.
2. The trace shows **Web search** passed; then **Web sources** lists what was actually retrieved, separately from the model's `[n]` citations.
3. The search is charged as its own tool call.
4. Readiness is checked first: if the server cannot search, the composer names the one cause (key, egress, mode, disabled, price or profile) and links to the fix.
5. Say it plainly: the sources are **retrieved, not independently verified**. Covenant proves which pages it fetched and what it cost, not that the answer is correct — check the answer against the cited page.
6. **Weather:** type the city in **Search location** (or name it in the prompt). Without either, Run is blocked locally and the Host would refuse with *location required*. Nothing is sent. With web sources **Off** the field is greyed out and ignored.
   - The model is told the current UTC time, the place, and that the retrieval time is not the page's observation time.
   - Search snippets often lack current, dated readings. Then the honest answer says what is missing (e.g. "no dated current conditions for Toronto in the sources") rather than a number. Adding a date or `site:` to the prompt helps relevance but does not guarantee live readings.

## 5. One predictable refusal, and its resolution

1. Use **Sensitive data detected: served locally**, then **Sensitive data: explicit external attempt is blocked** (same prompt). The prompt holds the synthetic marker `000-12-3456`. It is explicitly invalid (area 000 is never assigned), but it matches the detector.
2. The example pins the Anthropic model explicitly.
3. **Run:** **Refused at Policy**. PII is never routed to a cloud destination, and nothing is sent.
4. Point at the evidence: the request is still attributed to its application, team and workload, with no charge.
5. **Resolution:** switch Model back to **Let policy choose** and run again. PII routes to the local model and is served.

## 6. Optional: cache and budget

- **Cache** (only when the TTL > 0): run the cache example twice. The second run is a **Cache hit** at $0, still governed and audited. With the TTL at 0 the card says the cache is off and makes no claim.
- **Budget:** the budget example runs as the low-budget demo application. It shows **Refused at Budget** before any spend, and the reservation is released. Other examples never pick an application whose budget is exhausted.

## Presenting the trace

- Live requests animate from real events only: a running stage pulses, and a finished one settles into its state.
- Stages that finished in under a millisecond show **<1 ms**, never a rounded 0.
- For a step-by-step explanation use **Replay trace** (Slow / Normal / Fast). It re-reveals the **recorded** events with their recorded durations and outcome. It never calls a model or changes a record, and a new run cancels it.
- In presentation mode, retrieved web sources are one collapsed line (count, not verified). Expand it for the links and the untrusted-data note; caller, destination, cost and evidence stay visible, and only the answer text scrolls.
- *Finalize cost* runs after the provider answered. A refused request shows **No usage** there and is still recorded with its owner.

## What has been tested vs configured

Historical checks below were executed on October 17, 2026 at localhost:5210, using configuration versions v4–v6. That temporary state was no longer present on November 1; do not expect those old records in the current instance. Current verification is documented in `verification-2026-11-01.md`. Rehearse again after changing models, policies or prices.

| Step | Status on the demo machine |
|---|---|
| Local coder route | Passed: engineering-assistant → Qwen coder 7B; team engineering recorded. |
| Stronger local research route | Passed: research-assistant → Qwen 27B, complete concurrency comparison with output cap 2048; team research recorded. |
| Anthropic Public writing | Passed on v5: Claude Sonnet 5.5, 39 input / 101 output tokens, $0.001088; team communications. |
| Tavily search | tested: 3 sources retrieved and charged ($0.008, team research) on v4; answer accuracy NOT validated (that run used an unbounded query) |
| Sensitive refusal | Passed: synthetic PII pinned to Claude refused at policy; no provider attempt, $0, owner retained. |
| Cache hit | Passed: first Claude call $0.000462; identical repeat $0 with no provider attempt. |
| Budget refusal | Passed on v6: isolated budget-demo application with $0.0001 cap refused before provider call, owner retained. |

Unit-test counts are not product readiness. Only the recorded runs above are.
