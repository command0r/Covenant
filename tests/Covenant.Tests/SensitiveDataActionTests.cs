using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>ADR-0019: a workload profile states what happens when detection raises a request to PII/PHI. "reject" refuses it
/// at the classify stage — before policy, cache, rate limit, budget, web search and any provider — with the escalated class,
/// the signal label and a server-computed suggestion in evidence. "route" (default) is unchanged: PII/PHI stay local. The
/// simulator, the policy engine and execution share one rule. Each guarantee has a test that fails if it is removed.</summary>
public sealed class SensitiveDataActionTests
{
    private const string Ssn = "000-12-3456";

    /// <summary>The V2 hybrid fixture with the demo seed's ADR-0019 shape: public-demo rejects, internal-default routes.
    /// public-demo may search (search enabled and priced) and the exact cache is on, so a missing guard would show up as a
    /// search call, a cache entry or a provider call.</summary>
    private static CovenantConfig Cfg(SensitiveDataAction publicAction = SensitiveDataAction.Reject, bool grantInternal = true,
        Func<CovenantConfig, CovenantConfig>? tweak = null)
        => V2.Hybrid(tweak: c =>
        {
            var x = c with
            {
                Profiles = c.Profiles.Select(p => p.Id == "public-demo" ? p with { WebSearchAllowed = true, SensitiveDataAction = publicAction } : p).ToList(),
                Applications = grantInternal ? c.Applications
                    : c.Applications.Select(a => a.Id != "demo-app" ? a : a with { GrantedProfiles = ["public-demo"], DefaultProfile = "public-demo" }).ToList(),
                WebSearch = new WebSearchSettings { Enabled = true, PricePerCreditUsd = 0.008m },
                Settings = c.Settings with { CacheTtlSeconds = 300 },
            };
            return tweak is null ? x : tweak(x);
        });

    private sealed record Rig(V2Rig V2, StubChatClient Cloud, StubChatClient Local, StubSearchClient Search, ResponseCache Cache);

    private static Rig Build(CovenantConfig cfg)
    {
        var cloud = new StubChatClient("cloud reply");
        var local = new StubChatClient("local reply");
        var search = new StubSearchClient();
        var cache = new ResponseCache();
        var rig = V2Rig.Build(cfg, new Dictionary<string, MEAI.IChatClient>
        {
            [$"openai:{V2.CloudModel}"] = cloud,
            [$"ollama:{V2.LocalTag}"] = local,
        }, cache: cache, search: search);
        return new Rig(rig, cloud, local, search, cache);
    }

    public static TheoryData<string> PhiVariants => new()
    {
        "Patient John Smith was diagnosed with type 2 diabetes last week.",
        "patient jane roe has diabetes",
        "My name is Jane Roe. I was diagnosed with diabetes.",
        "Name: Jane Roe. Prescribed insulin for diabetes.",
        "MRN: 00123456",
        "{\"patient_name\":\"jane roe\",\"medication\":\"insulin\"}",
    };

    private static void AssertRejectedAtClassify(Rig rig, InferenceContext ctx, AuditEntry e)
    {
        Assert.True(ctx.IsDenied);
        Assert.Equal("classify", ctx.StoppedAtStage);
        Assert.Equal(TraceReasons.SensitiveDataRejected, ctx.DenialCode);
        Assert.Equal(DenialKind.Governance, ctx.DenialKind);
        Assert.Null(ctx.Policy);                                                     // policy never evaluated
        Assert.Equal(0, rig.Cloud.Calls + rig.Local.Calls + rig.Search.Calls);       // nothing external, nothing local
        Assert.Equal(PolicyEffect.Deny, e.Effect);
        Assert.True(e.Classification >= DataClassification.Pii);                     // the escalated class is kept
        Assert.Equal(DataClassification.Public, e.BaselineClassification);
        Assert.True(e.ClassificationEvaluated);                                      // a real detection, not "incomplete"
        Assert.NotNull(e.Signal);
        Assert.Equal("public-demo", e.ProfileId);
        Assert.True(e.Attempts is null || e.Attempts.Count == 0);
        Assert.Null(e.ToolCalls);
        Assert.False(ctx.ServedFromCache);
        Assert.Equal(0m, rig.V2.Ledger.Reserved("global"));
        Assert.DoesNotContain(Ssn, e.Reason, StringComparison.Ordinal);             // the reason never echoes content
    }

    // ---------------------------------------------------------------- reject: every format, nothing reaches anything

