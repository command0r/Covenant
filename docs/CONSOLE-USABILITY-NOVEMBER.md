# November usability correction

Preserve existing uncommitted October work. Preserve Playground responses, presentation mode and replay. Codex owns launcher/state recovery, live configuration and verification. Claude owns the UI implementation. No commits, subagents, runtime changes or secret reads.

## Verified defects

Running port 5210 is local-only configuration v1, with only Ollama, both Public/Internal routed locally and search disabled. Previous v7 hybrid state was under /tmp and is gone. The API contains all three user requests, although Requests showed only one. This is not simply operator misunderstanding.

## Required changes

1. Distinguish workload/data classification from destination. Public means cloud-eligible only under policy, not cloud execution. Show actual approved Local/Cloud destination and model beside workload selection, refreshed on form/config changes. Configuration-needed states must link to the relevant setup. Do not weaken classification or policy. Clear example labels/expectations when profile or relevant form values change; an Internal coding example cannot remain labelled as such after switching Public.
2. Disabled web search needs a precise visible reason and direct action even when Off: Local-only mode, profile restriction, missing key, price or disabled service. Internal workloads remain local. Correctly configured Public workloads must support Auto/Required. Hide irrelevant location controls. Never bypass controls.
3. Fix Requests refresh on navigation and successive completion. Inspect stale filters/pagination/snapshot bugs. Show active filters and Clear filters, count, loading and errors. All allowed, denied, cached and streamed requests stay discoverable. Add meaningful regressions.
4. Redesign Policies into Routes, Applications and Workloads views. Routes show match -> primary -> optional fallback -> state -> per-item Edit/Pause. Edit opens a focused accessible dialog/side panel titled with the entity, NOT an appended wall of fields. Configure primary/fallback with eligible model selectors; advanced retries/threshold collapsed. Applications/workloads get entity-specific editing. Preserve existing versioned ConfigEditor validation, conflict handling and deliberate Save/Cancel. Never save unrelated draft changes.
5. Budgets: per-scope Edit dialog; separate Add dialog. Settings: concise section summaries and section-specific dialogs. Shared dialog needs focus trap, Escape, focus return, safe dirty cancellation and visible action footer. No new dependencies. Test save/cancel isolation.
6. Models: clearly distinguish configured connections, available credentials and tested usability; explicit Add/Configure cloud flow. Repair alignment of model IDs, route arrows, badges, labels and Overview local route. Check 1280x720 and 1920x1080 in light/dark. No awkward narrow columns beside empty space.
7. Trace: preserve success/failure status colors, add a distinct accent and proportional duration indicator for the longest measured stage. Describe share of measured processing, not necessarily total wall time. Do not fabricate timings or mark slow as failed. Keep replay and <1ms/skipped semantics.
8. Audit: rename Verify to Check audit integrity. Explain exactly which hash links/checkpoints are checked (inspect actual implementation); it does NOT validate answer accuracy or compliance. Show checked count/time/result and actionable errors. Button must run fresh verification.
9. Usage guide starts with 3-5 practical sentences, technical reference later. Avoid explanatory walls in product flows.

## Acceptance

Build/tests plus actual browser verification: Internal local coding; switch Public and clearly see configured cloud route; search works when allowed and has explicit reason when blocked; every run appears in Requests; modal editing save/cancel/conflict behavior; integrity check reruns; no alignment/overflow at tested sizes. Test values and configuration must survive a normal launcher restart. Deliver a compilable checkpoint and pause for Codex review. Do not claim unexecuted tests passed.
