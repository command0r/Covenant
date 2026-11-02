using System.Text.RegularExpressions;
using Covenant.Core;
using Covenant.Governance;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>ADR-0018 local sensitive-data inspection: obfuscated/masked/partial SSNs, labelled identifier fields, payment
/// cards, PHI narratives about identifiable people, benign controls, every message role, bounds that fail closed — and,
/// end to end, zero external model or search calls for sensitive variants, no external fallback after a local failure,
/// same-input local-allow / cloud-pin-refuse, and cache isolation by classification.</summary>
public sealed class SensitiveInspectionTests
{
    private static readonly LocalSensitiveDataInspector Inspector = new();

    private static ClassificationResult Inspect(params ChatMessage[] messages)
        => Inspector.Classify(new InferenceRequest("p", messages, null, AttributionTags.Unattributed));

    private static ClassificationResult InspectUser(string text) => Inspect(new ChatMessage(ChatRole.User, text));

    // ---------------------------------------------------------------- PII variants

    public static TheoryData<string> PiiVariants => new()
    {
        "Reporter SSN 000-12-3456 (synthetic).",
        "ssn 000 12 3456",
        "SSN 000.12.3456",
        "SSN ０００-１２-３４５６",                         // full-width digits (NFKC)
        "SSN 000​-12​-3456",                   // zero-width spaces inside
        "SSN 000‑12‑3456",                     // non-breaking hyphens
        "SSN ٠٠٠-١٢-٣٤٥٦",                               // Arabic-Indic digits
        "my social security number is 000123456",        // unseparated, with context
        "the customer's SSN ending 3456 was verified",   // partial, with context
        "social security: last four 3456",
        "xxx-xx-3456",                                    // masked shape alone
        "***-**-3456",
        "ssn: 000 123 456",                               // labelled field, unusual grouping
        "email: jane.roe@example.test",
        "\"email\": \"jane.roe@example.test\"",          // structured (JSON) field
        "phone: +1 (555) 010-0199",
        "DOB: 1980-02-01",
        "passport no: X1234567",
        "card 4111 1111 1111 1111",                       // Luhn-valid test number, grouped
        "credit card 4111111111111111",                   // ungrouped with context
        "Please summarize this. 000123456",               // a bare compact identifier appended to any prompt
        "Order 123456789 shipped today.",                 // conservatively a possible identifier (not asserted to be an SSN)
        "Last four of SSN: 3456",
        "{\"us_ssn\": \"000123456\"}",                // structured keys
        "{\"social_security_number\":\"000-12-3456\"}",
        "{\"date_of_birth\":\"1980-02-01\"}",
        "{\"note\":\"SSN \\u0030\\u0030\\u0030-12-3456\"}",                       // JSON \u escapes
        "{\"outer\":\"{\\\"inner\\\":\\\"\\\\u0030\\\\u0030\\\\u0030-12-3456\\\"}\"}",   // nested (double-encoded)
    };

