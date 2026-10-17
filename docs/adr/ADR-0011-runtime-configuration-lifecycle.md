# ADR-0011 — Runtime configuration: versioned snapshots, validated apply, rollback

**Status:** Accepted
**Date:** 2026-10-16
**Supersedes in part:** ADR-0010 ("runtime apply is deferred"; probe allow-list source), ADR-0004 trigger
("a policy or budget editor") is now fired. Spec: `docs/CONSOLE-V2-IMPLEMENTATION-SPEC.md` §4.1.

## Context
Routing, connections, budgets and operational settings were fixed at startup from configuration keys and
changed only by editing secrets and restarting. The console must edit them safely: validated, versioned,
auditable, reversible, never half-applied, and without letting a request see a mixture of old and new rules.

## Decision
**Repository.** One directory, `Config:Path` (default `covenant-config/` beside the audit log), holds
immutable snapshots `versions/v000001.json …` plus `active.json` = `{ "version": N, "sha256": "<hash of vN file>" }`.
Only non-secret configuration and **secret references** are persisted. No database.

**Write ordering (apply/rollback):** take the single writer lock → validate the candidate against the
current active version (`If-Match: "vN"` must equal active; else 412) → write `versions/vM.json.tmp`,
flush to disk, rename to `versions/vM.json` (create-new; an existing file is never overwritten) → write
`active.json.tmp`, flush, rename over `active.json` → swap the in-memory snapshot reference → append a
`config.activated` audit event (v3 `EventType`) carrying version, base version, operator reference and a
change summary → release the lock. Rollback validates the historical snapshot as a NEW candidate and
activates it as version M+1 (`RolledBackFrom = K`); history is never rewritten.

**Crash recovery at boot:** read `active.json`; verify the referenced file's SHA-256. Missing/corrupt
pointer or hash mismatch ⇒ **refuse to start** with a diagnostic (fail-closed; the operator restores the
pointer, never a silent "latest"). Version files newer than the active one are reported as "written, never
activated". If the active version has no `config.activated` audit event (crash between pointer rename and
audit append), boot appends one marked `recovered: true`.

**Bootstrap precedence:** (1) existing repository wins; (2) empty repository + `Config:Seed` ∈
{`demo-local-only`, `demo-hybrid`} ⇒ that seed becomes v1; (3) otherwise the legacy startup keys
(`Providers:Mode`, `OpenAI:*`, `Anthropic:*`, `Local:*`, `Budget:*`, `Pricing:*`, `Cache:*`,
`Routing:*`, `Auth:Keys` identities) are **imported** into v1 with `origin = legacy-import`, reproducing the
existing effective routing exactly (legacy cloud mode stays legacy cloud mode; the migration preview shows
its effective rules). After v1 exists, those legacy keys are ignored for routing and reported as such.
Startup-only (never console-editable): `Admin:Token`, all secret values, `Auth:Keys` key values,
`Audit:*`, `Config:Path`, and **`Providers:ProbeAllowList` — the egress allow-list**: a configuration edit
can never widen where Covenant may connect. Connection endpoints must be on the allow-list (or be the
legacy-imported endpoints, which are added to it at import) at validate, apply and execution time.

**Secrets.** A connection names a `secretRef`: an `IConfiguration` key, restricted to `Secrets:<name>` and
the legacy keys `OpenAI:ApiKey`, `Anthropic:ApiKey`, `Local:ApiKey`. The API reports `available: true|false`,
never the value. Saving a cloud connection whose secret is unavailable is a validation **error** (an
unusable save is not shown as success).

**Snapshots and isolation.** A validated version compiles into an immutable `RuntimeSnapshot` (policies,
provider clients, price book, budgets, cache settings, profiles, applications). Each request acquires the
current snapshot once at entry (ref-counted) and uses only it; the snapshot id is the request's
`ConfigVersion` in trace and audit. A retired snapshot disposes its provider clients when its last request
releases it. Spend, reservations and audit history are never reset by apply/rollback.

**Concurrency:** one writer (lock); readers lock-free via a volatile reference. Version numbers are
monotonic; ETag = `"v<N>"`.

## Consequences
- (+) Edits are atomic, conflict-safe (412 on stale `If-Match`), reversible, and evidenced in the chain.
- (+) A request can never mix rules from two versions.
- (−) Boot fails if the active pointer is damaged — deliberate; recovery is a manual pointer fix.
- (−) Secret values still come only from startup configuration/vault; the console references them.

## Alternatives considered
- SQLite/Postgres — rejected: new dependency/service for a single-appliance, low-write workload.
- Mutable single file — rejected: no history, torn writes, no rollback.
- Auto-select the latest version on a corrupt pointer — rejected: could activate an unvalidated write.
