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

    private static ConfigValidationResult Validate(CovenantConfig c, bool secrets = true)
        => ConfigValidator.Validate(c, V2.Env(secrets));

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

    /// <summary>Review fix 5: no AllowSensitiveCloud escape hatch. A PII/PHI policy listing a cloud target is a
    /// validation ERROR (sensitive_cloud_target), and the engine still excludes cloud targets for PII/PHI.</summary>
    [Fact]
    public void Sensitive_data_never_reaches_cloud_and_a_sensitive_policy_with_a_cloud_target_is_invalid()
    {
        var c = WithPolicies(
            V2.Policy("public", [DataClassification.Public, DataClassification.Internal], [V2.T("ollama", V2.LocalTag)]),
            V2.Policy("sensitive", [DataClassification.Pii, DataClassification.Phi], [V2.T("openai", V2.CloudModel), V2.T("ollama", V2.LocalTag)]));
        var error = Assert.Single(Validate(c).Errors, e => e.Code == "sensitive_cloud_target");
        Assert.Equal("policies[1].targets[0]", error.Path);
        var o = Eval(c, DataClassification.Pii);                                  // defensive: engine excludes it anyway
        Assert.Equal(new[] { new RouteTarget("ollama", V2.LocalTag) }, o.Candidates);
        Assert.Contains(o.Excluded, x => x.Target.AdapterKey == "openai" && x.Reason.Contains("never routed to a cloud", StringComparison.Ordinal));

        var onlyCloud = WithPolicies(
            V2.Policy("public", [DataClassification.Public, DataClassification.Internal], [V2.T("ollama", V2.LocalTag)]),
            V2.Policy("sensitive", [DataClassification.Pii, DataClassification.Phi], [V2.T("openai", V2.CloudModel)]));
        Assert.Contains(Validate(onlyCloud).Errors, e => e.Code == "sensitive_cloud_target");
        Assert.Equal(PolicyEffect.Deny, Eval(onlyCloud, DataClassification.Phi).Effect);   // refused, never sent to cloud
        Assert.Equal(PolicyEffect.Deny, Eval(onlyCloud, DataClassification.Pii, model: V2.CloudModel).Effect);
        Assert.Equal(PolicyEffect.Deny, Eval(onlyCloud with { Mode = ProviderMode.LegacyCloud }, DataClassification.Pii).Effect);
        Assert.Null(typeof(PolicyConfigV2).GetProperty("AllowSensitiveCloud"));            // the escape hatch is gone
    }

    /// <summary>Review fix 9: a paused profile-specific policy refuses its traffic; it never falls through to the
    /// less specific (classification-only) policy that would otherwise serve it.</summary>
    [Fact]
    public void Paused_specific_policy_never_falls_through_to_a_less_specific_policy()
    {
        var c = WithPolicies(
            V2.Policy("public-generic", [DataClassification.Public], [V2.T("openai", V2.CloudModel)]),
            V2.Policy("public-demo-only", [DataClassification.Public], [V2.T("ollama", V2.LocalTag)], profiles: ["public-demo"], paused: true),
            V2.Policy("rest", [DataClassification.Internal, DataClassification.Pii, DataClassification.Phi], [V2.T("ollama", V2.LocalTag)]));
        Assert.Empty(Validate(c).Errors);
        var o = Eval(c, DataClassification.Public, "public-demo");
        Assert.Equal(PolicyEffect.Deny, o.Effect);
        Assert.Equal("public-demo-only", o.PolicyId);
        Assert.Equal("policy 'public-demo-only' is paused", o.Reason);
        Assert.Null(o.Route);
        // With an override naming the generic policy's target, still refused (the paused policy decided).
        Assert.Equal(PolicyEffect.Deny, Eval(c, DataClassification.Public, "public-demo", model: V2.CloudModel).Effect);
        // Other profiles are unaffected: the generic policy serves them.
        Assert.Equal("public-generic", Eval(c, DataClassification.Public, "internal-default").PolicyId);
    }

    /// <summary>Review fix 2: a bare model id eligible on two connections is REFUSED (never first-wins);
    /// connection/model resolves exactly; a bare id eligible on exactly one connection still pins.</summary>
    [Fact]
    public void Ambiguous_bare_model_override_is_refused_and_connection_slash_model_resolves_exactly()
    {
        var c = V2.Hybrid() with
        {
            Connections = [V2.Ollama(id: "local-a"), V2.Ollama(id: "local-b"), V2.OpenAi()],
            Policies =
            [
                V2.Policy("public", [DataClassification.Public], [V2.T("openai", V2.CloudModel), V2.T("local-a", V2.LocalTag)]),
                V2.Policy("internal", [DataClassification.Internal, DataClassification.Pii, DataClassification.Phi],
                    [V2.T("local-a", V2.LocalTag), V2.T("local-b", V2.LocalTag)]),
            ],
        };
        Assert.Empty(Validate(c).Errors);

        var ambiguous = Eval(c, DataClassification.Internal, model: V2.LocalTag);
        Assert.Equal(PolicyEffect.Deny, ambiguous.Effect);
        Assert.Equal($"model id '{V2.LocalTag}' is ambiguous across connections local-a, local-b — use connection/model", ambiguous.Reason);
        Assert.Null(ambiguous.Route);

        var exactB = Eval(c, DataClassification.Internal, model: $"local-b/{V2.LocalTag}");
        Assert.Equal(new[] { new RouteTarget("local-b", V2.LocalTag) }, exactB.Candidates);   // pinned, no fallback
        var exactA = Eval(c, DataClassification.Internal, model: $"LOCAL-A/{V2.LocalTag}");
        Assert.Equal(new RouteTarget("local-a", V2.LocalTag), exactA.Route);

        // Only ONE eligible target carries the bare id under the Public policy ⇒ it resolves.
        Assert.Equal(new RouteTarget("local-a", V2.LocalTag), Eval(c, DataClassification.Public, "public-demo", model: V2.LocalTag).Route);

        // Ambiguity is about ELIGIBLE targets: pause local-b and the bare id resolves to local-a.
        var paused = c with { Policies = c.Policies.Select(p => p.Id != "internal" ? p : p with { Targets = [V2.T("local-a", V2.LocalTag), V2.T("local-b", V2.LocalTag, paused: true)] }).ToList() };
        Assert.Equal(new RouteTarget("local-a", V2.LocalTag), Eval(paused, DataClassification.Internal, model: V2.LocalTag).Route);
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

    /// <summary>Review fix 4: no mode bypass. Unknown and disabled connections are never eligible — legacy-cloud
    /// included — and unknown_connection is always a validation error.</summary>
    [Fact]
    public void Disabled_or_unknown_connection_targets_are_ineligible_in_every_mode()
    {
        foreach (var mode in new[] { ProviderMode.Hybrid, ProviderMode.LegacyCloud, ProviderMode.LocalOnly })
        {
            var disabled = V2.Hybrid() with { Mode = mode, Connections = [V2.Ollama(enabled: false), V2.OpenAi(enabled: false)] };
            var d = Eval(disabled, DataClassification.Internal);
            Assert.Equal(PolicyEffect.Deny, d.Effect);
            Assert.Contains(d.Excluded, x => x.Reason.Contains("is disabled", StringComparison.Ordinal));

            var unknown = V2.Hybrid() with { Mode = mode, Connections = [V2.OpenAi(enabled: mode != ProviderMode.LocalOnly)] };   // ollama removed
            var u = Eval(unknown, DataClassification.Internal);
            Assert.Equal(PolicyEffect.Deny, u.Effect);
            Assert.Contains("unknown connection 'ollama'", u.Reason, StringComparison.Ordinal);
            Assert.Contains(Validate(unknown).Errors, e => e.Code == "unknown_connection");
            Assert.DoesNotContain(Validate(unknown).Warnings, w => w.Code == "unknown_connection");
        }
    }

    /// <summary>Review fix 6: Boundary.Local must be bound to a startup-approved local endpoint; cloud SDK kinds are
    /// always Cloud. A cloud host relabelled Local (to reach PII/PHI) is boundary_not_approved.</summary>
    [Fact]
    public void Local_boundary_requires_a_startup_approved_endpoint()
    {
        var relabelled = new ConnectionConfig
        {
            Id = "sneaky", DisplayName = "Cloud host labelled local", Kind = ProviderKind.OpenAiCompatible, Boundary = Boundary.Local,
            Endpoint = "https://api.openai.com/v1", Enabled = true, SecretRef = "OpenAI:ApiKey",
            Models = [new ModelConfig { ModelId = "gpt-4o-mini", Price = new PriceConfig { InputPer1M = 0m, OutputPer1M = 0m, Basis = "zero-marginal-local" } }],
        };
        var c = V2.Hybrid() with
        {
            Connections = [V2.Ollama(), V2.OpenAi(), relabelled],
            Policies = [.. V2.Hybrid().Policies.Where(p => p.Id != "sensitive"),
                V2.Policy("sensitive", [DataClassification.Pii, DataClassification.Phi], [V2.T("sneaky", "gpt-4o-mini")])],
        };
        var errors = Validate(c).Errors;
        var e = Assert.Single(errors, x => x.Code == "boundary_not_approved");
        Assert.Equal("connections[2].boundary", e.Path);
        Assert.DoesNotContain(errors, x => x.Code == "endpoint_not_allow_listed");   // the host IS allow-listed — only the label is refused

        // The same endpoint labelled Cloud is fine (and then a PII/PHI policy cannot use it).
        var honest = c with { Connections = [V2.Ollama(), V2.OpenAi(), relabelled with { Boundary = Boundary.Cloud }] };
        Assert.DoesNotContain(Validate(honest).Errors, x => x.Code == "boundary_not_approved");
        Assert.Contains(Validate(honest).Errors, x => x.Code == "sensitive_cloud_target");

        // Cloud SDK kinds are always Cloud.
        var openAiLocal = V2.Hybrid() with { Connections = [V2.Ollama(), V2.OpenAi() with { Boundary = Boundary.Local }] };
        Assert.Contains(Validate(openAiLocal).Errors, x => x.Code == "boundary_not_approved" && x.Path == "connections[1].boundary");

        // An approved local endpoint passes; the same Ollama on an unapproved host does not.
        Assert.DoesNotContain(Validate(V2.Hybrid()).Errors, x => x.Code == "boundary_not_approved");
        var otherHost = V2.Hybrid() with { Connections = [V2.Ollama() with { Endpoint = "http://10.1.2.3:11434/v1" }, V2.OpenAi()] };
        Assert.Contains(ConfigValidator.Validate(otherHost, V2.Env(true, "10.1.2.3:11434")).Errors, x => x.Code == "boundary_not_approved");
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
