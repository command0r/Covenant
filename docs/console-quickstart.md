# Covenant: everyday use

## Start and connect

With Ollama running and your Qwen model installed, run from the repository:

```sh
scripts/run-local-demo.sh
```

This builds the current code, starts Covenant on port 5210 and opens a connected console. Keep that terminal open. Do not use `SKIP_PUBLISH=1` after changing code: it reuses the old executable. If the port is occupied, stop your previous launcher with Ctrl+C or choose another port with `PORT=5212`.

The console needs only the **admin token**. The separate **application key** is for API clients such as Open WebUI; you do not enter it to run requests in the console. The launcher connects the console for you and keeps the application key in an owner-only file.

## Run a request

1. Open **Overview** or **Playground**.
2. Select the application under **Run as** and its workload profile.
3. Write a prompt and click **Run**. The answer appears on the same page.

Use an **Internal** profile for internal work. Use a **Public** profile for genuinely public content. Profiles establish the starting classification; detection can raise it. The current detector recognises specific patterns, not every possible secret or sensitive fact.

**Demo presets** fills the form; it does not run it. **Advanced** contains model overrides and streaming. **Presentation mode** provides the larger conference view. Use Escape to leave it.

## Get current information with Tavily

Open WebUI's Tavily configuration is separate. Covenant needs its own server configuration; it does not import Open WebUI's key.

One-time setup:

1. Supply `Tavily__ApiKey` to the Covenant server environment through your normal secret-management method. Do not put it in source code or the console.
2. Add `api.tavily.com:443` to `Providers__ProbeAllowList`, preserving existing approved endpoints, and restart Covenant.
3. Under **Policies**, change the mode to **Hybrid** and apply. Your model can still be local; Hybrid permits approved external calls, including search.
4. Under **Settings → Web search (Tavily)**, click **Edit**, configure web search, enter your plan's price per credit and enable it. Preview and apply.
5. Under **Policies → Applications & workload profiles**, allow web search on the Public profile you intend to use. Preview and apply.
6. Check **Settings → Web search**. Its readiness list tells you what is still missing.

To try it: choose that Public profile, select **Web sources → Auto**, enter a city under **Location**, and ask “What's the weather today?”

- **Off:** model knowledge only.
- **Auto:** searches for recognised current-information cues, including weather and news.
- **Required:** always searches before answering.

The answer includes a separate list of retrieved sources and their retrieval time. A failed or empty search produces a clear failure rather than asking the model to invent a current answer. Search is not a dedicated weather feed, so check the sources and their dates.

Local-only mode never searches externally. Internal, PII and PHI requests cannot use Tavily. Search and model inference have separate charges in the request evidence, charged to the same budget.

## Use OpenAI or Anthropic for Public requests

1. Supply `OpenAI__ApiKey` or `Anthropic__ApiKey` in the server environment and restart. These are provider credentials, separate from the console admin token.
2. Ensure the provider endpoint is approved at startup: `api.openai.com:443` or `api.anthropic.com:443` in `Providers__ProbeAllowList`. Preserve the local and search endpoints you still need.
3. Open **Models & connections → Add cloud provider**. Choose the provider, enter the exact model ID and its current input/output prices. The secret reference is selected automatically; the key itself never enters this form.
4. Preview and apply the connection.
5. Under **Policies**, use **Hybrid** mode and edit the **Public** route to select that connection and model. Add an approved local fallback if wanted. Keep sensitive routes local. Preview and apply.
6. Run a Public request in Playground. The result shows the actual destination and charge.

Adding a connection alone does not change routing. **Local-only** blocks cloud calls even if a cloud connection exists. Prices come from your provider plan; Covenant does not download invoices or automatically update prices.

## Know what happened

- **Playground trace:** the steps that actually ran, including search, refusal and provider attempts.
- **Requests → View:** one request's classification, route, attempts, cost and audit record.
- **Budgets → Edit:** change caps and enforcement, preview, then apply. Local inference has no per-call provider charge; hardware and electricity are not included.
- **Audit & evidence:** verify the audit chain and export evidence. “Evidence recorded” means the record exists; chain verification checks its integrity.
- **Pause new provider calls:** blocks new paid/external work through the pipeline. It does not erase history or reset budgets.

For exact search limits and troubleshooting, see [Web search](web-search.md). For the conference sequence, see [Demo runbook](demo-runbook.md).
