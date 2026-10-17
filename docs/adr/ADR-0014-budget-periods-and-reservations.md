# ADR-0014 — Budget scopes, UTC periods and atomic reservations

**Status:** Accepted
**Date:** 2026-10-16
**Supersedes in part:** ADR-0009 §5 (post-call threshold check only), the in-memory ledger rebuilt from
the audit segment. **Spec:** `docs/CONSOLE-V2-IMPLEMENTATION-SPEC.md` §4.5.

## Decision
**Budgets** (configuration): `id`, `scope` (`global` | `team:<id>` | `application:<id>`), `capUsd`,
`period` (`none` | `daily` | `monthly`, UTC boundaries; identity e.g. `2026-10`, `2026-10-16`, `all`),
`warnAt` (default [0.80, 0.95]), `enforcement` (`hard` | `warn-only`).

**Ledger** (`Budget:LedgerPath`, default `<Audit:Path>.ledger.jsonl`): an append-only journal, independent
of audit segments, of `reserve`/`settle`/`release` operations keyed by `(requestId, attemptId)` and recording
the **scopes charged** (`global`, `team:<t>`, `application:<a>`) with a UTC timestamp — **not** a budget id or
a period id — replayed at boot. A budget's spend is computed at read time as the sum of settled amounts for its
scope inside its current period window. Renaming a budget id, changing its period or editing its cap therefore
never resets accrued usage; changing a budget's scope shows that scope's own accrued history. An attempt is
charged to the scopes that applied **when it was reserved** (in-flight attempts across an activation settle
against their original reservation). **Audit archive never reopens a budget.**
Config apply never resets it. Boot recovery: a reservation with no settle/release is settled at its
reserved amount (`settle-recovered`) — conservative, never free capacity.

**Reservation.** Before each provider attempt, compute an upper-bound estimate: input tokens ≤ the prompt's
**UTF-8 byte count** + 16 per message (byte-level tokenizers never produce more tokens than bytes; chars/4 is
only an average and is never used for enforcement) at the target's input price + the **enforced** output bound (request `max_tokens`, capped by the application
default `maxOutputTokens`, default 1024 — the bound is sent to the provider so it is real) at the output
price. If reported usage exceeds the reservation (a provider ignoring the bound, tokenizer overhead), the full
reported cost is settled anyway — spend is never truncated to the reservation — and the overrun is flagged in
evidence; enforcement is therefore described as an **estimated** bound. Under one ledger lock, check every
applicable hard budget (`spent + reserved + estimate ≤ cap` for
global, team, application in the current period) and reserve in all or none. Refusal ⇒ governed denial at
stage `budget`. **Settle** exactly once per attempt with the actual cost (reported usage) or the
conservative estimate when usage is unavailable/failed (`estimated_*` basis), then **release** the
remainder exactly once (idempotent by key). Cancellation and fallback settle each attempt independently.
**Unpriced paid targets are ineligible** under any hard budget (validation error at apply). Local
targets with an explicit zero price reserve $0 ("zero marginal provider charge", infrastructure excluded).

Warnings at configured fractions are reported (status, evidence) and do not block; `warn-only` budgets
never block. Lowering a cap below spend blocks new paid work and keeps spend; raising/lowering is a
configuration activation (audited with before/after). Legacy migration: if no ledger exists, the current
audit segment's recorded spend is imported once as a `legacy-import` settle into the current period of the
global and team budgets. Precision: amounts are stored as decimal and displayed with significant digits
for sub-cent charges.

**Savings** ("Estimated provider charge avoided") are computed only when `Savings:Baseline` names a priced
connection/model; cache reuse and route selection are reported separately; otherwise hidden.

## Consequences
- (+) Concurrent requests cannot jointly overrun a hard cap beyond estimation error; the cap is an
  **estimated** enforcement bound, not an invoice ceiling, whenever usage is unreported.
- (−) A journal file is one more durable artifact to back up alongside the audit log.
