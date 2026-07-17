# Provenance

This file records the lineage of Covenant's code. **Patterns studied as influences are listed here; any actually-reused code would be listed with its source, license, and NOTICE attribution.** If reused code is not recorded here, it does not belong in the repo (see `docs/CLAUDE.md`).

## Status
No third-party source code has been copied into this repository. Everything below is an **influence** — a pattern understood and reimplemented in our own code — not copied code.

## Influences (patterns, not code)

| Pattern | Source of the idea | License of source | How we used it |
|---|---|---|---|
| Ordered request middleware with a `next` continuation | ASP.NET Core middleware; Bifrost pipeline | MIT / Apache-2.0 | Reimplemented as `IPipelineStage` + `InferencePipeline` in `Covenant.Core`. Our own code. |
| Canonical OpenAI-compatible request/response shape | LiteLLM, OpenRouter, Portkey (de-facto standard) | MIT / Apache-2.0 | Our own minimal canonical model in `Covenant.Core`; wire DTOs in `Covenant.Host`. |
| Provider abstraction (one interface, many providers) | Microsoft.Extensions.AI `IChatClient` | MIT (dependency, not copied) | Consumed as a NuGet dependency per ADR-0001; not vendored. |
| Hash-chained tamper-evident audit log | Common append-only / WORM ledger practice | — | Reimplemented in `Covenant.Host/FileAuditSink`. Our own code. |
| Two-layer cache, virtual keys, hierarchical budgets | LiteLLM, Bifrost, Portkey | MIT / Apache-2.0 | Not yet built; recorded now so the influence is acknowledged when they land. |

## Dependencies (consumed, not copied)
- `Microsoft.Extensions.AI` 10.8.0 / `Microsoft.Extensions.AI.OpenAI` 10.7.0 — MIT. Pinned and audited per root `CLAUDE.md`.
- `OpenAI` (.NET SDK) 2.12.0 — MIT.
- Test-only: `xunit` 2.9.3 (Apache-2.0), `xunit.runner.visualstudio` 3.1.5 (Apache-2.0), `Microsoft.NET.Test.Sdk` 18.7.0 (MIT). Never shipped in the appliance.
- `OpenTelemetry.Extensions.Hosting` / `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.17.0 — Apache-2.0. Host-only, activated only when `Otel:Endpoint` is configured (ADR-0003). Core instruments via BCL `ActivitySource` and takes no OTel dependency.

_Update this file in the same change as any code that introduces a new influence or dependency._
