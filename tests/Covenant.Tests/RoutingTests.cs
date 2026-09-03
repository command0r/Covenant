using Covenant.Core;
using Covenant.Governance;
using Xunit;

namespace Covenant.Tests;

/// <summary>Complexity routing within the ordered permitted set: cheapest for simple prompts, strongest past the token threshold.</summary>
public class RoutingTests
{
    private static readonly RouteTarget Cheap = new("openai", "gpt-4o-mini");
    private static readonly RouteTarget Strong = new("openai", "gpt-4o");

    private static PolicyEngine Engine(long threshold = 100) => new(
        new PolicyConfig
        {
            AllowedRoutes = new Dictionary<DataClassification, IReadOnlyList<RouteTarget>>
            {
                [DataClassification.Internal] = [Cheap, Strong],
            }
        },
        new RoutingOptions { ComplexityTokenThreshold = threshold });

    private static InferenceContext Ctx(string content, string? requestedModel = null)
    {
        var ctx = new InferenceContext(new InferenceRequest(
            "tester", [new ChatMessage(ChatRole.User, content)], requestedModel, AttributionTags.Unattributed));
        ctx.Classification = DataClassification.Internal;
        return ctx;
    }

    [Fact]
    public void Short_prompt_takes_the_cheapest_permitted_route()
    {
        var outcome = Engine().Evaluate(Ctx("what is our leave policy?"));

        Assert.Equal(PolicyEffect.Allow, outcome.Effect);
        Assert.Equal(Cheap, outcome.Route);
    }

    [Fact]
    public void Complex_prompt_escalates_to_the_strongest_permitted_route()
    {
        var longPrompt = new string('x', 4_000);            // ~1000 estimated tokens > threshold 100
        var outcome = Engine().Evaluate(Ctx(longPrompt));

        Assert.Equal(PolicyEffect.Allow, outcome.Effect);
        Assert.Equal(Strong, outcome.Route);
        Assert.Contains("complexity-routed", outcome.Reason);
    }

    [Fact]
    public void Explicitly_requested_model_bypasses_complexity_routing_but_not_policy()
    {
        var longPrompt = new string('x', 4_000);
        var outcome = Engine().Evaluate(Ctx(longPrompt, requestedModel: "gpt-4o-mini"));

        Assert.Equal(Cheap, outcome.Route);                  // caller's explicit (permitted) choice wins

        var denied = Engine().Evaluate(Ctx("hi", requestedModel: "claude-3-opus"));
        Assert.Equal(PolicyEffect.Deny, denied.Effect);      // …but only within the permitted set
    }
}
