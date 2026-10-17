using Covenant.Core;
using Covenant.Governance;
using Xunit;

namespace Covenant.Tests;

/// <summary>ADR-0013 policy engine v2 and the configuration validator: precedence, ambiguity, pause semantics,
/// eligibility (mode boundary, sensitive-cloud rule, pricing), override pinning, the honestly named length heuristic.</summary>
public sealed class PolicyEngineV2Tests
{
    private static PolicyOutcome Eval(CovenantConfig c, DataClassification cls, string? profile = null, string? model = null, string prompt = "hi")
        => PolicyEngineV2.Evaluate(V2.Snap(c), cls, profile, model, [new ChatMessage(ChatRole.User, prompt)]);

    private static CovenantConfig WithPolicies(params PolicyConfigV2[] policies) => V2.Hybrid() with { Policies = policies };

    private static ConfigValidationResult Validate(CovenantConfig c, bool legacy = false, bool secrets = true)
        => ConfigValidator.Validate(c, V2.Env(legacy, secrets));

    [Fact]
    public void Demo_defaults_validate_cleanly_and_route_as_specified()
    {
        var c = V2.Hybrid();
        Assert.Empty(Validate(c).Errors);

        var pub = Eval(c, DataClassification.Public, "public-demo");
        Assert.Equal(new RouteTarget("openai", V2.CloudModel), pub.Route);
        Assert.Equal(new[] { new RouteTarget("openai", V2.CloudModel), new RouteTarget("ollama", V2.LocalTag) }, pub.Candidates);
        Assert.Equal("public", pub.PolicyId);
        Assert.Equal($"Public → openai/{V2.CloudModel} (fallback: ollama/{V2.LocalTag}) · policy public v1", pub.Explanation);

        foreach (var sensitive in new[] { DataClassification.Internal, DataClassification.Pii, DataClassification.Phi })
        {
            var o = Eval(c, sensitive);
            Assert.Equal(PolicyEffect.Allow, o.Effect);
            Assert.All(o.Candidates, t => Assert.Equal("ollama", t.AdapterKey));   // never a cloud fallback
        }
    }

    [Fact]
    public void Profile_specific_policy_takes_precedence_over_a_classification_only_policy()
    {
        var c = WithPolicies(
            V2.Policy("public", [DataClassification.Public], [V2.T("openai", V2.CloudModel)]),
            V2.Policy("public-demo-local", [DataClassification.Public], [V2.T("ollama", V2.LocalTag)], profiles: ["public-demo"]),
            V2.Policy("rest", [DataClassification.Internal, DataClassification.Pii, DataClassification.Phi], [V2.T("ollama", V2.LocalTag)]));
        Assert.Empty(Validate(c).Errors);                                    // defined precedence, not an overlap
        Assert.Equal("public-demo-local", Eval(c, DataClassification.Public, "public-demo").PolicyId);
        Assert.Equal("public", Eval(c, DataClassification.Public, "other-profile").PolicyId);
    }

    [Fact]
    public void Ambiguous_overlap_is_a_validation_error_and_a_runtime_refusal()
    {
        var c = WithPolicies(
            V2.Policy("a", [DataClassification.Internal, DataClassification.Pii], [V2.T("ollama", V2.LocalTag)]),
            V2.Policy("b", [DataClassification.Internal], [V2.T("ollama", V2.LocalTag)]),
            V2.Policy("phi", [DataClassification.Phi], [V2.T("ollama", V2.LocalTag)]));
        Assert.Contains(Validate(c).Errors, e => e.Code == "ambiguous_policy_overlap");
        var o = Eval(c, DataClassification.Internal);
        Assert.Equal(PolicyEffect.Deny, o.Effect);
        Assert.Contains("ambiguous", o.Reason, StringComparison.Ordinal);
        Assert.Equal(PolicyEffect.Allow, Eval(c, DataClassification.Pii).Effect);   // only one matches Pii
    }

