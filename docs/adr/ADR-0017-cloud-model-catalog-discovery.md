# ADR-0017 — Cloud model catalog discovery (admin-only, fixed endpoints)

**Status:** Accepted
**Date:** 2026-11-01
**Relates to:** ADR-0011 (versioned configuration, startup egress allow-list, secret references), ADR-0013 (permitted
models are configuration, not discovery).

## Context
Adding a cloud model means typing an exact provider model ID. Operators mistype IDs, or copy them from a vendor page
that does not match their account. Anthropic and OpenAI both expose a `GET /v1/models` list for the caller's
credential. Listing is a new outbound call made on behalf of an administrator, so it must not become an open proxy
(SSRF), a secret leak or a way to approve models implicitly.

## Decision
1. **Endpoint.** `POST /admin/connections/discover-models` with `{kind: "Anthropic" | "OpenAi", secret_ref}`
   (admin token required). Only these two kinds are supported. Ollama keeps using the existing probe.
2. **Fixed destinations.** The URL is a constant per kind: `https://api.anthropic.com/v1/models` and
   `https://api.openai.com/v1/models`. Nothing in the request can change the host, path or scheme. The authority
   must also be on the startup egress allow-list (`Providers:ProbeAllowList`), otherwise `egress_not_approved`.
3. **Credentials.** The secret is resolved server-side from `secret_ref`. The reference must name a credential the
   configuration may use (the same rule the connection editor uses); the value is never returned, logged or echoed in
   errors. A missing secret gives `secret_missing`.
4. **Bounded calls.** Redirects are disabled. Each page has a 10 s deadline and the whole discovery has a 20 s
   deadline. Each response is capped at 512 KiB. Anthropic pages are followed via `after_id` up to 5 pages of 100;
   OpenAI returns one list. At most 500 models are returned. There are no retries.
5. **Parsing.** Wire shapes are parsed with source-generated JSON in a provider-specific catalog component (the
   Host's provider layer, next to the SDK clients). Only `id`, `display_name` (Anthropic), `created`/`created_at`
   and `owned_by` (OpenAI) are kept. IDs longer than 200 characters or containing control characters are dropped.
6. **Safe errors.** Results carry a bounded code (`unauthorized`, `rate_limited`, `provider_error`, `timeout`,
   `invalid_response`, `egress_not_approved`, `secret_missing`, `unsupported_kind`) and a fixed human message, never
   provider free text.
7. **Discovery is not approval or pricing.** The console uses a discovered ID only to fill the model-ID field. The
   operator still adds the model to the connection, enters the price explicitly and saves a new configuration version.
   Prices and permissions are never derived from the list. Manual entry always remains available.

## Consequences
- (+) Fewer mistyped model IDs; the list reflects what the credential can actually see.
- (−) One more admin-triggered egress path. It is mitigated by fixed URLs, the allow-list, bounds and bounded error
  codes.

## Alternatives considered
- A configurable discovery URL — rejected (SSRF).
- Listing through the SDK clients — not chosen. The fixed HTTP call with a fake transport is simpler to bound and to
  test. The SDKs remain the inference path.
- Automatic adding of discovered models — rejected: permission and price are explicit operator decisions.
