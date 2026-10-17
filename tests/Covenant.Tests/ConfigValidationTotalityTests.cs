using System.Text.Json;
using System.Text.Json.Nodes;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>Review fixes 1, 3 and 12 (ADR-0011 "validation is total", snapshot deep copy, wire spellings).
/// Every malformed-but-deserializable candidate is a validation error with a field path — never an exception.</summary>
public sealed class ConfigValidationTotalityTests
{
    private static ConfigValidationResult Validate(CovenantConfig c) => ConfigValidator.Validate(c, V2.Env());

    private static void AssertError(CovenantConfig c, string path, string code)
    {
        var result = Validate(c);                                    // must not throw
        Assert.False(result.IsValid);
        Assert.False(result.StructurallySound);
        Assert.Contains(result.Errors, e => e.Path == path && e.Code == code);
    }

    public static TheoryData<string> UndefinedEnums => new()
    {
        "mode", "kind", "boundary", "strategy", "period", "enforcement", "baseline", "classification",
    };

    [Theory]
    [MemberData(nameof(UndefinedEnums))]
    public void Every_out_of_range_enum_is_an_invalid_enum_error_with_its_path(string which)
    {
        var c = V2.Hybrid();
        var (bad, path) = which switch
        {
            "mode" => (c with { Mode = (ProviderMode)999 }, "mode"),
            "kind" => (c with { Connections = [c.Connections[0] with { Kind = (ProviderKind)999 }, c.Connections[1]] }, "connections[0].kind"),
            "boundary" => (c with { Connections = [c.Connections[0], c.Connections[1] with { Boundary = (Boundary)(-1) }] }, "connections[1].boundary"),
            "strategy" => (c with { Policies = [c.Policies[0] with { Strategy = (RoutingStrategy)7 }, c.Policies[1], c.Policies[2]] }, "policies[0].strategy"),
            "period" => (c with { Budgets = [c.Budgets[0] with { Period = (BudgetPeriod)42 }] }, "budgets[0].period"),
            "enforcement" => (c with { Budgets = [c.Budgets[0] with { Enforcement = (BudgetEnforcement)42 }] }, "budgets[0].enforcement"),
            "baseline" => (c with { Profiles = [c.Profiles[0] with { Baseline = (DataClassification)9 }, c.Profiles[1], c.Profiles[2]] }, "profiles[0].baseline"),
            _ => (c with { Policies = [c.Policies[0], c.Policies[1] with { Classifications = [DataClassification.Internal, (DataClassification)4] }, c.Policies[2]] },
                "policies[1].classifications[1]"),
        };
        AssertError(bad, path, "invalid_enum");
    }

    [Fact]
    public void Null_required_objects_lists_and_elements_are_required_errors_never_exceptions()
    {
        var c = V2.Hybrid();
        var pol = c.Policies[0];
        var app = c.Applications[0];
        var conn = c.Connections[0];
        var cases = new (CovenantConfig Config, string Path)[]
        {
            (c with { Settings = null! }, "settings"),
            (c with { Connections = null! }, "connections"),
            (c with { Connections = [conn, null!] }, "connections[1]"),
            (c with { Connections = [conn with { Models = null! }, c.Connections[1]] }, "connections[0].models"),
            (c with { Connections = [conn with { Models = [null!] }, c.Connections[1]] }, "connections[0].models[0]"),
            (c with { Applications = null! }, "applications"),
            (c with { Applications = [null!] }, "applications[0]"),
            (c with { Applications = [app with { GrantedProfiles = null! }] }, "applications[0].granted_profiles"),
            (c with { Applications = [app with { GrantedProfiles = ["public-demo", null!] }] }, "applications[0].granted_profiles[1]"),
            (c with { Profiles = null! }, "profiles"),
            (c with { Profiles = [c.Profiles[0], null!] }, "profiles[1]"),
            (c with { Policies = null! }, "policies"),
            (c with { Policies = [pol, null!] }, "policies[1]"),
            (c with { Policies = [pol with { Targets = null! }] }, "policies[0].targets"),
            (c with { Policies = [pol with { Targets = [pol.Targets[0], null!] }] }, "policies[0].targets[1]"),
            (c with { Policies = [pol with { Targets = [new PolicyTarget { ConnectionId = null!, ModelId = "m" }] }] }, "policies[0].targets[0].connection_id"),
            (c with { Policies = [pol with { Classifications = null! }] }, "policies[0].classifications"),
            (c with { Policies = [pol with { Profiles = null! }] }, "policies[0].profiles"),
            (c with { Policies = [pol with { Retry = null! }] }, "policies[0].retry"),
            (c with { Policies = [pol with { Retry = pol.Retry with { On = null! } }] }, "policies[0].retry.on"),
            (c with { Budgets = null! }, "budgets"),
            (c with { Budgets = [null!] }, "budgets[0]"),
            (c with { Budgets = [c.Budgets[0] with { WarnAt = null! }] }, "budgets[0].warn_at"),
            (c with { SavingsBaseline = new ModelRef(null!, "m") }, "savings_baseline.connection_id"),
        };
        foreach (var (config, path) in cases)
        {
            var result = Validate(config);
            Assert.False(result.IsValid, path);
            Assert.False(result.StructurallySound, path);
            Assert.Contains(result.Errors, e => e.Path == path && e.Code == "required");
        }
    }