    [Fact]
    public void Profile_specific_policies_with_disjoint_profiles_do_not_overlap()
    {
        var c = WithPolicies(
            V2.Policy("p1", [DataClassification.Public], [V2.T("ollama", V2.LocalTag)], profiles: ["public-demo"]),
            V2.Policy("p2", [DataClassification.Public], [V2.T("openai", V2.CloudModel)], profiles: ["internal-default"]),
            V2.Policy("rest", [DataClassification.Internal, DataClassification.Pii, DataClassification.Phi], [V2.T("ollama", V2.LocalTag)]));
        Assert.DoesNotContain(Validate(c).Errors, e => e.Code == "ambiguous_policy_overlap");
        var same = c with { Policies = [.. c.Policies, V2.Policy("p3", [DataClassification.Public], [V2.T("ollama", V2.LocalTag)], profiles: ["public-demo"])] };
        Assert.Contains(Validate(same).Errors, e => e.Code == "ambiguous_policy_overlap");
    }

    [Fact]
    public void Paused_policy_denies_its_matching_traffic()
    {
        var c = V2.Hybrid(tweak: x => x with { Policies = x.Policies.Select(p => p.Id == "internal" ? p with { Paused = true } : p).ToList() });
        var o = Eval(c, DataClassification.Internal);
        Assert.Equal(PolicyEffect.Deny, o.Effect);
        Assert.Equal("policy 'internal' is paused", o.Reason);
        Assert.Equal("internal", o.PolicyId);
    }

    [Fact]
    public void Disabled_policy_does_not_match_and_falls_through_to_refusal()
    {
        var c = V2.Hybrid(tweak: x => x with { Policies = x.Policies.Select(p => p.Id == "internal" ? p with { Enabled = false } : p).ToList() });
        Assert.Contains("no permitted route", Eval(c, DataClassification.Internal).Reason, StringComparison.Ordinal);
        Assert.Contains(Validate(c).Warnings, w => w.Code == "no_route_for_classification");
    }

