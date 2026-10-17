using System.Text;
using Covenant.Core;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Adapters;

/// <summary>Resolves a route (adapter + model) to a Microsoft.Extensions.AI IChatClient.</summary>
public interface IChatClientRegistry
{
    bool TryResolve(string adapterKey, string modelId, out MEAI.IChatClient client);
}

/// <summary>Keys: exact "adapter:model" (how the Host registers — a route to an unregistered model
/// fails closed) or a bare "adapter" wildcard for test doubles. Exact match wins.</summary>
public sealed class ChatClientRegistry(IReadOnlyDictionary<string, MEAI.IChatClient> clients) : IChatClientRegistry
{
    public bool TryResolve(string adapterKey, string modelId, out MEAI.IChatClient client)
        => clients.TryGetValue($"{adapterKey}:{modelId}", out client!)
        || clients.TryGetValue(adapterKey, out client!);
}

/// <summary>Observed provider health (Task 06): written by real pipeline calls and admin probes. A later
/// failure always supersedes an earlier success for current state; last-success time is preserved.</summary>
public interface IProviderHealth
{
    void RecordSuccess(string adapterKey, string modelId);
    void RecordFailure(string adapterKey, string modelId, string error);
}

/// <summary>The one place provider concepts exist (ADR-0001): canonical ↔ Microsoft.Extensions.AI
/// mapping around the resolved IChatClient. No governance here; no provider concept escapes.</summary>
public sealed class ProviderCallStage(IChatClientRegistry registry, IProviderHealth? health = null) : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        var route = ctx.Policy?.Route;
        if (route is null) { ctx.Deny("no route resolved before provider call"); return; }

        if (!registry.TryResolve(route.AdapterKey, route.ModelId, out var client))
        {
            ctx.Deny($"no adapter registered for '{route.AdapterKey}:{route.ModelId}'"); // fail-closed
            return;
        }

        var messages = ctx.Request.Messages.Select(ToMeai).ToList();
        ctx.AttemptedModel = route.ModelId;   // a call is now actually started (not merely selected)
        var options = new MEAI.ChatOptions { ModelId = route.ModelId, MaxOutputTokens = ctx.Request.MaxOutputTokens };

        // ADR-0002: streaming is an emission mode, not a different pipeline. The stream is consumed
        // HERE so attribution, budget, and audit run on the normal unwind with real usage.
        if (ctx.Request.Stream && ctx.DeltaSink is { } sink)
        {
            var text = new StringBuilder();
            MEAI.UsageDetails? usageDetails = null;
            try
            {
                await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
                {
                    foreach (var uc in update.Contents.OfType<MEAI.UsageContent>())
                        usageDetails = uc.Details;

                    if (update.Text is { Length: > 0 } fragment)
                    {
                        text.Append(fragment);
                        ctx.PartialOutputChars = text.Length;
                        try
                        {
                            await sink(new ChatDelta(fragment), ct);
                        }
                        catch (Exception) when (!ct.IsCancellationRequested)
                        {
                            // Writing to the CLIENT failed (it disconnected): an incomplete request, not
                            // a provider failure — no health penalty for the provider.
                            ctx.Deny("client disconnected during streaming", DenialKind.Cancelled);
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancelled — not a provider failure (a provider-side timeout is, below)
            }
            catch (Exception ex)
            {
                // Mid-stream failure: same governed refusal as the buffered path. The Host decides
                // how to terminate the already-started SSE stream; evidence records the denial.
                ctx.Deny($"provider '{route.AdapterKey}' stream failed: {Bound(ex.Message)}", DenialKind.UpstreamFailure);
                health?.RecordFailure(route.AdapterKey, route.ModelId, Bound(ex.Message));
                return;
            }

            health?.RecordSuccess(route.AdapterKey, route.ModelId);
            ctx.Response = new InferenceResponse(
                Message: new ChatMessage(ChatRole.Assistant, text.ToString()),
                Usage: new Usage(usageDetails?.InputTokenCount ?? 0, usageDetails?.OutputTokenCount ?? 0, 0m),
                ServedByModel: route.ModelId,
                UsageReported: usageDetails?.InputTokenCount is not null && usageDetails.OutputTokenCount is not null);

            await next(ctx, ct);
            return;
        }

        MEAI.ChatResponse response;
        try
        {
            response = await client.GetResponseAsync(messages, options, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancelled — not a provider failure (a provider-side timeout is, below)
        }
        catch (Exception ex)
        {
            // Adapter contract: error mapping (src/CLAUDE.md). An upstream failure is a governed,
            // audited refusal — never an unhandled exception escaping the pipeline as a 500.
            ctx.Deny($"provider '{route.AdapterKey}' call failed: {Bound(ex.Message)}", DenialKind.UpstreamFailure);
            health?.RecordFailure(route.AdapterKey, route.ModelId, Bound(ex.Message));
            return;
        }
        health?.RecordSuccess(route.AdapterKey, route.ModelId);

        var usage = new Usage(
            InputTokens: response.Usage?.InputTokenCount ?? 0,
            OutputTokens: response.Usage?.OutputTokenCount ?? 0,
            CostUsd: 0m); // cost is computed by the attribution stage

        ctx.Response = new InferenceResponse(
            Message: new ChatMessage(ChatRole.Assistant, response.Text ?? string.Empty),
            Usage: usage,
            ServedByModel: route.ModelId,
            UsageReported: response.Usage?.InputTokenCount is not null && response.Usage.OutputTokenCount is not null);

        await next(ctx, ct);
    }

    /// <summary>Provider error text lands in evidence: bounded, single-line.</summary>
    private static string Bound(string message)
    {
        var oneLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 240 ? oneLine : oneLine[..240] + "…";
    }

    private static MEAI.ChatMessage ToMeai(ChatMessage m) => new(ToMeaiRole(m.Role), m.Content);

    private static MEAI.ChatRole ToMeaiRole(ChatRole role) => role switch
    {
        ChatRole.System => MEAI.ChatRole.System,
        ChatRole.User => MEAI.ChatRole.User,
        ChatRole.Assistant => MEAI.ChatRole.Assistant,
        ChatRole.Tool => MEAI.ChatRole.Tool,
        _ => MEAI.ChatRole.User
    };
}