    [Fact]
    public void Null_ids_and_required_strings_are_required_errors_and_never_reach_the_snapshot()
    {
        var c = V2.Hybrid();
        var weird = c with
        {
            Connections = [c.Connections[0] with { Id = null! }, c.Connections[1] with { Models = [c.Connections[1].Models[0] with { ModelId = null! }] }],
            Applications = [c.Applications[0] with { Id = null!, Team = null! }],
            Profiles = [c.Profiles[0] with { Id = null! }, c.Profiles[1], c.Profiles[2]],
            Policies = [c.Policies[0] with { Id = null!, Name = null! }, c.Policies[1], c.Policies[2]],
            Budgets = [c.Budgets[0] with { Scope = null!, Id = null! }],
        };
        var result = Validate(weird);
        Assert.False(result.StructurallySound);
        foreach (var path in new[] { "connections[0].id", "connections[1].models[0].model_id", "applications[0].id", "applications[0].team",
                     "profiles[0].id", "policies[0].id", "policies[0].name", "budgets[0].scope", "budgets[0].id" })
            Assert.Contains(result.Errors, e => e.Path == path && e.Code == "required");

        // Semantically invalid but structurally sound candidates still get an impact preview without throwing.
        var dup = c with { Connections = [.. c.Connections, c.Connections[0]], Applications = [.. c.Applications, c.Applications[0]] };
        Assert.True(Validate(dup).StructurallySound);
        Assert.False(Validate(dup).IsValid);
        Assert.NotNull(ImpactAnalyzer.Analyze(new RuntimeSnapshot(c), new RuntimeSnapshot(dup), _ => true));
    }

