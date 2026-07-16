namespace Covenant.Core;

/// <summary>Continuation delegate that invokes the next stage in the chain.</summary>
public delegate Task PipelineDelegate(InferenceContext context, CancellationToken cancellationToken);

/// <summary>
/// One ordered stage of the inference pipeline. A stage may inspect or mutate the context, call
/// <paramref name="next"/> to continue, or short-circuit by returning without calling it (e.g. on a denial).
/// </summary>
public interface IPipelineStage
{
    Task InvokeAsync(InferenceContext context, PipelineDelegate next, CancellationToken cancellationToken);
}

/// <summary>
/// Composes stages into a single execution chain, in registration order. The chain is built once at
/// construction (hot-path friendly). Once a context is denied, no further stage runs — fail-closed.
/// </summary>
public sealed class InferencePipeline
{
    private readonly PipelineDelegate _entry;

    public InferencePipeline(IReadOnlyList<IPipelineStage> stages)
    {
        PipelineDelegate next = static (_, _) => Task.CompletedTask;
        for (int i = stages.Count - 1; i >= 0; i--)
        {
            var stage = stages[i];
            var localNext = next;
            next = (ctx, ct) => ctx.IsDenied ? Task.CompletedTask : stage.InvokeAsync(ctx, localNext, ct);
        }
        _entry = next;
    }

    public Task ExecuteAsync(InferenceContext context, CancellationToken cancellationToken)
        => _entry(context, cancellationToken);
}
