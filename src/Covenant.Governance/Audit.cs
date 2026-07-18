using Covenant.Core;

namespace Covenant.Governance;

/// <summary>
/// The outermost stage. It wraps the entire pipeline in a try/finally so that EVERY request —
/// allowed, denied, or errored — produces exactly one audit entry. Implemented as an enclosing stage
/// (rather than a final linear stage) precisely so denials and exceptions are still recorded.
/// Entries are handed to the sink off the hot path; this stage never blocks on durable persistence.
/// </summary>
public sealed class AuditStage(IAuditSink sink, TimeProvider? clock = null) : IPipelineStage
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
                Usage: ctx.Response?.Usage ?? Usage.Empty);

            await sink.EnqueueAsync(entry, ct);
        }
    }
}
