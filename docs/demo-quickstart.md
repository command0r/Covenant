# Covenant: quick demo instructions

Start Ollama, then run `scripts/run-local-demo.sh` from the Covenant repository. It opens the console on port 5210 and resumes your saved setup. Keep that terminal open; Ctrl+C stops it. Do not use `FRESH=1` for normal starts.

From **Overview**, click **Open Playground**. Choose **Send a request as → Example → Use example → Run**. Each application lists 2–3 **Recommended** examples; diagnostics (cache, refusal, cloud research) are under **More examples**. The application identifies the calling system and its paying team. The workload selects its permitted route. **Public allows external processing; it does not force it.** Read the Local/External destination tag to see where the request actually went.

## Seven-minute sequence

1. **Research assistant → Public research: local model + governed web search.** Show local inference, Tavily sources and the separate search charge.
2. **Engineering assistant → Compare local coding with <configured external model>** (for example Claude Opus 5.5; the title comes from the approved route). Before Run, the steps say: local first, then an explicit comparison. Run locally, then press **Compare with Claude Opus 5.5**. It is a separate paid request using the same original prompt and caller; which answer is better is your judgement.
3. **Communications assistant → More examples → Sensitive data detected: served locally**, then **Sensitive data: explicit external attempt is blocked**. Same synthetic prompt: with policy choosing it is *Served locally · sensitive data detected*; with the example's explicit external pin it is *Blocked · sensitive data cannot go external* (a governance block, not a provider outage). Switching workload resets the example's pin to “Let policy choose”.
4. **Support assistant → Sensitive record: sanitized external call (opt-in).** Use example, review the placeholder-only payload, tick the confirmation and Run. Only the supported structured record qualifies; arbitrary prose and PHI do not.
5. **Engineering assistant → More examples → Repeat a request: exact cache hit.** Run twice within five minutes. The second is audited but makes no provider call.
6. Open **Usage** to show who pays; **Requests** (25 per page, Previous/Next) → **View** explains an individual decision; **Budgets** shows Global plus the additional application ceiling over the same charges. **Audit & evidence → Check audit integrity** checks record consistency, not answer correctness.

Use **Presentation mode** for the stage, **Replay trace** to explain the steps, and **Esc** to return. For a quick budget refusal, use **Budget limit example**. For local-only deeper analysis, use **Research assistant → Internal research on the stronger local model**.

To test weather, choose **Demo application → Public**, write the question, set **Web sources → Required**, and enter **Toronto** in Search location. Check the returned source dates; web search is not a dedicated weather feed.
