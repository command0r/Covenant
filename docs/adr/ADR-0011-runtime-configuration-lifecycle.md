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
**Repository.** One directory, `Config:Path` (default `<Audit:Path>.config/`, so every audit log — and every
isolated test or demo — gets its own repository), holds immutable snapshots `versions/v000001.json …`,
`active.json` = `{ "version": N, "sha256": "<hash of vN file>" }`, and `activations.jsonl`, an append-only
**activation journal** (one line per activation: version, base, rolled-back-from, utc, summary). The journal,
not the presence of a version file, defines history: version files that were never activated (orphans) are
excluded from history, `GET /admin/config/versions/{n}` and rollback, and their numbers are never reused.
Only non-secret configuration and **secret references** are persisted. No database.

**Write ordering (apply/rollback):** take the single writer lock → validate the candidate against the
current active version (`If-Match: "vN"` must equal active; else 412) → **compile the runtime snapshot
(provider clients included); any compile failure aborts here with nothing durable written** → choose
M = max(existing version file numbers, journal max) + 1 → write `versions/vM.json.tmp`, flush, rename to
`versions/vM.json` (create-new) → write `active.json.tmp`, flush, rename over `active.json` → append the
journal line (flush) → swap the in-memory snapshot reference → enqueue the `config.activated` audit event
(v3 `EventType`: version, base, operator reference, bounded redacted change summary — never secret values)
→ release the lock. Rollback validates the historical snapshot as a NEW candidate and
activates it as version M+1 (`RolledBackFrom = K`); history is never rewritten.

**Crash recovery at boot:** read `active.json`; verify the referenced file's SHA-256. Missing/corrupt
pointer or hash mismatch ⇒ **refuse to start** with a diagnostic (fail-closed; the operator restores the
pointer, never a silent "latest"). Version files not in the journal are reported as orphans. If the active
version is missing from the journal (crash between pointer rename and journal append) it is appended with
`recovered: true`. If the active version has no `config.activated` audit event, boot appends one marked
`recovered: true`.

**Live audit-write failure.** The activation event goes through the same audit sink as requests, whose write
failure is already fail-closed (`Environment.FailFast`: an appliance that cannot write evidence stops). So a
failing evidence write after the pointer swap halts the process; at the next boot the active version is
missing its activation event and boot appends it with `recovered: true` before serving. The configuration
change therefore never runs unevidenced: either the event is written, or nothing is served until it is.

**Validation is total.** Every enum value must be a defined member (numeric out-of-range values are errors),
required objects/lists and their elements must be non-null, ids are unique and non-empty, and every error is a
422 with a field path — never an exception or 500. A validated candidate is deep-copied into immutable
collections when compiled; later mutation of the caller's objects cannot change an active snapshot.
**Boundary is not self-declared:** a connection may be `Local` only if its endpoint authority is on the
startup local-endpoint allow-list (`Providers:LocalEndpoints`, plus the legacy `Local:Endpoint`); cloud SDK
kinds (`OpenAi`, `Anthropic`) are always `Cloud`. A cloud host cannot be relabelled Local to reach PII/PHI.

**Bootstrap precedence:** (1) existing repository wins; (2) empty repository + `Config:Seed` ∈
{`demo-local-only`, `demo-hybrid`} ⇒ that seed becomes v1; (3) otherwise the legacy startup keys
(`Providers:Mode`, `OpenAI:*`, `Anthropic:*`, `Local:*`, `Budget:*`, `Pricing:*`, `Cache:*`,
`Routing:*`, `Auth:Keys` identities) are **imported** into v1 with `origin = legacy-import`, reproducing the
existing effective routing exactly (legacy cloud mode stays legacy cloud mode; the migration preview shows
its effective rules). A legacy local route without `Local:*` imports as an **explicitly disabled
placeholder connection** `local` ("not configured — import placeholder"), so the migration preview shows why
PII/PHI are refused; disabled connections are never eligible, in any mode. After v1 exists, those legacy keys
are ignored for routing and reported as such.
Startup-only (never console-editable): `Admin:Token`, all secret values, `Auth:Keys` key values,
`Audit:*`, `Config:Path`, and **`Providers:ProbeAllowList` — the egress allow-list**: a configuration edit
can never widen where Covenant may connect. Connection endpoints must be on the allow-list (or be the
legacy-imported endpoints, which are added to it at import) at validate, apply and execution time.

**Secrets.** A connection names a `secretRef`: an `IConfiguration` key, restricted to `Secrets:<name>` and
the legacy keys `OpenAI:ApiKey`, `Anthropic:ApiKey`, `Local:ApiKey`. The API reports `available: true|false`,
never the value. Saving a cloud connection whose secret is unavailable is a validation **error** (an
unusable save is not shown as success).

**Provider clients** are built with automatic redirects disabled, SDK-internal retries disabled (attempts are
Covenant's, counted and accounted per ADR-0013/0014) and the connection's timeout as the effective network
timeout. A target whose client cannot be built makes the candidate invalid (no silently skipped target).

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
