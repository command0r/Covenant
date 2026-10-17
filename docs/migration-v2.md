# Migrating to configuration v2 (ADR-0011)

On first start of a v2 build with no configuration repository (`Config:Path`, default `covenant-config/`):

1. **`Config:Seed=demo-local-only` or `demo-hybrid`** (isolated demo profiles only) ⇒ the seed becomes v1.
2. **Otherwise the legacy startup keys are imported into v1** (`origin: legacy-import`) and reproduce today's
   effective routing exactly:
   - `Providers:Mode=local-only` ⇒ mode `LocalOnly`, one Ollama connection from `Local:Endpoint`/`Local:ModelId`,
     every classification → that model.
   - default (cloud) ⇒ mode `LegacyCloud`: an `openai` connection (secretRef `OpenAI:ApiKey`) with the public
     and strong models; `anthropic` if `Anthropic:ApiKey` is set; `local` if `Local:*` is set. Public/Internal →
     openai cheap model with the existing length heuristic to the strong model (threshold `Routing:ComplexityTokenThreshold`);
     PII/PHI → local (or refused when local is not configured). Nothing is silently re-routed.
   - `Budget:GlobalCapUsd`/`TeamCapsUsd` ⇒ budgets with period `None` (as before). `Pricing:*` ⇒ model prices.
   - Each `Auth:Keys` entry ⇒ an application (id = `Application` or `Principal`, team = `Team`, profile
     `legacy-internal`, console testing off).
3. The console's Policies page shows the imported effective rules and a "Move to Hybrid defaults" preview; nothing
   changes until an operator applies it.
4. Spend: if no ledger exists, the current audit segment's spend is imported once (`legacy-import`) into the current
   period. Audit history is untouched and keeps verifying.

After v1 exists the legacy routing keys are ignored and reported as such in Settings → Diagnostics.