    [Theory]
    [MemberData(nameof(SensitiveInspectionTests.PiiVariants), MemberType = typeof(SensitiveInspectionTests))]
    public async Task Public_workload_rejects_every_PII_format_at_classify_with_search_requested(string text)
    {
        var rig = Build(Cfg());
        var (ctx, e) = await rig.V2.RunAsync(text, profile: "public-demo", webSearch: new WebSearchRequest(WebSearchMode.Required));
        AssertRejectedAtClassify(rig, ctx, e);
        Assert.Equal(DataClassification.Pii, e.Classification);
    }

    [Theory]
    [MemberData(nameof(PhiVariants))]
    public async Task Public_workload_rejects_PHI_and_names_the_kind(string text)
    {
        var rig = Build(Cfg());
        var (ctx, e) = await rig.V2.RunAsync(text, profile: "public-demo");
        AssertRejectedAtClassify(rig, ctx, e);
        Assert.Equal(DataClassification.Phi, e.Classification);
        Assert.StartsWith("sensitive data detected (PHI", e.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reason_is_clear_and_suggests_the_granted_internal_workload()
    {
        var rig = Build(Cfg());
        var (ctx, e) = await rig.V2.RunAsync($"Summarize this note. SSN on file: {Ssn}.", profile: "public-demo");
        AssertRejectedAtClassify(rig, ctx, e);
        Assert.Contains("sensitive data detected (PII: US SSN pattern)", e.Reason, StringComparison.Ordinal);
        Assert.Contains("workload 'public-demo' rejects detected sensitive data", e.Reason, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", e.Reason, StringComparison.Ordinal);
        Assert.EndsWith("choose workload 'internal-default'.", e.Reason, StringComparison.Ordinal);

        // No granted alternative: said plainly, nothing invented.
        var lone = Build(Cfg(grantInternal: false));
        var (_, le) = await lone.V2.RunAsync($"Summarize this note. SSN on file: {Ssn}.", profile: "public-demo");
        Assert.EndsWith("No other workload granted to this application accepts it.", le.Reason, StringComparison.Ordinal);
        Assert.Empty(SensitiveDataRules.Suggestions(lone.V2.Snapshot, lone.V2.Snapshot.FindApplication("demo-app"), "public-demo", DataClassification.Pii));
    }

    [Fact]
    public async Task A_sensitive_system_turn_is_rejected_too_and_an_explicit_cloud_pin_changes_nothing()
    {
        var rig = Build(Cfg());
        var ctx = new InferenceContext(new InferenceRequest("caller",
            [new ChatMessage(ChatRole.System, $"Context: account holder SSN {Ssn}."), new ChatMessage(ChatRole.User, "Write a greeting.")],
            "openai/" + V2.CloudModel, new AttributionTags("self-declared", "wf", "uc"), Credential: "demo-key",
            RequestId: $"req_{Guid.NewGuid():n}", RequestedProfile: "public-demo"))
        {
            Configuration = rig.V2.Snapshot,
            ConfigVersion = rig.V2.Snapshot.Version,
        };
        await rig.V2.Pipeline.ExecuteAsync(ctx, default);
        Assert.Equal("classify", ctx.StoppedAtStage);                                // before policy: the pin is never considered
        Assert.Equal(TraceReasons.SensitiveDataRejected, ctx.DenialCode);
        Assert.Equal(0, rig.Cloud.Calls + rig.Local.Calls);
    }

    [Fact]
    public async Task A_rejected_request_never_reads_or_writes_the_cache()
    {
        var rig = Build(Cfg());
        var prompt = $"Summarize this note. SSN on file: {Ssn}.";
        var (first, _) = await rig.V2.RunAsync(prompt, profile: "public-demo");
        var (second, e) = await rig.V2.RunAsync(prompt, profile: "public-demo");
        Assert.True(first.IsDenied && second.IsDenied);
        Assert.False(second.ServedFromCache);
        Assert.True(e.CacheOutcome is null or "not_reached", e.CacheOutcome);        // the cache stage was never reached
        Assert.Equal(0, rig.Cloud.Calls + rig.Local.Calls);
    }

    [Fact]
    public async Task An_innocent_public_request_is_still_served_on_the_public_route()
    {
        var rig = Build(Cfg());
        var (ctx, e) = await rig.V2.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal(DataClassification.Public, e.Classification);
        Assert.Equal("openai", ctx.ServedTarget!.AdapterKey);
        Assert.Equal(1, rig.Cloud.Calls);
    }

    // ---------------------------------------------------------------- route (Internal / default): unchanged, local only

    [Theory]
    [MemberData(nameof(PhiVariants))]
    [InlineData("Summarize this note. SSN on file: " + Ssn + ".")]
    [InlineData("card 4111 1111 1111 1111")]
    public async Task The_internal_workload_serves_detected_sensitive_data_locally(string text)
    {
        var rig = Build(Cfg());
        var (ctx, e) = await rig.V2.RunAsync(text, profile: "internal-default");
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.True(e.Classification >= DataClassification.Pii);
        Assert.Equal("ollama", ctx.ServedTarget!.AdapterKey);
        Assert.Equal(0, rig.Cloud.Calls + rig.Search.Calls);
        Assert.Equal(1, rig.Local.Calls);
    }

    [Fact]
    public async Task Default_route_keeps_the_previous_behaviour_for_a_public_workload()
    {
        // Backward compatibility: a version without the field (Route) still escalates and routes locally — never external.
        var rig = Build(Cfg(publicAction: SensitiveDataAction.Route));
        var (ctx, e) = await rig.V2.RunAsync($"Summarize. SSN {Ssn}.", profile: "public-demo");
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal(DataClassification.Pii, e.Classification);
        Assert.Equal("ollama", ctx.ServedTarget!.AdapterKey);
        Assert.Equal(0, rig.Cloud.Calls);
    }

    [Fact]
    public async Task A_failed_local_attempt_on_the_internal_workload_never_falls_back_externally()
    {
        var rig = Build(Cfg(tweak: c => c with
        {
            Policies = c.Policies.Select(p => p.Id != "sensitive" ? p : p with
            {
                Targets = [V2.T("ollama", V2.LocalTag), V2.T("openai", V2.CloudModel)],   // unvalidated: lists a cloud target
                Retry = new RetrySettings { MaxAttempts = 3, DeadlineMs = 60_000 },
            }).ToList(),
        }));
        rig.Local.Then(StubChatClient.Mode.Throw, () => Failures.Status(HttpStatusCode.ServiceUnavailable));
        var (ctx, e) = await rig.V2.RunAsync($"Summarize. SSN {Ssn}.", profile: "internal-default");
        Assert.True(ctx.IsDenied);
        Assert.Equal(0, rig.Cloud.Calls);
        Assert.Single(e.Attempts!);
    }

    // ---------------------------------------------------------------- fail-closed ordering

    [Fact]
    public async Task Incomplete_inspection_is_still_refused_as_not_evaluated_not_as_a_rejection()
    {
        var rig = Build(Cfg());
        var (ctx, e) = await rig.V2.RunAsync("Summarize this ticket \uD800 please.", profile: "public-demo");
        Assert.Equal("classify", ctx.StoppedAtStage);
        Assert.Equal(TraceReasons.InspectionIncomplete, ctx.DenialCode);
        Assert.False(e.ClassificationEvaluated);
        Assert.Equal(0, rig.Cloud.Calls + rig.Local.Calls + rig.Search.Calls);
    }

    [Fact]
    public void The_policy_engine_refuses_too_so_impact_previews_and_summaries_agree()
    {
        var snap = new RuntimeSnapshot(Cfg());
        foreach (var cls in new[] { DataClassification.Pii, DataClassification.Phi })
        {
            var o = PolicyEngineV2.Evaluate(snap, cls, "public-demo", null, []);
            Assert.Equal(PolicyEffect.Deny, o.Effect);
            Assert.StartsWith("refused: workload 'public-demo' rejects detected sensitive data", PolicyEngineV2.Summary(snap, cls, "public-demo"), StringComparison.Ordinal);
            Assert.Equal($"ollama/{V2.LocalTag}", PolicyEngineV2.Summary(snap, cls, "internal-default"));
        }
        Assert.Equal(PolicyEffect.Allow, PolicyEngineV2.Evaluate(snap, DataClassification.Public, "public-demo", null, []).Effect);

        // Impact preview of the migration (route → reject) shows exactly the PII/PHI rows of public-demo changing.
        var before = new RuntimeSnapshot(Cfg(publicAction: SensitiveDataAction.Route));
        var rows = ImpactAnalyzer.Analyze(before, snap, _ => true).Classifications.Where(r => r.Changed).ToList();
        Assert.Equal(["Pii", "Phi"], rows.Where(r => r.Profile == "public-demo").Select(r => r.Classification).OrderByDescending(x => x).ToArray());
        Assert.DoesNotContain(rows, r => r.Profile != "public-demo");
    }

    // ---------------------------------------------------------------- simulation matches execution

    [Theory]
    [InlineData("public-demo", "Summarize this note. SSN on file: " + Ssn + ".")]
    [InlineData("public-demo", "Patient John Smith was diagnosed with type 2 diabetes last week.")]
    [InlineData("internal-default", "Summarize this note. SSN on file: " + Ssn + ".")]
    [InlineData("public-demo", V2Rig.PublicPrompt)]
    public async Task Simulation_matches_execution(string profile, string prompt)
    {
        var rig = Build(Cfg());
        var sim = ConfigAdminApi.Simulate(rig.V2.Snapshot, new RegexDataClassifier(),
            new SimulateRequest { ApplicationId = "demo-app", ProfileId = profile, Prompt = prompt });
        var (ctx, e) = await rig.V2.RunAsync(prompt, profile: profile);

        Assert.Equal(e.Classification.ToString(), sim.Classification);
        Assert.Equal(e.Signal, sim.DetectedSignal);
        if (ctx.IsDenied)
        {
            Assert.Equal("refuse", sim.Decision);
            Assert.Equal(e.Reason, sim.Reason);                                       // same rule, same words
            Assert.Equal("classify", sim.RefusedAtStage);
            Assert.Equal(["internal-default"], sim.SuggestedProfiles!);
        }
        else
        {
            Assert.Equal("route", sim.Decision);
            Assert.Null(sim.RefusedAtStage);
            Assert.Equal($"{ctx.ServedTarget!.AdapterKey}/{ctx.ServedTarget.ModelId}", sim.Primary);
        }
    }

    // ---------------------------------------------------------------- validation, sanitized policy, JSON compatibility

    /// <summary>Validation without the web-search block (its server-environment checks are covered by WebSearchTests).</summary>
    private static ConfigValidationResult Validate(CovenantConfig c) => ConfigValidator.Validate(c with { WebSearch = null }, V2.Env());

    [Fact]
    public void Validation_accepts_reject_on_public_or_internal_and_refuses_it_on_a_sensitive_baseline()
    {
        Assert.True(Validate(Cfg()).IsValid);
        var internalReject = Cfg(tweak: c => c with
        {
            Profiles = c.Profiles.Select(p => p.Id == "internal-default" ? p with { SensitiveDataAction = SensitiveDataAction.Reject } : p).ToList(),
        });
        Assert.True(Validate(internalReject).IsValid);

        var phiReject = Cfg(tweak: c => c with
        {
            Profiles = c.Profiles.Select(p => p.Id == "phi-workload" ? p with { SensitiveDataAction = SensitiveDataAction.Reject } : p).ToList(),
        });
        var v = Validate(phiReject);
        var issue = Assert.Single(v.Errors, x => x.Code == "sensitive_action_requires_non_sensitive_baseline");
        Assert.Equal("profiles[2].sensitive_data_action", issue.Path);

        var undefined = Cfg(tweak: c => c with
        {
            Profiles = c.Profiles.Select(p => p.Id == "public-demo" ? p with { SensitiveDataAction = (SensitiveDataAction)99 } : p).ToList(),
        });
        Assert.Contains(Validate(undefined).Errors, x => x.Code == "invalid_enum" && x.Path == "profiles[0].sensitive_data_action");

        // Engine re-check: an unvalidated PHI-baseline "reject" never rejects (and never widens anything).
        Assert.False(SensitiveDataRules.Rejects(new RuntimeSnapshot(phiReject), "phi-workload", DataClassification.Phi));
    }

    [Fact]
    public void The_dedicated_sanitized_workload_is_unchanged()
    {
        var cfg = Cfg(tweak: c => c with
        {
            Profiles = [.. c.Profiles, new WorkloadProfileConfig { Id = "support-records", Name = "Support records", Baseline = DataClassification.Pii }],
            Applications = c.Applications.Select(a => a.Id != "demo-app" ? a : a with { GrantedProfiles = [.. a.GrantedProfiles, "support-records"] }).ToList(),
            Policies = [.. c.Policies, V2.Policy("sanitized-support", [DataClassification.Pii], [V2.T("openai", V2.CloudModel), V2.T("ollama", V2.LocalTag)],
                profiles: ["support-records"]) with { Redaction = new RedactionPolicy { Types = ["email", "phone", "us_ssn"], Tasks = ["triage_case"] } }],
        });
        Assert.True(Validate(cfg).IsValid);
        var snap = new RuntimeSnapshot(cfg);
        Assert.Equal(SensitiveDataAction.Route, snap.FindProfile("support-records")!.SensitiveDataAction);   // default
        var o = PolicyEngineV2.Evaluate(snap, DataClassification.Pii, "support-records", null, []);
        Assert.Equal("openai", o.Route!.AdapterKey);
        Assert.True(o.SanitizedPayloadRequired);
        Assert.False(SensitiveDataRules.Rejects(snap, "support-records", DataClassification.Pii));
        // The sanitized workload is not offered as a suggestion for a rejected Public request: it is PII by declaration.
        Assert.Equal(["internal-default"], SensitiveDataRules.Suggestions(snap, snap.FindApplication("demo-app"), "public-demo", DataClassification.Pii));
    }

    [Fact]
    public void Older_versions_without_the_field_read_as_route_and_reject_round_trips()
    {
        var json = JsonSerializer.Serialize(Cfg(), ConfigJsonContext.Default.CovenantConfig);
        Assert.Contains("\"sensitive_data_action\":\"reject\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sensitive_data_action\":\"route\"", json, StringComparison.Ordinal);
        Assert.Equal(Cfg().Profiles, JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CovenantConfig)!.Profiles);

        var legacy = json.Replace(",\"sensitive_data_action\":\"route\"", "", StringComparison.Ordinal)
                         .Replace(",\"sensitive_data_action\":\"reject\"", "", StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive_data_action", legacy, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize(legacy, ConfigJsonContext.Default.CovenantConfig)!;
        Assert.All(back.Profiles, p => Assert.Equal(SensitiveDataAction.Route, p.SensitiveDataAction));
    }
}

/// <summary>ADR-0019 through the real Host (demo-hybrid seed): raw /v1 callers and the console get the same gateway decision.</summary>
public sealed class SensitiveDataActionHttpTests : IDisposable
{
    private readonly HybridSeedFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient App(string profile)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo-key");
        c.DefaultRequestHeaders.Add("X-Covenant-Profile", profile);
        return c;
    }

    [Fact]
    public async Task Raw_api_public_rejects_internal_serves_locally_and_the_preview_says_the_same()
    {
        const string prompt = "Summarize this note in one sentence: Customer asked to update their mailing address. SSN on file: 000-12-3456.";
        var admin = _factory.CreateClient();

        using var pub = App("public-demo");
        using var rejected = await pub.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat(prompt));
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("rejects detected sensitive data", body, StringComparison.Ordinal);
        Assert.Contains("internal-default", body, StringComparison.Ordinal);
        Assert.DoesNotContain("000-12-3456", body, StringComparison.Ordinal);
        var re = await CorrelationTestKit.LookupAsync(admin, CorrelationTestKit.HeaderId(rejected));
        Assert.Equal("classify", re.StoppedAtStage);
        Assert.Equal(DataClassification.Pii, re.Classification);
        Assert.Equal("public-demo", re.ProfileId);
        Assert.Equal(0, _factory.Cloud.Calls + _factory.Local.Calls);

        var (status, sim, _) = await V2Admin.SendAsync(admin, HttpMethod.Post, "/admin/policies/simulate",
            JsonSerializer.Serialize(new SimulateRequest { ApplicationId = "demo-app", ProfileId = "public-demo", Prompt = prompt }, ConfigJsonContext.Default.SimulateRequest));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("refuse", sim.GetProperty("decision").GetString());
        Assert.Equal(re.Reason, sim.GetProperty("reason").GetString());

        using var internalApp = App("internal-default");
        using var served = await internalApp.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat(prompt));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        var se = await CorrelationTestKit.LookupAsync(admin, CorrelationTestKit.HeaderId(served));
        Assert.Equal(V2.LocalTag, se.ServedByModel);
        Assert.Equal(DataClassification.Pii, se.Classification);
        Assert.Equal(0, _factory.Cloud.Calls);

        using var innocent = await pub.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("Summarize this public product announcement."));
        Assert.Equal(HttpStatusCode.OK, innocent.StatusCode);
        Assert.Equal(1, _factory.Cloud.Calls);
    }

    [Fact]
    public async Task The_console_playground_gets_the_same_rejection()
    {
        using var resp = await Playground.PostAsync(_factory.CreateClient(),
            Playground.Body("demo-app", "Patient John Smith was diagnosed with type 2 diabetes last week.", profile: "public-demo"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var e = await CorrelationTestKit.LookupAsync(_factory.CreateClient(), CorrelationTestKit.HeaderId(resp));
        Assert.Equal("classify", e.StoppedAtStage);
        Assert.Equal(DataClassification.Phi, e.Classification);
        Assert.Equal("console", e.EntryPoint);
        Assert.Equal(0, _factory.Cloud.Calls + _factory.Local.Calls);
    }
}
