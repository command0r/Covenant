using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>ADR-0016: opt-in sanitized-payload egress for a bounded structured record. Default denial unchanged; the original
/// classification is kept; only the built payload (fixed instruction + placeholders + bounded attributes) reaches any provider
/// attempt; raw values never reach a provider, web search, the cache key, the fingerprint, the preview or the refusal text.</summary>
public sealed class SanitizedEgressTests
{
    private const string Ssn = "000-12-3456";
    private const string Email = "jordan.sample@example.test";
    private const string Phone = "555-010-0199";
    private const string Profile = "support-records";

    private static string Record(string ssn = Ssn, string task = "triage_case", string? extraField = null, string? extraTop = null)
        => "{\"schema\":\"covenant.record.v1\",\"task\":\"" + task + "\"" + (extraTop ?? "") + ",\"fields\":{"
           + "\"email\":\"" + Email + "\",\"phone\":\"" + Phone + "\",\"us_ssn\":\"" + ssn + "\","
           + (extraField ?? "")
           + "\"service\":\"subscription\",\"issue\":\"billing_question\",\"channel\":\"portal\",\"days_open\":2,\"amount_usd\":129.99}}";

    private static CovenantConfig Config(bool redaction = true, int cacheTtl = 0, bool search = false,
        Func<PolicyConfigV2, PolicyConfigV2>? tweakPolicy = null, DataClassification baseline = DataClassification.Pii)
        => V2.Hybrid(tweak: c =>
        {
            var sanitized = V2.Policy("sanitized-support", [DataClassification.Pii], [V2.T("openai", V2.CloudModel), V2.T("ollama", V2.LocalTag)],
                profiles: [Profile]) with
            {
                Redaction = redaction ? new RedactionPolicy { Types = ["email", "phone", "us_ssn"], Tasks = ["triage_case", "reply_outline"] } : null,
            };
            if (tweakPolicy is not null) sanitized = tweakPolicy(sanitized);
            return c with
            {
                Profiles = [.. c.Profiles, new WorkloadProfileConfig { Id = Profile, Name = "Support records", Baseline = baseline, WebSearchAllowed = search }],
                Applications = c.Applications.Select(a => a.Id != "demo-app" ? a : a with { GrantedProfiles = [.. a.GrantedProfiles, Profile] }).ToList(),
                // A Public policy that WOULD send raw text to the cloud for any profile: the sanitized workload must never reach it.
                Policies = [.. c.Policies, sanitized],
                Settings = c.Settings with { PromptPreviewChars = 500, CacheTtlSeconds = cacheTtl },
                WebSearch = search ? new WebSearchSettings { Enabled = true, PricePerCreditUsd = 0.008m } : c.WebSearch,
            };
        });

    private sealed record Rig(V2Rig V2, StubChatClient Cloud, StubChatClient Local);

    private static Rig Build(CovenantConfig cfg, bool withRedact = true, ResponseCache? cache = null, IWebSearchClient? search = null)
    {
        var cloud = new StubChatClient("cloud reply about [SSN_1]");
        var local = new StubChatClient("local reply");
        var rig = V2Rig.Build(cfg, new Dictionary<string, MEAI.IChatClient>
        {
            [$"openai:{V2.CloudModel}"] = cloud,
            [$"ollama:{V2.LocalTag}"] = local,
        }, cache: cache, search: search, withRedact: withRedact);
        return new Rig(rig, cloud, local);
    }

    private static string AuditJson(AuditEntry e) => JsonSerializer.Serialize(e, AuditJsonContext.Default.AuditEntry);

    private static void AssertNoRaw(string? text, params string[] raw)
    {
        if (text is null) return;
        foreach (var r in raw) Assert.DoesNotContain(r, text, StringComparison.Ordinal);
    }

    private static string Sent(StubChatClient c) => string.Join("\n", c.LastMessages.Select(m => m.Text));

    // ---------------------------------------------------------------- default stays fail-closed

