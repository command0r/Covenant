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

/// <summary>Runs after the provider call: computes cost from usage + price book, finalizes the
/// response Usage, and records attribution against the caller's tags.</summary>
public sealed class AttributionStage(IPriceBook priceBook) : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        if (ctx.Response is { } resp && ctx.Policy?.Route is { } route)
        {
            decimal cost;
            (decimal InputPer1K, decimal OutputPer1K) p;
            bool hasPrice = ctx.Configuration is { } cfg
                ? ConfigPrices.TryGet(cfg, route.AdapterKey, route.ModelId, out p)   // connection-scoped (ADR-0011)
                : priceBook.TryGetPrice(route.ModelId, out p);
            if (!hasPrice)
            {
                cost = 0m;
                ctx.CostBasis = "unpriced";               // unknown, flagged — never a real $0
            }
            else if (resp.UsageReported)
            {
                cost = (resp.Usage.InputTokens / 1000m * p.InputPer1K) + (resp.Usage.OutputTokens / 1000m * p.OutputPer1K);
                ctx.CostBasis = p.InputPer1K == 0m && p.OutputPer1K == 0m ? "zero_priced" : "reported";
            }
            else
            {
                // ADR-0009: billable call, provider reported no usage → estimate from text and CHARGE it,
                // so unknown usage cannot escape the budget. Token counts stay unreported.
                long inTok = TokenEstimate.FromChars(ctx.Request.Messages.Sum(m => m.Content.Length));
                long outTok = TokenEstimate.FromChars(resp.Message.Content.Length);
                cost = inTok / 1000m * p.InputPer1K + outTok / 1000m * p.OutputPer1K;
                ctx.CostBasis = p.InputPer1K == 0m && p.OutputPer1K == 0m ? "zero_priced" : "estimated_from_text";
            }

            var priced = resp.Usage with { CostUsd = cost };
            ctx.Response = resp with { Usage = priced };
            ctx.Attribution = new AttributionRecord(ctx.Identity.Tags, route.ModelId, priced);
        }

        await next(ctx, ct);
    }
}