    [Fact]
    public void Paused_route_is_skipped_and_the_next_approved_target_serves()
    {
        var c = WithPolicies(
            V2.Policy("public", [DataClassification.Public], [V2.T("openai", V2.CloudModel, paused: true), V2.T("ollama", V2.LocalTag)]),
            V2.Policy("rest", [DataClassification.Internal, DataClassification.Pii, DataClassification.Phi], [V2.T("ollama", V2.LocalTag)]));
        var o = Eval(c, DataClassification.Public, "public-demo");
        Assert.Equal(new RouteTarget("ollama", V2.LocalTag), o.Route);
        var excluded = Assert.Single(o.Excluded);
        Assert.Equal("route paused", excluded.Reason);

        var allPaused = WithPolicies(
            V2.Policy("public", [DataClassification.Public], [V2.T("openai", V2.CloudModel, paused: true), V2.T("ollama", V2.LocalTag, paused: true)]),
            V2.Policy("rest", [DataClassification.Internal, DataClassification.Pii, DataClassification.Phi], [V2.T("ollama", V2.LocalTag)]));
        var refused = Eval(allPaused, DataClassification.Public, "public-demo");
        Assert.Equal(PolicyEffect.Deny, refused.Effect);
        Assert.Contains("no eligible destination", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_only_mode_excludes_cloud_targets_even_if_a_cloud_connection_is_enabled()
    {
        var c = V2.Hybrid() with { Mode = ProviderMode.LocalOnly };
        Assert.Contains(Validate(c).Errors, e => e.Code == "cloud_in_local_only");   // validation refuses it …
        var o = Eval(c, DataClassification.Public, "public-demo");                    // … and the engine never selects it
        Assert.Equal(new RouteTarget("ollama", V2.LocalTag), o.Route);
        Assert.Contains(o.Excluded, x => x.Target.AdapterKey == "openai" && x.Reason.Contains("local-only", StringComparison.Ordinal));
    }

    [Fact]
    public void Sensitive_data_never_reaches_cloud_without_an_explicit_acknowledgement()
    {
        var c = WithPolicies(
            V2.Policy("public", [DataClassification.Public, DataClassification.Internal], [V2.T("ollama", V2.LocalTag)]),
            V2.Policy("sensitive", [DataClassification.Pii, DataClassification.Phi], [V2.T("openai", V2.CloudModel), V2.T("ollama", V2.LocalTag)]));
        var o = Eval(c, DataClassification.Pii);
        Assert.Equal(new[] { new RouteTarget("ollama", V2.LocalTag) }, o.Candidates);
        Assert.Contains(Validate(c).Warnings, w => w.Code == "sensitive_cloud_without_ack");

        var onlyCloud = WithPolicies(
            V2.Policy("public", [DataClassification.Public, DataClassification.Internal], [V2.T("ollama", V2.LocalTag)]),
            V2.Policy("sensitive", [DataClassification.Pii, DataClassification.Phi], [V2.T("openai", V2.CloudModel)]));
        Assert.Equal(PolicyEffect.Deny, Eval(onlyCloud, DataClassification.Phi).Effect);   // refused, never sent to cloud

        var acknowledged = WithPolicies(
            V2.Policy("public", [DataClassification.Public, DataClassification.Internal], [V2.T("ollama", V2.LocalTag)]),
            V2.Policy("sensitive", [DataClassification.Pii, DataClassification.Phi], [V2.T("openai", V2.CloudModel)], allowSensitiveCloud: true));
        Assert.Equal(new RouteTarget("openai", V2.CloudModel), Eval(acknowledged, DataClassification.Pii).Route);
        Assert.Contains(Validate(acknowledged).Warnings, w => w.Code == "sensitive_cloud_without_ack" && w.Message.Contains("WARNING", StringComparison.Ordinal));
    }

    [Fact]
    public void Model_override_must_be_an_eligible_target_and_pins_it()
    {
        var c = V2.Hybrid();
        var pinned = Eval(c, DataClassification.Public, "public-demo", model: V2.LocalTag);
        Assert.Equal(new[] { new RouteTarget("ollama", V2.LocalTag) }, pinned.Candidates);   // no fallback after a pin
        Assert.Equal(new RouteTarget("ollama", V2.LocalTag), Eval(c, DataClassification.Public, "public-demo", model: $"ollama/{V2.LocalTag}").Route);

        var notInPolicy = Eval(c, DataClassification.Internal, model: V2.CloudModel);
        Assert.Equal(PolicyEffect.Deny, notInPolicy.Effect);
        Assert.Equal($"model '{V2.CloudModel}' not permitted for classification 'Internal'", notInPolicy.Reason);

        var paused = c with { Policies = c.Policies.Select(p => p.Id != "public" ? p : p with { Targets = [V2.T("openai", V2.CloudModel, paused: true), V2.T("ollama", V2.LocalTag)] }).ToList() };
        var excludedPin = Eval(paused, DataClassification.Public, "public-demo", model: V2.CloudModel);
        Assert.Equal(PolicyEffect.Deny, excludedPin.Effect);
        Assert.Contains("route paused", excludedPin.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Length_heuristic_is_named_honestly_and_uses_the_configured_threshold()
    {
        var c = WithPolicies(
            V2.Policy("general", [DataClassification.Public, DataClassification.Internal],
                [V2.T("openai", V2.CloudModel), V2.T("openai", "gpt-4o")], strategy: RoutingStrategy.LengthHeuristic, threshold: 10),
            V2.Policy("sensitive", [DataClassification.Pii, DataClassification.Phi], [V2.T("ollama", V2.LocalTag)]));

        var shortO = Eval(c, DataClassification.Internal, prompt: new string('a', 40));   // 10 tokens: not above 10
        Assert.Equal(V2.CloudModel, shortO.Route!.ModelId);
        var longO = Eval(c, DataClassification.Internal, prompt: new string('a', 44));    // 11 tokens: above 10
        Assert.Equal("gpt-4o", longO.Route!.ModelId);
        Assert.Contains("length-based heuristic", longO.Reason, StringComparison.Ordinal);
        Assert.Contains("threshold 10", longO.Reason, StringComparison.Ordinal);
        Assert.Equal(new[] { "gpt-4o", V2.CloudModel }, longO.Candidates.Select(t => t.ModelId).ToArray());
    }

    [Fact]
    public void Unpriced_paid_target_under_a_hard_budget_is_ineligible_and_invalid()
    {
        var c = V2.Hybrid() with { Connections = [V2.Ollama(), V2.OpenAi(priced: false)] };
        Assert.Contains(Validate(c).Errors, e => e.Code == "unpriced_paid_target_under_hard_budget");
        var o = Eval(c, DataClassification.Public, "public-demo");
        Assert.Equal(new RouteTarget("ollama", V2.LocalTag), o.Route);
        Assert.Contains(o.Excluded, x => x.Reason.Contains("unpriced", StringComparison.Ordinal));
    }

    [Fact]
    public void Disabled_or_unknown_connection_targets_are_ineligible_outside_legacy_cloud()
    {
        var disabled = V2.Hybrid() with { Connections = [V2.Ollama(), V2.OpenAi(enabled: false)] };
        Assert.Contains(Eval(disabled, DataClassification.Public, "public-demo").Excluded, x => x.Reason.Contains("disabled", StringComparison.Ordinal));

        var unknown = V2.Hybrid() with { Connections = [V2.OpenAi()] };   // ollama removed
        Assert.Equal(PolicyEffect.Deny, Eval(unknown, DataClassification.Internal).Effect);
        Assert.Contains(Validate(unknown).Errors, e => e.Code == "unknown_connection");
        Assert.Contains(Validate(unknown, legacy: true).Warnings, w => w.Code == "unknown_connection");
        Assert.DoesNotContain(Validate(unknown, legacy: true).Errors, e => e.Code == "unknown_connection");
    }

    [Fact]
    public void Validator_reports_contract_codes()
    {
        var bad = V2.Hybrid() with
        {
            Connections =
            [
                V2.Ollama() with { Endpoint = "http://10.9.9.9:11434/v1" },                     // not allow-listed
                V2.OpenAi() with { Models = [new ModelConfig { ModelId = V2.CloudModel, Price = new PriceConfig { InputPer1M = 1, OutputPer1M = 1, Basis = "x" } }] },
            ],
            Applications =
            [
                new ApplicationConfig { Id = "a1", Team = "t", Enabled = true, ConsoleTesting = true, GrantedProfiles = ["nope"], DefaultProfile = "public-demo" },
                new ApplicationConfig { Id = "a1", Team = "t", Enabled = true, ConsoleTesting = true, GrantedProfiles = [] },
            ],
            Policies =
            [
                V2.Policy("public", [DataClassification.Public], [V2.T("openai", "gpt-4o")]),           // model not on the connection
                V2.Policy("rest", [DataClassification.Internal, DataClassification.Pii, DataClassification.Phi], [V2.T("ollama", V2.LocalTag)]),
            ],
            Budgets = [new BudgetConfigV2 { Id = "b", Scope = "teams:x", CapUsd = 1 }],
        };
        var codes = Validate(bad, secrets: false).Errors.Select(e => e.Code).ToHashSet();
        foreach (var expected in new[] { "endpoint_not_allow_listed", "secret_unavailable", "profile_not_found", "default_profile_not_granted",
                     "duplicate_id", "unknown_model", "invalid_budget_scope", "missing_global_budget" })
            Assert.Contains(expected, codes);
    }

    [Fact]
    public void Summary_states_the_effective_route_or_refusal()
    {
        var c = V2.Hybrid();
        Assert.Equal($"openai/{V2.CloudModel} (fallback: ollama/{V2.LocalTag})", PolicyEngineV2.Summary(V2.Snap(c), DataClassification.Public, "public-demo"));
        var noLocal = c with { Connections = [V2.Ollama(enabled: false), V2.OpenAi()] };
        Assert.StartsWith("refused:", PolicyEngineV2.Summary(V2.Snap(noLocal), DataClassification.Phi, null), StringComparison.Ordinal);
    }
}