    [Fact]
    public void Without_a_redaction_contract_a_PII_policy_never_makes_a_cloud_target_eligible()
    {
        var snap = new RuntimeSnapshot(Config(redaction: false));
        var outcome = PolicyEngineV2.Evaluate(snap, DataClassification.Pii, Profile, null, []);
        Assert.Equal(PolicyEffect.Allow, outcome.Effect);
        Assert.Equal("ollama", outcome.Route!.AdapterKey);                          // local only
        Assert.Contains(outcome.Excluded, x => x.Target.AdapterKey == "openai" && x.Reason.Contains("never routed to a cloud", StringComparison.Ordinal));
        Assert.False(outcome.SanitizedPayloadRequired);

        var with = PolicyEngineV2.Evaluate(new RuntimeSnapshot(Config()), DataClassification.Pii, Profile, null, []);
        Assert.Equal("openai", with.Route!.AdapterKey);
        Assert.True(with.SanitizedPayloadRequired);
        // Other workloads' PII keeps the generic local rule — the opt-in is profile-scoped, never global.
        var other = PolicyEngineV2.Evaluate(new RuntimeSnapshot(Config()), DataClassification.Pii, "internal-default", null, []);
        Assert.Equal("sensitive", other.PolicyId);
        Assert.False(other.SanitizedPayloadRequired);
        // PHI is never covered.
        Assert.Equal("sensitive", PolicyEngineV2.Evaluate(new RuntimeSnapshot(Config()), DataClassification.Phi, Profile, null, []).PolicyId);
    }

    [Fact]
    public async Task Prose_with_an_SSN_on_the_sanitized_workload_is_refused_and_nothing_is_sent()
    {
        var rig = Build(Config());
        const string prose = "Customer Jane Roe at 12 Elm Street, SSN " + Ssn + ", disputes a fee.";
        var (ctx, e) = await rig.V2.RunAsync(prose, profile: Profile);

        Assert.True(ctx.IsDenied);
        Assert.Equal("redact", ctx.StoppedAtStage);
        Assert.Equal(0, rig.Cloud.Calls);
        Assert.Equal(0, rig.Local.Calls);
        Assert.Equal(DataClassification.Pii, e.Classification);                      // never rewritten
        Assert.Equal("refused", e.Redaction!.Outcome);
        Assert.Equal(RedactionReasons.UnsupportedShape, e.Redaction.ReasonCode);
        Assert.Null(e.PromptSha256);                                                 // no guessable fingerprint
        Assert.Null(e.PromptPreview);                                                // preview suppressed although enabled
        AssertNoRaw(AuditJson(e), Ssn, "Jane Roe", "Elm Street");
    }

    // ---------------------------------------------------------------- applied: only the built payload leaves