    [Theory]
    [MemberData(nameof(PiiVariants))]
    public void Sensitive_identifier_variants_are_PII_and_the_signal_never_echoes_the_value(string text)
    {
        var r = InspectUser(text);
        Assert.Equal(DataClassification.Pii, r.Classification);
        Assert.NotNull(r.Signal);
        Assert.DoesNotMatch(new Regex(@"\d{3}"), r.Signal!);     // a fixed label, never digits of the input
        Assert.DoesNotContain("example.test", r.Signal, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- PHI narratives

    [Theory]
    [InlineData("MRN: 00123456")]
    [InlineData("Patient ID 4711, diagnosis pending.")]
    [InlineData("Patient John Smith was diagnosed with type 2 diabetes last week.")]
    [InlineData("Mrs. Doe was prescribed 20 mg fluoxetine.")]
    [InlineData("DOB 1980-02-01; diagnosis code E11.9")]
    [InlineData("email: jane.roe@example.test — she is undergoing chemotherapy")]
    [InlineData("patient jane roe has diabetes")]
    [InlineData("My name is Jane Roe. I was diagnosed with diabetes.")]
    [InlineData("Name: Jane Roe. Prescribed insulin for diabetes.")]
    [InlineData("{\"patient_id\":\"A1234\",\"diagnosis\":\"E11.9\"}")]
    [InlineData("{\"patient_name\":\"jane roe\",\"medication\":\"insulin\"}")]
    public void Health_information_about_an_identifiable_person_is_PHI(string text)
    {
        var r = InspectUser(text);
        Assert.Equal(DataClassification.Phi, r.Classification);
        Assert.DoesNotMatch(new Regex(@"\d{3}"), r.Signal!);
    }

    // ---------------------------------------------------------------- benign controls (false positives)

    [Theory]
    [InlineData("What are the common symptoms of type 2 diabetes?")]
    [InlineData("The usual adult dose is 500 mg every 6 hours.")]
    [InlineData("Order #12345 shipped on 2026-11-01; 4 items, 3456 grams.")]   // ordinary short quantities stay benign
    [InlineData("Run id a123456789b and hash 0f1e2d3c4b5a69788796a5b4c3d2e1f0.")]  // digits inside hex ids are not identifiers
    [InlineData("file name: report final.pdf mentions diabetes statistics")]
    [InlineData("Version 4.8.1 was released; the meeting room is 3456.")]
    [InlineData("Ticket ending 3456 was closed.")]
    [InlineData("Our support phone line opens at 9am.")]
    [InlineData("Call 555-010-0199 for the public press office.")]
    [InlineData("Write a C# extension method Chunk<T>(this IEnumerable<T> source, int size).")]
    [InlineData("Summarize: hospitals reported fewer flu cases this winter.")]
    public void Generic_text_numbers_and_health_questions_are_not_escalated(string text)
    {
        var r = InspectUser(text);
        Assert.Equal(DataClassification.Internal, r.Classification);   // "no escalation" — not a claim of public safety
        Assert.Null(r.Signal);
    }

    // ---------------------------------------------------------------- every role, bounds, fail closed

    [Theory]
    [InlineData(ChatRole.System)]
    [InlineData(ChatRole.Assistant)]
    [InlineData(ChatRole.Tool)]
    public void Every_message_role_is_inspected(ChatRole role)
    {
        var r = Inspect(new ChatMessage(ChatRole.User, "Please summarize the ticket."), new ChatMessage(role, "Reporter SSN 000-12-3456."));
        Assert.Equal(DataClassification.Pii, r.Classification);
    }

    [Fact]
    public void Identity_and_health_detail_in_different_messages_is_still_PHI()
    {
        var r = Inspect(new ChatMessage(ChatRole.User, "Patient: Jane Roe, DOB 1980-02-01."),
                        new ChatMessage(ChatRole.Assistant, "Noted."),
                        new ChatMessage(ChatRole.User, "She was diagnosed with diabetes last year — draft a care note."));
        Assert.Equal(DataClassification.Phi, r.Classification);
        Assert.Equal(InspectionSignals.HealthNarrative, r.Signal);
        // Identity without any health detail anywhere stays PII (no PHI claim).
        var pii = Inspect(new ChatMessage(ChatRole.User, "Patient: Jane Roe, DOB 1980-02-01."), new ChatMessage(ChatRole.User, "Book a meeting."));
        Assert.Equal(DataClassification.Pii, pii.Classification);
    }

    [Fact]
    public void Oversize_input_is_incomplete_not_a_PHI_claim()
    {
        var r = InspectUser(new string('a', LocalSensitiveDataInspector.MaxInspectedChars + 1));
        Assert.True(r.Incomplete);
        Assert.Equal(InspectionSignals.Incomplete, r.Signal);
    }

    [Fact]
    public void The_aggregate_time_budget_and_match_cap_fail_closed_deterministically()
    {
        var noTime = new LocalSensitiveDataInspector(new InspectionLimits { Budget = TimeSpan.Zero });
        Assert.True(noTime.Classify(new InferenceRequest("p", [new ChatMessage(ChatRole.User, "hello")], null, AttributionTags.Unattributed)).Incomplete);

        var capped = new LocalSensitiveDataInspector(new InspectionLimits { MaxMatchesPerPattern = 3 });
        var many = capped.Classify(new InferenceRequest("p", [new ChatMessage(ChatRole.User, "ssn ssn ssn ssn 1234")], null, AttributionTags.Unattributed));
        Assert.True(many.Incomplete);
        var few = capped.Classify(new InferenceRequest("p", [new ChatMessage(ChatRole.User, "ssn ssn ending 1234")], null, AttributionTags.Unattributed));
        Assert.False(few.Incomplete);
        Assert.Equal(DataClassification.Pii, few.Classification);
    }

    [Fact]
    public void Bounded_escape_decoding_handles_nested_and_malformed_input()
    {
        Assert.Equal("000", LocalSensitiveDataInspector.DecodeEscapes("\\u0030\\u0030\\u0030"));
        Assert.Equal("\\uZZZZ end", LocalSensitiveDataInspector.DecodeEscapes("\\uZZZZ end"));   // malformed kept as-is
        Assert.Equal("\\", LocalSensitiveDataInspector.DecodeEscapes("\\", maxPasses: 0));
    }

    [Fact]
    public void Normalization_and_luhn_are_exact()
    {
        Assert.Equal("000-12-3456", LocalSensitiveDataInspector.Normalize("０００‑１２​-３４５６"));
        Assert.True(LocalSensitiveDataInspector.Luhn("4111 1111 1111 1111"));
        Assert.False(LocalSensitiveDataInspector.Luhn("4111 1111 1111 1112"));
        Assert.False(LocalSensitiveDataInspector.Luhn("4111"));   // too short to be a card
        Assert.Contains("not comprehensive DLP", InspectionSignals.Coverage, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- end to end: no external call, ever

    private sealed record Rig(V2Rig V2, StubChatClient Cloud, StubChatClient Local, StubSearchClient Search);

    private static Rig Build(Func<CovenantConfig, CovenantConfig>? tweak = null, ResponseCache? cache = null)
    {
        var cfg = V2.Hybrid(tweak: c =>
        {
            var x = c with
            {
                Profiles = c.Profiles.Select(p => p.Id == "public-demo" ? p with { WebSearchAllowed = true } : p).ToList(),
                WebSearch = new WebSearchSettings { Enabled = true, PricePerCreditUsd = 0.008m },
            };
            return tweak is null ? x : tweak(x);
        });
        var cloud = new StubChatClient("cloud reply");
        var local = new StubChatClient("local reply");
        var search = new StubSearchClient();
        var rig = V2Rig.Build(cfg, new Dictionary<string, MEAI.IChatClient>
        {
            [$"openai:{V2.CloudModel}"] = cloud,
            [$"ollama:{V2.LocalTag}"] = local,
        }, cache: cache, search: search);
        return new Rig(rig, cloud, local, search);
    }

    public static TheoryData<string> PipelineVariants => new()
    {
        "Summarize this ticket. Reporter SSN ０００-１２-３４５６.",
        "Summarize this ticket. SSN 000​-12​-3456.",
        "Summarize: the customer's SSN ending 3456 was verified.",
        "Summarize: xxx-xx-3456 was masked in the export.",
        "Summarize: Patient John Smith was diagnosed with diabetes.",
    };

    [Theory]
    [MemberData(nameof(PipelineVariants))]
    public async Task Sensitive_variants_on_a_Public_workload_are_served_locally_with_zero_external_calls(string prompt)
    {
        var rig = Build();
        var (ctx, e) = await rig.V2.RunAsync(prompt, profile: "public-demo");
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal(0, rig.Cloud.Calls);
        Assert.Equal(1, rig.Local.Calls);
        Assert.True(e.Classification >= DataClassification.Pii);       // never silently downgraded
        Assert.NotNull(e.Signal);
        Assert.Equal("ollama", ctx.ServedTarget!.AdapterKey);
    }

    [Theory]
    [MemberData(nameof(PipelineVariants))]
    public async Task Web_search_is_never_called_for_sensitive_variants(string prompt)
    {
        var rig = Build();
        var (ctx, _) = await rig.V2.RunAsync(prompt, profile: "public-demo", webSearch: new WebSearchRequest(WebSearchMode.Required));
        Assert.True(ctx.IsDenied);
        Assert.Equal(0, rig.Search.Calls);
        Assert.Equal(0, rig.Cloud.Calls);
    }

    [Fact]
    public async Task Same_input_is_served_locally_by_policy_and_refused_when_a_cloud_model_is_pinned()
    {
        const string prompt = "Summarize this public support ticket in one sentence: \"Printer jammed. Reporter SSN 000-12-3456 (synthetic).\"";
        var rig = Build();
        var (local, _) = await rig.V2.RunAsync(prompt, profile: "public-demo");
        Assert.False(local.IsDenied, local.DenialReason);
        Assert.Equal("ollama", local.ServedTarget!.AdapterKey);

        var (pinned, e) = await rig.V2.RunAsync(prompt, profile: "public-demo", model: "openai/" + V2.CloudModel);
        Assert.True(pinned.IsDenied);
        Assert.Equal("policy", pinned.StoppedAtStage);                 // blocked by policy, not a provider failure
        Assert.Equal(DataClassification.Pii, e.Classification);
        Assert.Equal(0, rig.Cloud.Calls);
    }

    [Fact]
    public async Task An_Internal_workload_with_an_approved_local_PII_route_serves_sensitive_input()
    {
        var rig = Build();
        var (ctx, e) = await rig.V2.RunAsync("Draft a reply. Customer SSN 000-12-3456.", profile: "internal-default");
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal(DataClassification.Pii, e.Classification);
        Assert.Equal(0, rig.Cloud.Calls);
        Assert.Equal(1, rig.Local.Calls);
    }

    [Fact]
    public async Task A_failed_local_attempt_never_falls_back_to_an_external_target()
    {
        // Unvalidated configuration whose sensitive policy also LISTS the cloud model: the engine still excludes it.
        var rig = Build(c => c with
        {
            Policies = c.Policies.Select(p => p.Id != "sensitive" ? p : p with
            {
                Targets = [V2.T("ollama", V2.LocalTag), V2.T("openai", V2.CloudModel)],
                Retry = new RetrySettings { MaxAttempts = 3, DeadlineMs = 60_000 },
            }).ToList(),
        });
        rig.Local.Then(StubChatClient.Mode.Throw, () => Failures.Status(System.Net.HttpStatusCode.ServiceUnavailable));
        var (ctx, e) = await rig.V2.RunAsync("Summarize. SSN 000 12 3456.", profile: "public-demo");
        Assert.True(ctx.IsDenied);
        Assert.Equal(1, rig.Local.Calls);
        Assert.Equal(0, rig.Cloud.Calls);
        Assert.Single(e.Attempts!);
    }

    [Fact]
    public async Task A_cached_Public_answer_is_never_reused_once_a_sensitive_turn_is_present()
    {
        var cache = new ResponseCache();
        var rig = Build(c => c with { Settings = c.Settings with { CacheTtlSeconds = 300 } }, cache);
        const string prompt = "In one sentence, what does an exact-match cache do?";
        var (_, first) = await rig.V2.RunAsync(prompt, profile: "public-demo");
        Assert.Equal(DataClassification.Public, first.Classification);
        Assert.Equal(1, rig.Cloud.Calls);

        // Same user text, plus a system turn carrying an identifier: classification rises to PII, so neither the cache
        // entry (keyed by classification and messages) nor the cloud route may be used.
        var ctx = new InferenceContext(new InferenceRequest("caller",
            [new ChatMessage(ChatRole.System, "Context: account holder SSN 000-12-3456."), new ChatMessage(ChatRole.User, prompt)],
            null, new AttributionTags("self-declared", "wf", "uc"), Credential: "demo-key", RequestId: $"req_{Guid.NewGuid():n}", RequestedProfile: "public-demo"))
        {
            Configuration = rig.V2.Snapshot,
            ConfigVersion = rig.V2.Snapshot.Version,
        };
        await rig.V2.Pipeline.ExecuteAsync(ctx, default);
        Assert.False(ctx.ServedFromCache);
        Assert.Equal(DataClassification.Pii, ctx.Classification);
        Assert.Equal(1, rig.Cloud.Calls);
        Assert.Equal("ollama", ctx.ServedTarget!.AdapterKey);

        // And the key itself separates classifications for identical messages.
        var snap = rig.V2.Snapshot;
        var a = V2.Ctx(prompt); a.Configuration = snap;
        var b = V2.Ctx(prompt); b.Configuration = snap;
        var route = new RouteTarget("ollama", V2.LocalTag);
        a.Policy = PolicyOutcome.Allow(route) with { Candidates = [route] };
        b.Policy = a.Policy;
        a.Classification = DataClassification.Public;
        b.Classification = DataClassification.Pii;
        Assert.NotEqual(CacheStage.KeyFor(a, route), CacheStage.KeyFor(b, route));
    }

    // ---------------------------------------------------------------- incomplete inspection: refused everywhere, nothing sent

    private sealed class IncompleteClassifier : IDataClassifier
    {
        public ClassificationResult Classify(InferenceRequest request) => new(DataClassification.Phi, InspectionSignals.Incomplete, Incomplete: true);
    }

    [Fact]
    public async Task An_incomplete_inspection_is_refused_at_classify_and_nothing_reaches_any_provider_or_search()
    {
        var rig = Build();
        var huge = "Summarize: " + new string('a', LocalSensitiveDataInspector.MaxInspectedChars);
        var (ctx, e) = await rig.V2.RunAsync(huge, profile: "public-demo", webSearch: new WebSearchRequest(WebSearchMode.Required));
        Assert.True(ctx.IsDenied);
        Assert.Equal("classify", ctx.StoppedAtStage);
        Assert.Equal(TraceReasons.InspectionIncomplete, ctx.DenialCode);
        Assert.Equal(0, rig.Local.Calls + rig.Cloud.Calls + rig.Search.Calls);   // not "PHI → local": nothing at all
        Assert.False(e.ClassificationEvaluated);                                // never presented as a detection
        Assert.Equal(InspectionSignals.Incomplete, e.Signal);
    }

    [Fact]
    public async Task An_incomplete_search_query_inspection_refuses_the_search()
    {
        var search = new StubSearchClient();
        var cfg = V2.Hybrid(tweak: c => c with
        {
            Profiles = c.Profiles.Select(p => p.Id == "public-demo" ? p with { WebSearchAllowed = true } : p).ToList(),
            WebSearch = new WebSearchSettings { Enabled = true, PricePerCreditUsd = 0.008m },
        });
        var cloud = new StubChatClient("cloud reply");
        var rig = V2Rig.Build(cfg, new Dictionary<string, MEAI.IChatClient> { [$"openai:{V2.CloudModel}"] = cloud, [$"ollama:{V2.LocalTag}"] = new StubChatClient() },
            search: search, searchClassifier: new IncompleteClassifier());
        var (ctx, _) = await rig.RunAsync("What is the latest .NET release?", profile: "public-demo", webSearch: new WebSearchRequest(WebSearchMode.Required));
        Assert.True(ctx.IsDenied);
        Assert.Equal(TraceReasons.SearchSensitive, ctx.DenialCode);
        Assert.Equal(0, search.Calls);
        Assert.Equal(0, cloud.Calls);
    }

    [Fact]
    public void The_console_preview_and_the_sanitized_reinspection_honor_an_incomplete_result()
    {
        var snap = new RuntimeSnapshot(V2.Hybrid());
        var preview = Covenant.Host.ConfigAdminApi.Simulate(snap, new IncompleteClassifier(),
            new Covenant.Host.SimulateRequest { ApplicationId = "demo-app", ProfileId = "public-demo", Prompt = "anything" });
        Assert.Equal("refuse", preview.Decision);
        Assert.Equal("Not evaluated", preview.Classification);
        Assert.Contains("could not complete", preview.Reason, StringComparison.Ordinal);

        var outcome = PolicyOutcome.Allow(new RouteTarget("openai", V2.CloudModel)) with
        {
            SanitizedPayloadRequired = true, RedactionTypes = ["email", "phone", "us_ssn"], RedactionTasks = ["triage_case"],
        };
        var record = "{\"schema\":\"covenant.record.v1\",\"task\":\"triage_case\",\"fields\":{\"us_ssn\":\"000-12-3456\",\"service\":\"account\"}}";
        var request = new InferenceRequest("p", [new ChatMessage(ChatRole.User, record)], null, AttributionTags.Unattributed);
        var decision = SanitizedPayload.Evaluate(request.Messages, DataClassification.Pii, outcome, new IncompleteClassifier(), request);
        Assert.False(decision.Applied);
        Assert.Equal(RedactionReasons.Incomplete, decision.ReasonCode);
        Assert.Null(decision.Messages);

        // No regression of the narrow sanitizer exception: the real inspector still passes the built payload.
        var ok = SanitizedPayload.Evaluate(request.Messages, DataClassification.Pii, outcome, new LocalSensitiveDataInspector(), request);
        Assert.True(ok.Applied, ok.Reason);
    }

    // ---------------------------------------------------------------- malformed Unicode: refused, never inspected "as-is"

    [Fact]
    public void Malformed_unicode_is_an_incomplete_inspection()
    {
        // Built in code (not [InlineData]): lone surrogates do not survive test-case serialization.
        foreach (var text in new[] { "Summarize: SSN 000-12-3456 " + '\uD800' + " tail", "Summarize this " + '\uDC00' + " text" })
        {
            var r = InspectUser(text);
            Assert.True(r.Incomplete);
            Assert.Equal(InspectionSignals.Incomplete, r.Signal);
        }
        // An ESCAPED lone surrogate decodes into the same malformed text: also refused.
        Assert.True(InspectUser("{\"note\":\"\\uD800 x\"}").Incomplete);
    }

    [Fact]
    public async Task Malformed_unicode_never_reaches_any_provider_or_search()
    {
        var rig = Build();
        var (ctx, e) = await rig.V2.RunAsync("Summarize this ticket \uD800 please.", profile: "public-demo",
            webSearch: new WebSearchRequest(WebSearchMode.Required));
        Assert.True(ctx.IsDenied);
        Assert.Equal("classify", ctx.StoppedAtStage);
        Assert.Equal(TraceReasons.InspectionIncomplete, ctx.DenialCode);
        Assert.Equal(0, rig.Local.Calls + rig.Cloud.Calls + rig.Search.Calls);
        Assert.False(e.ClassificationEvaluated);
    }
}
