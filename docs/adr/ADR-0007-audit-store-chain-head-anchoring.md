# ADR-0007 — Audit store: chain-head anchoring; the file stays the evidence of record

**Status:** Accepted (2026-07-20)
**Date:** 2026-07-20

## Context
The hash-chained audit file detects edits, deletions, reordering, and forged hashes — but truncation
from the END is undetectable from the file alone (documented in `AuditChain.cs` since the chain
landed): an attacker who removes the last N lines leaves a perfectly valid shorter chain. Separately,
the Postgres question has recurred: should evidence move to a database?

## Decision
1. **The append-only hash-chained file remains the evidence of record.** No database replaces it: a
   relational store adds mutability surface (a DBA's DELETE is the same attack as `truncate`) without
   adding integrity. Queryability is already served by the evidence export and the evidence graph
   (ADR-0006), both derived and rebuildable.
2. **Chain-head anchoring closes the truncation gap.** Every N entries (`Audit:AnchorEvery`), the sink
   appends `{entryCount}\t{headHash}\t{timestamp}` to a separate anchor file (`Audit:AnchorPath`).
   Verification then requires the log to contain at least every anchored count with exactly the
   anchored hash at that position — a log truncated past any anchor fails verification. The anchor
   file's whole value is *placement*: the operator points it at storage with a different failure and
   attacker domain (another volume, a WORM share, object storage with object-lock). Exposure is
   bounded to the entries since the last anchor (the cadence is the knob).
3. **Both settings are required together** (one without the other → refuse to start); unset means
   anchoring off — the first-slice posture, with the gap still documented.
4. **Rotation archives log and anchor file together**; a fresh chain starts fresh anchors.
5. **Postgres trigger, recorded for the future:** multi-instance HA (shared serving state) or
   runtime-editable policy (a config store) — and then customer-operated, never bundled. Ledger
   replay cost at boot is the trigger for snapshotting, not for a database.

## Consequences
- (+) End-truncation becomes detectable wherever an operator can mount one independent path — no new
  infrastructure, no new dependency, ~40 bytes per anchor.
- (+) The threat model is explicit: an attacker must now compromise two storage domains coherently.
- (−) Entries newer than the last anchor remain truncatable — bounded, configurable, stated.
- (−) An attacker who can write BOTH files can still rewrite history; anchoring raises the bar, only
  external notarization (e.g. publishing anchor hashes to a customer's existing WORM/ledger service)
  removes it. Deliberately out of scope until a customer mandate names the target system.

## Alternatives considered
- **Move evidence to Postgres/SQL.** Rejected for evidence (mutability without integrity gain);
  triggers for a store recorded above.
- **Hash-chain the anchor file too.** Rejected: circular — anchors are trusted because of *where*
  they live, not how they're formatted; chaining them adds format complexity, not security.
- **Anchor every entry.** Rejected: doubles hot-path writes for negligible exposure reduction vs
  N=100; cadence is configurable for those who disagree.
- **External notarization now.** Deferred: needs a named customer target (their TSA, ledger DB, or
  object-lock bucket); the anchor-file seam is exactly where it will plug in.
