using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Per-model token pricing in USD per 1,000 tokens.</summary>
public interface IPriceBook
{
    bool TryGetPrice(string modelId, out (decimal InputPer1K, decimal OutputPer1K) price);
}

public sealed class PriceBook(IReadOnlyDictionary<string, (decimal InputPer1K, decimal OutputPer1K)> prices) : IPriceBook
{
    public bool TryGetPrice(string modelId, out (decimal InputPer1K, decimal OutputPer1K) price)
        => prices.TryGetValue(modelId, out price);
}

/// <summary>
/// Runs after the provider call (it sits after the provider stage in the chain, so the response is
/// already on the context). Computes cost from usage + price book, finalizes the response Usage with
/// that cost, and records attribution against the caller's tags.
/// </summary>
public sealed class AttributionStage(IPriceBook priceBook) : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        if (ctx.Response is { } resp && ctx.Policy?.Route is { } route)
        {
            decimal cost = priceBook.TryGetPrice(route.ModelId, out var p)
                ? (resp.Usage.InputTokens / 1000m * p.InputPer1K) + (resp.Usage.OutputTokens / 1000m * p.OutputPer1K)
                : 0m;

            var priced = resp.Usage with { CostUsd = cost };
            ctx.Response = resp with { Usage = priced };
            ctx.Attribution = new AttributionRecord(ctx.Identity.Tags, route.ModelId, priced);
        }

        await next(ctx, ct);
    }
}
