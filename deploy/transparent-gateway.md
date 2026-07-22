# Transparent egress gateway deployment (ADR-0005)

Goal: AI traffic inside the perimeter is governed by Covenant regardless of client configuration.
Nothing here changes the binary — this is network + certificate + firewall configuration.

## The three mechanisms (all three, or it leaks)

### 1. DNS override — traffic finds Covenant
On the internal resolver, point provider hostnames at the Covenant appliance:

```
api.openai.com        A   <covenant-ip>
api.anthropic.com     A   <covenant-ip>     # when the anthropic adapter lands
```

Every SDK, script, LangChain app, and desktop tool using the provider API now reaches Covenant
without any client change. Split-horizon DNS: only the internal zone is overridden.

### 2. TLS termination — Covenant answers as the provider
Clients connect with TLS to what they believe is `api.openai.com`. Covenant must present a
certificate for that name, issued by the **customer's private CA** — the CA already trusted on
managed endpoints (standard BFSI posture). Issue one cert per intercepted hostname (or a SAN cert)
and configure Kestrel with per-SNI certificates:

```json
// appsettings.Production.json (certificates come from the customer's PKI, keys from the vault)
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:443",
        "Sni": {
          "api.openai.com":    { "Certificate": { "Path": "/certs/api.openai.com.pfx",    "Password": "…" } },
          "covenant.internal": { "Certificate": { "Path": "/certs/covenant.internal.pfx", "Password": "…" } }
        }
      }
    }
  }
}
```

Covenant's own upstream connection uses the system trust store and the *real* provider endpoint
(`OpenAI:Endpoint` unset or explicit) — the DNS override must not apply to the appliance host, or
give the appliance a direct resolver entry for the true IPs.

### 3. Egress firewall — the bypass is closed
Only the Covenant appliance may open connections to AI provider ranges; all other hosts are denied.
Someone hardcoding an IP or using DNS-over-HTTPS hits the firewall, not the provider. This is the
same egress discipline deploy/CLAUDE.md already mandates for the appliance itself.

## Credentials in transparent mode
Intercepted clients send whatever key they were configured with. Covenant looks it up in the
virtual-key registry like any other credential:

- **Registered corporate keys** → resolved to principal + team; governed and attributed normally.
  Register each sanctioned key: `Auth:Keys:N:Key/Principal/Team`.
- **Unregistered keys** (personal/shadow) → 401, audited. Day-one output of this deployment is a
  list of every ungoverned AI credential in the building, with principals attached as they
  self-identify to claim access.

## What is NOT intercepted — say it plainly to the customer
- **Consumer web apps (chatgpt.com, claude.ai, gemini.google.com):** not the public API; content
  interception requires endpoint-agent TLS MITM (CASB/SWG territory) and breaks on pinning.
  Posture: **block at egress**, offer the sanctioned in-perimeter chat UI backed by Covenant
  (README-SCAFFOLD §5). Blocked traffic is visible in firewall logs, not in Covenant's audit chain.
- **Certificate-pinned native clients:** cannot be TLS-terminated; they fail closed at the firewall.
- **Anything not on the DNS override list:** new provider hostnames must be added deliberately —
  which is the allow-list working, not a gap.

## Verification checklist
1. From a managed endpoint: `curl https://api.openai.com/v1/models -H "Authorization: Bearer <registered-key>"`
   → returns only policy-permitted models, TLS chain roots in the corporate CA.
2. Same call with an unregistered key → 401, and the denial appears in Covenant's request history.
3. From a non-appliance host, connect directly to the provider's real IP:443 → blocked by firewall.
4. `chatgpt.com` from any endpoint → blocked at egress (firewall log entry, by design).
