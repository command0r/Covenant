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
        finally
        {
            // Final outcome, not any single stage's opinion: only a request that produced a response
            // was allowed. Denied anywhere, or errored with no response → recorded as a denial.
            bool served = !ctx.IsDenied && ctx.Response is not null;
            var entry = new AuditEntry(
                Id: Guid.NewGuid().ToString("n"),
                TimestampUtc: _clock.GetUtcNow(),
                Principal: ctx.Identity.Principal,
                Tags: ctx.Identity.Tags,
                Classification: ctx.Classification,
                Effect: served ? PolicyEffect.Allow : PolicyEffect.Deny,
                Reason: ctx.DenialReason
                    ?? (served ? ctx.Policy?.Reason ?? "allowed" : "no response produced (errored or incomplete)"),
                ServedByModel: ctx.Response?.ServedByModel,
                Usage: ctx.Response?.Usage ?? Usage.Empty,
                DurationMs: (_clock.GetUtcNow() - ctx.StartedUtc).TotalMilliseconds,
                // Size + fingerprint, never content: the SHA-256 proves WHICH prompt this entry is
                // about (holder of the text can verify), while the text itself stays in the data plane.
                PromptChars: ctx.Request.Messages.Sum(m => m.Content.Length),
                PromptSha256: Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(string.Join('\n', ctx.Request.Messages.Select(m => m.Content))))).ToLowerInvariant(),
                Signal: ctx.ClassificationSignal,
                PromptPreview: promptPreviewChars > 0
                    ? Truncate(string.Join(" ⏎ ", ctx.Request.Messages.Select(m => m.Content)), promptPreviewChars)
                    : null,
                CacheHit: ctx.ServedFromCache);

            await sink.EnqueueAsync(entry, ct);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
