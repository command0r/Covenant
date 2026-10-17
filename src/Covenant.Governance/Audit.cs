using System.Security.Cryptography;
using System.Text;
using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Outermost stage: try/finally so EVERY request — allowed, denied, or errored — yields exactly one audit entry, handed to the sink off the hot path.</summary>
/// <param name="promptPreviewChars">0 (default) = no input content in evidence; a positive value is an EXPLICIT operator opt-in (Audit:PromptPreviewChars) to a truncated preview.</param>
public sealed class AuditStage(IAuditSink sink, TimeProvider? clock = null, int promptPreviewChars = 0) : IPipelineStage
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        try
        {
            await next(ctx, ct);
        }
        catch (OperationCanceledException)
        {
            // The caller went away mid-request. Still evidence: record it as an incomplete request,
            // never as served, and never let the cancelled token suppress the audit write below.
            ctx.Deny("request cancelled before completion", DenialKind.Cancelled);
            throw;
        }
        finally
        {
            // Final outcome, not any single stage's opinion: only a request that produced a response
            // was allowed. Denied anywhere, or errored with no response → recorded as a denial.
            bool served = !ctx.IsDenied && ctx.Response is not null;
            ctx.CompleteTrace();   // stages never entered → skipped (not_reached | cache_hit) before evidence is written
            int previewChars = ctx.Configuration?.Config.Settings.PromptPreviewChars ?? promptPreviewChars;
            var id = ctx.Request.RequestId ?? $"req_{Guid.NewGuid():n}";
            var usage = ctx.Response?.Usage ?? ctx.FailedCallUsage ?? Usage.Empty;
            // ADR-0014: the request's charge is everything settled across its attempts (a failed primary plus the served
            // fallback), not only the served attempt; Attempts[] carries the breakdown.
            if (ctx.SettledUsd is { } settled && ctx.Attempts.Count > 0) usage = usage with { CostUsd = settled };
            bool v3 = ctx.Configuration is not null;
            // Review round 2 (B2): the request-level basis describes the TOTAL — "reported" only when every charged
            // attempt is reported; one shared estimated basis stays that basis; differing bases are "mixed".
            if (ctx.Attempts.Count > 0) ctx.CostBasis = CostBases.Aggregate(ctx.Attempts, ctx.CostBasis);
            var servedAttempt = ctx.ServedTarget is { } st
                ? ctx.Attempts.LastOrDefault(a => a.Outcome == "served" && a.ConnectionId == st.AdapterKey && a.ModelId == st.ModelId)
                : null;
            var entry = new AuditEntry(
                Id: id,
                TimestampUtc: _clock.GetUtcNow(),
                Principal: ctx.Identity.Principal,
                Tags: ctx.Identity.Tags,
                Classification: ctx.Classification,
                Effect: served ? PolicyEffect.Allow : PolicyEffect.Deny,
                Reason: ctx.DenialReason
                    ?? (served ? ctx.Policy?.Reason ?? "allowed" : "no response produced (errored or incomplete)"),
                ServedByModel: ctx.Response?.ServedByModel,
                Usage: usage,
                DurationMs: (_clock.GetUtcNow() - ctx.StartedUtc).TotalMilliseconds,
                // Size + fingerprint, never content: the SHA-256 proves WHICH prompt this entry is
                // about (holder of the text can verify), while the text itself stays in the data plane.
                PromptChars: ctx.Request.Messages.Sum(m => m.Content.Length),
                PromptSha256: Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(string.Join('\n', ctx.Request.Messages.Select(m => m.Content))))).ToLowerInvariant(),
                Signal: ctx.ClassificationSignal,
                PromptPreview: previewChars > 0
                    ? Truncate(string.Join(" ⏎ ", ctx.Request.Messages.Select(m => m.Content)), previewChars)
                    : null,
                CacheHit: ctx.ServedFromCache,
                SchemaVersion: 2,
                CorrelationId: id,
                ClassificationEvaluated: ctx.ClassificationEvaluated,
                DenialKind: ctx.IsDenied ? ctx.DenialKind.ToString() : null,
                StoppedAtStage: ctx.IsDenied ? ctx.StoppedAtStage : null,
                SelectedModel: ctx.Policy?.Route?.ModelId,
                AttemptedModel: ctx.AttemptedModel,
                Stream: ctx.Request.Stream,
                UsageReported: ctx.AttemptedModel is null ? null : ctx.Response?.UsageReported ?? false,
                CostBasis: ctx.CostBasis ?? (ctx.AttemptedModel is null ? "no_call" : "unknown_failed_call"),
                // v3 (additive): configuration binding and identity — only recorded when a configuration
                // version was bound (legacy compositions keep writing the v2 shape exactly).
                EventType: null,
                ConfigVersion: ctx.ConfigVersion,
                EntryPoint: ctx.Configuration is null ? null : ctx.EntryPoint,
                OperatorRef: ctx.OperatorRef,
                ApplicationId: ctx.ApplicationId,
                ProfileId: ctx.ProfileId,
                BaselineClassification: ctx.BaselineClassification,
                Attempts: v3 ? ctx.Attempts.Select(a => a.ToAudit()).ToList() : null,
                Trace: v3 ? ctx.CompactTrace() : null,
                ReservedUsd: v3 ? ctx.ReservedUsd : null,
                SettledUsd: v3 ? ctx.SettledUsd : null,
                RequestedMaxOutputTokens: v3 ? ctx.Request.MaxOutputTokens : null,
                EffectiveMaxOutputTokens: v3 ? servedAttempt?.EffectiveMaxOutputTokens : null,
                PartialOutputChars: v3 && !served && ctx.PartialOutputChars > 0 ? ctx.PartialOutputChars : null,
                ServedConnectionId: v3 && served ? ctx.ServedTarget?.AdapterKey : null);

            // CancellationToken.None: a cancelled request must still produce its audit record.
            await sink.EnqueueAsync(entry, CancellationToken.None);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