    [Fact]
    public void Undefined_numeric_enum_and_null_settings_deserialize_and_are_reported_not_thrown()
    {
        var json = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(ConfigRepository.Serialize(V2.Hybrid())))!.AsObject();
        json["mode"] = 999;
        json["settings"] = null;
        var c = JsonSerializer.Deserialize(json.ToJsonString(), ConfigJsonContext.Default.CovenantConfig)!;
        Assert.Equal((ProviderMode)999, c.Mode);
        var result = Validate(c);
        Assert.Contains(result.Errors, e => e.Path == "mode" && e.Code == "invalid_enum");
        Assert.Contains(result.Errors, e => e.Path == "settings" && e.Code == "required");
    }

    /// <summary>Review fix 3: the snapshot owns deep, read-only copies — mutating the caller's lists after compile
    /// changes nothing, and the snapshot's lists cannot be cast back to a mutable type.</summary>
    [Fact]
    public void Snapshot_is_a_deep_copy_unaffected_by_later_mutation_of_the_callers_lists()
    {
        var targets = new List<PolicyTarget> { V2.T("ollama", V2.LocalTag) };
        var classes = new List<DataClassification> { DataClassification.Internal };
        var models = new List<ModelConfig> { V2.Ollama().Models[0] };
        var connections = new List<ConnectionConfig> { V2.Ollama() with { Models = models }, V2.OpenAi() };
        var granted = new List<string> { "internal-default" };
        var baseCfg = V2.Hybrid();
        var cfg = baseCfg with
        {
            Connections = connections,
            Applications = [baseCfg.Applications[0] with { GrantedProfiles = granted, DefaultProfile = "internal-default" }],
            Policies = [baseCfg.Policies[0], new PolicyConfigV2 { Id = "internal", Name = "i", Enabled = true, Classifications = classes, Targets = targets }, baseCfg.Policies[2]],
        };
        var snap = new RuntimeSnapshot(cfg);
        var before = PolicyEngineV2.Summary(snap, DataClassification.Internal, null);

        // Caller mutates everything it still holds.
        targets.Clear();
        targets.Add(V2.T("openai", V2.CloudModel));
        classes.Add(DataClassification.Public);
        models.Clear();
        connections.RemoveAt(0);
        granted.Add("public-demo");

        Assert.Equal(before, PolicyEngineV2.Summary(snap, DataClassification.Internal, null));
        Assert.Equal($"ollama/{V2.LocalTag}", before);
        var internalPolicy = snap.Config.Policies.Single(p => p.Id == "internal");
        Assert.Equal(new[] { V2.T("ollama", V2.LocalTag) }, internalPolicy.Targets);
        Assert.Equal(new[] { DataClassification.Internal }, internalPolicy.Classifications);
        Assert.NotNull(snap.FindConnection("ollama"));
        Assert.NotNull(snap.FindModel("ollama", V2.LocalTag));
        Assert.Single(snap.Config.Connections.Single(x => x.Id == "ollama").Models);
        Assert.Equal(new[] { "internal-default" }, snap.FindApplication("demo-app")!.GrantedProfiles);

        // Not aliased and not castable back to a mutable list.
        Assert.NotSame(cfg.Policies, snap.Config.Policies);
        Assert.IsNotType<List<PolicyTarget>>(internalPolicy.Targets);
        Assert.IsNotType<PolicyTarget[]>(internalPolicy.Targets);
        Assert.True(((ICollection<PolicyTarget>)internalPolicy.Targets).IsReadOnly);
        Assert.Throws<NotSupportedException>(() => ((IList<PolicyTarget>)internalPolicy.Targets).Add(V2.T("openai", V2.CloudModel)));
    }

    [Fact]
    public void Snapshot_refuses_a_structurally_unsound_configuration_instead_of_defaulting()
        => Assert.Throws<ArgumentException>(() => new RuntimeSnapshot(V2.Hybrid() with { Settings = null! }));

    /// <summary>Review fix 12: the documented, stable wire spellings (docs/ui-api-contract.md v2 "Wire spellings").</summary>
    [Fact]
    public void Wire_spellings_are_the_documented_contract()
    {
        var all = V2.Hybrid() with
        {
            Connections =
            [
                V2.Ollama(),
                V2.OpenAi(),
                new ConnectionConfig
                {
                    Id = "compat", DisplayName = "c", Kind = ProviderKind.OpenAiCompatible, Boundary = Boundary.Local, Endpoint = "http://127.0.0.1:11434/v1",
                    Enabled = false, Models = [],
                },
                new ConnectionConfig { Id = "claude", DisplayName = "a", Kind = ProviderKind.Anthropic, Boundary = Boundary.Cloud, Enabled = false, Models = [] },
            ],
            Policies =
            [
                .. V2.Hybrid().Policies,
                V2.Policy("lh", [DataClassification.Public], [V2.T("ollama", V2.LocalTag)], profiles: ["phi-workload"], strategy: RoutingStrategy.LengthHeuristic),
            ],
            Budgets =
            [
                new BudgetConfigV2 { Id = "g", Scope = "global", CapUsd = 1, Period = BudgetPeriod.None, Enforcement = BudgetEnforcement.Hard },
                new BudgetConfigV2 { Id = "d", Scope = "team:a", CapUsd = 1, Period = BudgetPeriod.Daily, Enforcement = BudgetEnforcement.WarnOnly },
                new BudgetConfigV2 { Id = "m", Scope = "team:b", CapUsd = 1, Period = BudgetPeriod.Monthly },
            ],
        };
        var json = System.Text.Encoding.UTF8.GetString(ConfigRepository.Serialize(all));
        foreach (var expected in new[]
                 {
                     "\"mode\":\"hybrid\"", "\"strategy\":\"fixed\"", "\"strategy\":\"length-heuristic\"",
                     "\"period\":\"none\"", "\"period\":\"daily\"", "\"period\":\"monthly\"",
                     "\"enforcement\":\"hard\"", "\"enforcement\":\"warn-only\"",
                     "\"kind\":\"Ollama\"", "\"kind\":\"OpenAi\"", "\"kind\":\"OpenAiCompatible\"", "\"kind\":\"Anthropic\"",
                     "\"boundary\":\"Local\"", "\"boundary\":\"Cloud\"",
                     "\"baseline\":\"Public\"", "\"baseline\":\"Internal\"", "\"baseline\":\"Phi\"",
                     "\"classifications\":[\"Pii\",\"Phi\"]",
                     "\"input_per_1m\":0.15", "\"output_per_1m\":0.60",
                 })
            Assert.Contains(expected, json, StringComparison.Ordinal);
        Assert.DoesNotContain("input_per1_m", json, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_sensitive_cloud", json, StringComparison.Ordinal);

        foreach (var (mode, value) in new[] { ("local-only", ProviderMode.LocalOnly), ("hybrid", ProviderMode.Hybrid), ("legacy-cloud", ProviderMode.LegacyCloud) })
        {
            var node = JsonNode.Parse(json)!.AsObject();
            node["mode"] = mode;
            Assert.Equal(value, JsonSerializer.Deserialize(node.ToJsonString(), ConfigJsonContext.Default.CovenantConfig)!.Mode);
        }
        // Round trip is lossless for every enum.
        var back = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CovenantConfig)!;
        Assert.Equal(json, System.Text.Encoding.UTF8.GetString(ConfigRepository.Serialize(back)));
    }
}