    [Fact]
    public async Task A_valid_record_reaches_the_cloud_only_as_the_built_payload_and_evidence_never_holds_raw_values()
    {
        var rig = Build(Config());
        var (ctx, e) = await rig.V2.RunAsync(Record(), profile: Profile);

        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal(1, rig.Cloud.Calls);
        var sent = Sent(rig.Cloud);
        AssertNoRaw(sent, Ssn, Email, Phone, "covenant.record.v1\",");            // never the caller's JSON
        Assert.Contains("[SSN_1]", sent, StringComparison.Ordinal);
        Assert.Contains("[EMAIL_1]", sent, StringComparison.Ordinal);
        Assert.Contains("issue: billing_question", sent, StringComparison.Ordinal);
        Assert.Contains(StructuredRecordSchema.Tasks["triage_case"], sent, StringComparison.Ordinal);   // fixed instruction
        Assert.Equal(2, rig.Cloud.LastMessages.Count);                              // fixed system + built user message

        Assert.Equal(DataClassification.Pii, e.Classification);                     // original classification kept
        Assert.Equal("applied", e.Redaction!.Outcome);
        Assert.Equal("sanitized-support", e.Redaction.PolicyId);
        Assert.Equal(DataClassification.Pii, e.Redaction.OriginalClassification);
        Assert.Equal(DataClassification.Internal, e.Redaction.TransformedClassification);
        Assert.Equal(new[] { "email", "phone", "us_ssn" }, e.Redaction.Counts.Select(c => c.Type));
        Assert.All(e.Redaction.Counts, c => Assert.Equal(1, c.Count));

        var builtHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ctx.ModelMessages!.Select(m => m.Content))))).ToLowerInvariant();
        Assert.Equal(builtHash, e.PromptSha256);                                    // fingerprint of the BUILT payload
        Assert.Contains("[SSN_1]", e.PromptPreview, StringComparison.Ordinal);
        AssertNoRaw(AuditJson(e), Ssn, Email, Phone);
        Assert.Contains(e.Trace!, s => s.Stage == "redact" && s.State == TraceStates.Passed && s.ReasonCode == RedactionReasons.Applied);
    }

    [Fact]
    public async Task An_approved_fallback_also_receives_only_the_built_payload()
    {
        var rig = Build(Config());
        rig.Cloud.Then(StubChatClient.Mode.Throw, () => Failures.Status(System.Net.HttpStatusCode.ServiceUnavailable));
        var (ctx, e) = await rig.V2.RunAsync(Record(), profile: Profile);

        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal(1, rig.Cloud.Calls);
        Assert.Equal(1, rig.Local.Calls);
        AssertNoRaw(Sent(rig.Cloud), Ssn, Email, Phone);
        AssertNoRaw(Sent(rig.Local), Ssn, Email, Phone);
        Assert.Contains("[SSN_1]", Sent(rig.Local), StringComparison.Ordinal);
        Assert.Equal(2, e.Attempts!.Count);
    }

    // ---------------------------------------------------------------- defence in depth

    [Fact]
    public async Task Without_the_redact_stage_the_attempt_gate_refuses_cloud_egress_of_PII()
    {
        var rig = Build(Config(), withRedact: false);
        var (ctx, e) = await rig.V2.RunAsync(Record(), profile: Profile);

        Assert.True(ctx.IsDenied);
        Assert.Equal(0, rig.Cloud.Calls);                                           // the guarantee holds without the stage
        Assert.Equal(RedactionReasons.SensitiveEgress, ctx.DenialCode);
        Assert.Equal(PolicyEffect.Deny, e.Effect);
    }

    [Fact]
    public void The_egress_guard_rejects_messages_replaced_after_redaction()
    {
        var snap = new RuntimeSnapshot(Config());
        var ctx = V2.Ctx(Record(), profile: Profile);
        ctx.Configuration = snap;
        ctx.Classification = DataClassification.Pii;
        var (record, _) = StructuredRecord.Parse(ctx.Request.Messages, RedactionTypes.Supported, ["triage_case"]);
        var (built, _) = StructuredRecord.Build(record!);
        ctx.Redaction = new RedactionState { PolicyId = "sanitized-support", OriginalClassification = DataClassification.Pii, Outcome = "applied", Messages = built };
        ctx.ModelMessages = built;
        var cloud = new RouteTarget("openai", V2.CloudModel);
        Assert.Null(EgressGuard.Refusal(ctx, cloud));
        ctx.ModelMessages = ctx.Request.Messages;                                   // e.g. a later stage re-grounded on the original
        Assert.NotNull(EgressGuard.Refusal(ctx, cloud));
        ctx.Classification = DataClassification.Phi;
        ctx.ModelMessages = built;
        Assert.NotNull(EgressGuard.Refusal(ctx, cloud));                            // PHI never, even with a payload
        Assert.Null(EgressGuard.Refusal(ctx, new RouteTarget("ollama", V2.LocalTag)));
    }

    // ---------------------------------------------------------------- refusals never echo caller data

    [Fact]
    public async Task An_identifier_hidden_in_an_unknown_property_name_is_refused_without_echoing_it()
    {
        var rig = Build(Config());
        const string secretName = "jane.doe@example.test 000-55-6666";
        var (ctx, e) = await rig.V2.RunAsync(Record(extraField: "\"" + secretName + "\":\"x\","), profile: Profile);

        Assert.True(ctx.IsDenied);
        Assert.Equal(RedactionReasons.UnknownField, e.Redaction!.ReasonCode);
        Assert.Equal(0, rig.Cloud.Calls + rig.Local.Calls);
        AssertNoRaw(ctx.DenialReason, "jane.doe", "000-55-6666", Ssn, Email);
        AssertNoRaw(AuditJson(e), "jane.doe", "000-55-6666", Ssn, Email, Phone);
        Assert.Null(e.PromptPreview);
        Assert.Null(e.PromptSha256);

        var (ctx2, e2) = await rig.V2.RunAsync(Record(extraTop: ",\"000-77-8888 Jane Roe\":1"), profile: Profile);
        Assert.Equal(RedactionReasons.UnknownField, e2.Redaction!.ReasonCode);
        AssertNoRaw(ctx2.DenialReason, "000-77-8888", "Jane Roe");
        AssertNoRaw(AuditJson(e2), "000-77-8888", "Jane Roe", Ssn);

        var (ctx3, e3) = await rig.V2.RunAsync(Record(extraField: "\"us_ssn\":\"000-12-9999\","), profile: Profile);   // duplicate field
        Assert.Equal(RedactionReasons.UnsupportedShape, e3.Redaction!.ReasonCode);
        AssertNoRaw(ctx3.DenialReason, "000-12-9999", Ssn);
    }

    [Theory]
    [InlineData("{\"schema\":\"covenant.record.v1\",\"task\":\"triage_case\",\"fields\":{\"us_ssn\":\"000-12-3456\",\"notes\":\"call Jane\"}}", RedactionReasons.UnknownField)]
    [InlineData("{\"schema\":\"covenant.record.v1\",\"task\":\"write_poem\",\"fields\":{\"us_ssn\":\"000-12-3456\"}}", RedactionReasons.NotAllowed)]
    [InlineData("{\"schema\":\"covenant.record.v1\",\"task\":\"compliance_checklist\",\"fields\":{\"us_ssn\":\"000-12-3456\"}}", RedactionReasons.NotAllowed)]   // not allowed by this policy
    [InlineData("{\"schema\":\"covenant.record.v1\",\"task\":\"triage_case\",\"fields\":{\"us_ssn\":\"000-12-3456\",\"service\":\"yacht\"}}", RedactionReasons.InvalidValue)]
    [InlineData("{\"schema\":\"covenant.record.v1\",\"task\":\"triage_case\",\"fields\":{\"us_ssn\":\"000-12-3456\",\"amount_usd\":1.234}}", RedactionReasons.InvalidValue)]
    [InlineData("{\"schema\":\"covenant.record.v1\",\"task\":\"triage_case\",\"fields\":{\"us_ssn\":\"000-12-3456\",\"phone\":\"call me 555-010-0199\"}}", RedactionReasons.InvalidValue)]
    [InlineData("{\"schema\":\"covenant.record.v1\",\"task\":\"triage_case\",\"fields\":{\"us_ssn\":\"000-12-3456\",\"service\":{\"x\":1}}}", RedactionReasons.InvalidValue)]
    [InlineData("Here is the record: {\"schema\":\"covenant.record.v1\",\"task\":\"triage_case\",\"fields\":{\"us_ssn\":\"000-12-3456\"}}", RedactionReasons.UnsupportedShape)]
    [InlineData("{\"schema\":\"covenant.record.v2\",\"task\":\"triage_case\",\"fields\":{\"us_ssn\":\"000-12-3456\"}}", RedactionReasons.UnsupportedShape)]
    public async Task Unsupported_records_fail_closed_with_a_specific_reason(string prompt, string code)
    {
        var rig = Build(Config());
        var (ctx, e) = await rig.V2.RunAsync(prompt, profile: Profile);
        Assert.True(ctx.IsDenied);
        Assert.Equal(code, e.Redaction!.ReasonCode);
        Assert.Equal(0, rig.Cloud.Calls + rig.Local.Calls);
        AssertNoRaw(ctx.DenialReason, Ssn, "Jane");
        AssertNoRaw(AuditJson(e), Ssn, "Jane");
    }

    [Fact]
    public void More_than_one_message_or_a_system_turn_is_an_unsupported_shape()
    {
        var types = RedactionTypes.Supported;
        string[] tasks = ["triage_case"];
        var two = StructuredRecord.Parse([new ChatMessage(ChatRole.System, "be helpful"), new ChatMessage(ChatRole.User, Record())], types, tasks);
        Assert.Equal(RedactionReasons.UnsupportedShape, two.Refusal!.Code);
        var assistant = StructuredRecord.Parse([new ChatMessage(ChatRole.Assistant, Record())], types, tasks);
        Assert.Equal(RedactionReasons.UnsupportedShape, assistant.Refusal!.Code);
        var ok = StructuredRecord.Parse([new ChatMessage(ChatRole.User, Record())], types, tasks);
        Assert.Null(ok.Refusal);
        // An identifier type the policy does not allow is refused (never silently passed through).
        var noPhone = StructuredRecord.Parse([new ChatMessage(ChatRole.User, Record())], ["email", "us_ssn"], tasks);
        Assert.Equal(RedactionReasons.NotAllowed, noPhone.Refusal!.Code);
    }

    [Fact]
    public async Task PHI_never_takes_the_sanitized_route()
    {
        var rig = Build(Config());
        // An unknown field NAME carrying a PHI keyword: the detector escalates to PHI, the sanitized PII policy cannot match.
        var (ctx, e) = await rig.V2.RunAsync(Record(extraField: "\"diagnosis\":\"x\","), profile: Profile);
        Assert.Equal(DataClassification.Phi, e.Classification);
        Assert.Equal(0, rig.Cloud.Calls);
        Assert.Null(e.Redaction);                                                   // the generic local PHI rule decided
        Assert.Equal("sensitive", e.PolicyId);
        Assert.Null(e.PromptPreview);                                               // sensitive: no preview at all
        Assert.True(ctx.ServedTarget is null || ctx.ServedTarget.AdapterKey == "ollama");
    }

    // ---------------------------------------------------------------- web search and cache

    [Fact]
    public async Task Web_search_never_receives_a_sanitized_request()
    {
        var search = new StubSearchClient();
        var rig = Build(Config(search: true), search: search);
        var (ctx, e) = await rig.V2.RunAsync(Record(), profile: Profile, webSearch: new WebSearchRequest(WebSearchMode.Required));
        Assert.True(ctx.IsDenied);
        Assert.Equal(0, search.Calls);
        Assert.Null(search.LastQuery);
        Assert.Equal(TraceReasons.SearchSensitive, ctx.DenialCode);
        Assert.Equal(0, rig.Cloud.Calls);
        AssertNoRaw(AuditJson(e), Ssn, Email, Phone);
    }

    [Fact]
    public async Task The_cache_keys_on_the_built_payload_and_the_redaction_identity_never_on_raw_values()
    {
        var cache = new ResponseCache();
        var rig = Build(Config(cacheTtl: 300), cache: cache);
        var (_, first) = await rig.V2.RunAsync(Record(ssn: "000-12-3456"), profile: Profile);
        var (_, second) = await rig.V2.RunAsync(Record(ssn: "000-98-7654"), profile: Profile);
        Assert.Equal("miss", first.CacheOutcome);
        Assert.True(second.CacheHit);                                               // raw values are not part of the key
        Assert.Equal(1, rig.Cloud.Calls);

        // The same built payload under another policy identity is a different partition.
        var other = Build(Config(cacheTtl: 300, tweakPolicy: p => p with { Redaction = p.Redaction! with { Tasks = ["triage_case"] } }), cache: cache);
        var (_, third) = await other.V2.RunAsync(Record(), profile: Profile);
        Assert.False(third.CacheHit);
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public void Validation_accepts_only_a_profile_scoped_PII_only_contract_with_known_types_and_tasks()
    {
        Assert.True(ConfigValidator.Validate(Config(), V2.Env()).IsValid, string.Join("; ", ConfigValidator.Validate(Config(), V2.Env()).Errors));

        static IEnumerable<string> Codes(Func<PolicyConfigV2, PolicyConfigV2> t)
            => ConfigValidator.Validate(Config(tweakPolicy: t), V2.Env()).Errors.Select(x => x.Code);

        Assert.Contains("redaction_requires_pii_only", Codes(p => p with { Classifications = [DataClassification.Pii, DataClassification.Phi] }));
        Assert.Contains("redaction_requires_profile", Codes(p => p with { Profiles = [] }));
        Assert.Contains("redaction_missing_required_type", Codes(p => p with { Redaction = p.Redaction! with { Types = ["email"] } }));
        Assert.Contains("redaction_invalid_types", Codes(p => p with { Redaction = p.Redaction! with { Types = ["us_ssn", "name"] } }));
        Assert.Contains("redaction_invalid_tasks", Codes(p => p with { Redaction = p.Redaction! with { Tasks = ["free_text"] } }));
        Assert.Contains("redaction_invalid_tasks", Codes(p => p with { Redaction = p.Redaction! with { Tasks = [] } }));
        // Without the contract the old rule stands.
        Assert.Contains("sensitive_cloud_target", Codes(p => p with { Redaction = null }));
    }

    [Fact]
    public void The_console_preview_uses_the_same_decision_and_returns_placeholders_only()
    {
        var snap = new RuntimeSnapshot(Config());
        var preview = ConfigAdminApi.Simulate(snap, new RegexDataClassifier(), new SimulateRequest { ApplicationId = "demo-app", ProfileId = Profile, Prompt = Record() });
        Assert.Equal("route", preview.Decision);
        Assert.Equal("applied", preview.Redaction!.Outcome);
        var text = string.Join("\n", preview.Redaction.Messages.Select(m => m.Content));
        AssertNoRaw(text, Ssn, Email, Phone);
        Assert.Contains("[PHONE_1]", text, StringComparison.Ordinal);
        Assert.Contains(preview.Redaction.Schema.Fields, f => f.Name == "us_ssn" && f.Kind == "identifier");

        var refused = ConfigAdminApi.Simulate(snap, new RegexDataClassifier(), new SimulateRequest { ApplicationId = "demo-app", ProfileId = Profile, Prompt = "SSN " + Ssn });
        Assert.Equal("refused", refused.Redaction!.Outcome);
        Assert.Empty(refused.Redaction.Messages);
        AssertNoRaw(refused.Redaction.Reason, Ssn);
    }

    // ---------------------------------------------------------------- dedicated workload: no bypass without an SSN

    private static string EmailOnlyRecord(string? extraField = null)
        => "{\"schema\":\"covenant.record.v1\",\"task\":\"reply_outline\",\"fields\":{\"email\":\"" + Email + "\","
           + (extraField ?? "") + "\"service\":\"workspace\",\"issue\":\"login_failed\"}}";

    [Fact]
    public async Task An_email_only_record_still_takes_the_structured_contract_because_the_workload_baseline_is_PII()
    {
        var rig = Build(Config());
        var (ctx, e) = await rig.V2.RunAsync(EmailOnlyRecord(), profile: Profile);
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        // ADR-0018: the labelled e-mail field is now detected too — either way the classification is PII (the workload
        // baseline alone already makes it PII), and the signal names the kind of field, never the value.
        Assert.Equal(InspectionSignals.EmailField, e.Signal);
        Assert.Equal(DataClassification.Pii, e.Classification);
        Assert.Equal("applied", e.Redaction!.Outcome);
        AssertNoRaw(Sent(rig.Cloud), Email);
        Assert.Contains("[EMAIL_1]", Sent(rig.Cloud), StringComparison.Ordinal);
        AssertNoRaw(AuditJson(e), Email);
    }

    [Theory]
    [InlineData("Please reset access for " + Email + ", thanks.")]                    // prose, e-mail only, no SSN
    [InlineData("{\"schema\":\"covenant.record.v1\",\"task\":\"reply_outline\",\"fields\":{\"email\":\"" + Email + "\",\"customer_name\":\"Jane Roe\"}}")]
    public async Task Without_an_SSN_unsupported_input_on_the_dedicated_workload_is_refused_and_nothing_is_sent(string prompt)
    {
        var rig = Build(Config());
        var (ctx, e) = await rig.V2.RunAsync(prompt, profile: Profile);
        Assert.True(ctx.IsDenied);
        Assert.Equal("redact", ctx.StoppedAtStage);
        Assert.Equal(0, rig.Cloud.Calls + rig.Local.Calls);
        Assert.True(e.Signal is null || e.Signal == InspectionSignals.EmailField);   // prose e-mail: no label; JSON field: labelled
        Assert.Equal(DataClassification.Pii, e.Classification);
        Assert.Equal("refused", e.Redaction!.Outcome);
        AssertNoRaw(ctx.DenialReason, Email, "Jane Roe", "customer_name");
        AssertNoRaw(AuditJson(e), Email, "Jane Roe");
        Assert.Null(e.PromptPreview);
        Assert.Null(e.PromptSha256);
    }

    [Fact]
    public async Task An_unvalidated_non_PII_baseline_never_falls_through_to_an_ordinary_external_route()
    {
        // Validation refuses this configuration; the engine still guards it (runtime defence).
        var cfg = Config(baseline: DataClassification.Public);
        Assert.Contains("redaction_profile_baseline", ConfigValidator.Validate(cfg, V2.Env()).Errors.Select(x => x.Code));
        var rig = Build(cfg);
        var (ctx, e) = await rig.V2.RunAsync("Please reset access for " + Email + ".", profile: Profile);
        Assert.True(ctx.IsDenied);
        Assert.Equal("policy", ctx.StoppedAtStage);
        Assert.Equal(0, rig.Cloud.Calls + rig.Local.Calls);                         // the generic Public→cloud policy was NOT used
        AssertNoRaw(AuditJson(e), Email);
    }
}
