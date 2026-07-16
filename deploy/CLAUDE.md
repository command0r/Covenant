# deploy — deployment & appliance

Scope: packaging and running Covenant in a regulated customer's environment.

## Shape
- **One artifact, one deployable:** a NativeAOT self-contained binary or a single container image. No sidecar gateway.
- Runs in-VPC / on-prem / air-gapped. Must run with **no outbound egress** except to the customer-approved model allow-list. No phone-home, no usage telemetry back to us — ever.

## Config & secrets
- Config via env vars / mounted files. Secrets via the customer's vault (e.g. HashiCorp Vault); never in the image, repo, or logs.
- Fail-closed on startup: missing or invalid policy / model allow-list → refuse to start, with a clear reason.

## Audit store (a deployment-grade requirement, not an afterthought)
- Append-only / WORM-capable, hash-chained for tamper-evidence. Retention configurable to the customer's mandate.
- The audit store's integrity is part of the security boundary.

## Supply chain (regulated buyers will ask)
- Reproducible builds. Ship an SBOM. Pin and audit every dependency (license + provenance).
- Sign artifacts; document the provenance of the build itself.
