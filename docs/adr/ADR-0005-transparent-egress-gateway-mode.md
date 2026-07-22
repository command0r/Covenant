# ADR-0005 — Transparent egress gateway mode: interception by network, not by client config

**Status:** Accepted (2026-07-18)
**Date:** 2026-07-18

## Context
Explicit-proxy mode (clients configured with Covenant's base URL) governs only cooperating clients.
Regulated buyers need the Kong-style posture: AI traffic is governed no matter what the client is
configured to do. No gateway product achieves this "by magic" — traffic is *made* to reach the
gateway by the network: DNS override of provider hostnames, TLS termination with certificates issued
by the customer's private CA (which managed endpoints already trust), and an egress firewall that
permits only the gateway to reach AI providers. Covenant's wire surface is already OpenAI-compatible,
so a client that believes it is talking to api.openai.com and lands on Covenant functions normally.

A hard boundary applies to every product in this category: consumer AI web apps (chatgpt.com) are
not the public API, and full content interception of them is endpoint-agent/SWG territory with
certificate pinning working against it. The enterprise-realistic posture is: block consumer apps at
egress; provide a sanctioned in-perimeter chat UI backed by the gateway.

## Decision
Covenant supports two deployment modes, differing only in configuration — one binary, one pipeline:

1. **Explicit endpoint** (default): clients are configured with Covenant's URL and a virtual key.
2. **Transparent egress gateway**: the customer's network makes provider hostnames resolve to
   Covenant; Kestrel serves TLS with per-SNI certificates for those hostnames issued by the
   customer's CA; the egress firewall allows only Covenant outbound to the provider allow-list.

In both modes the pipeline is identical and fail-closed. In transparent mode, credentials presented
by intercepted clients are looked up in the virtual-key registry exactly like explicit-mode keys:
an unregistered key (someone's personal OpenAI key) is denied 401 and **audited** — shadow AI usage
becomes evidence on day one, which is a feature of the posture, not a side effect.

Consumer web apps are blocked at egress, not intercepted. The sanctioned alternative (an
OpenAI-compatible chat UI pointed at Covenant) is deployment guidance, not product code.

## Consequences
- (+) No new pipeline code: interception is DNS + certificates + firewall, documented in `deploy/`.
- (+) Shadow-key discovery: every ungoverned credential in the building shows up as an audited 401.
- (+) The egress-firewall requirement is the same one deploy/CLAUDE.md already mandates.
- (−) Requires the customer to operate a private CA and managed trust stores — table stakes in BFSI,
  a real barrier elsewhere. Explicit mode remains for everyone else.
- (−) Certificate-pinned native clients cannot be intercepted; they fail closed at the firewall
  instead. This is stated honestly rather than promised away.
- (−) Covenant must keep its wire surface faithful to the providers it impersonates; drift breaks
  intercepted clients. Bounded by the small surface we already own (ADR-0001).

## Alternatives considered
- **Endpoint agents / TLS-MITM of consumer web apps (CASB/SWG).** Rejected: different product
  category, massive endpoint footprint, breaks the single-appliance posture.
- **Explicit mode only.** Rejected: leaves ungoverned traffic invisible; the research question (RQ1
  enforceability) demands the perimeter answer.
- **In-line L4 passthrough without TLS termination.** Rejected: can see destinations (SNI) but not
  content — cannot classify, attribute, or audit; that is monitoring, not governance.
