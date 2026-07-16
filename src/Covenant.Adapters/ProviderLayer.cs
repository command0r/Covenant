using Covenant.Core;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Adapters;

/// <summary>Resolves a route's AdapterKey to a Microsoft.Extensions.AI IChatClient.</summary>
public interface IChatClientRegistry
{
    bool TryResolve(string adapterKey, out MEAI.IChatClient client);
}

public sealed class ChatClientRegistry(IReadOnlyDictionary<string, MEAI.IChatClient> clients) : IChatClientRegistry
{
    public bool TryResolve(string adapterKey, out MEAI.IChatClient client)
        => clients.TryGetValue(adapterKey, out client!);
}

/// <summary>
/// The one place provider concepts exist (ADR-0001). Maps canonical → Microsoft.Extensions.AI, calls
/// the resolved IChatClient, and maps the response back to canonical. No governance logic lives here;
/// no provider concept escapes this class.
/// </summary>
public sealed class ProviderCallStage(IChatClientRegistry registry) : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        var route = ctx.Policy?.Route;
        if (route is null) { ctx.Deny("no route resolved before provider call"); return; }

        if (!registry.TryResolve(route.AdapterKey, out var client))
        {
            ctx.Deny($"no adapter registered for key '{route.AdapterKey}'"); // fail-closed
            return;
        }

        var messages = ctx.Request.Messages.Select(ToMeai).ToList();
        var options = new MEAI.ChatOptions { ModelId = route.ModelId };

        MEAI.ChatResponse response;
        try
        {
            response = await client.GetResponseAsync(messages, options, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // caller cancelled — not a provider failure
        }
        catch (Exception ex)
        {
            // Adapter contract: error mapping (src/CLAUDE.md). An upstream failure is a governed,
            // audited refusal — never an unhandled exception escaping the pipeline as a 500.
            ctx.Deny($"provider '{route.AdapterKey}' call failed: {ex.Message}", DenialKind.UpstreamFailure);
            return;
        }

        var usage = new Usage(
            InputTokens: response.Usage?.InputTokenCount ?? 0,
            OutputTokens: response.Usage?.OutputTokenCount ?? 0,
            CostUsd: 0m); // cost is computed by the attribution stage

        ctx.Response = new InferenceResponse(
            Message: new ChatMessage(ChatRole.Assistant, response.Text ?? string.Empty),
            Usage: usage,
            ServedByModel: route.ModelId);

        await next(ctx, ct);
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
