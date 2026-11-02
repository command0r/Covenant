using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>Usage aggregation over recorded evidence: refused/zero-cost requests count, unknown is not $0, retries and search
/// are summed once from their own records (never the request total again), detection ≠ baseline label, control-plane
/// events and out-of-window records are excluded.</summary>
public sealed class UsageTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-01T12:00:00Z");

    private static AuditEntry Rec(string team = "research", string? app = "research-assistant", string? profile = "public-research",
        PolicyEffect effect = PolicyEffect.Allow, string? denial = null, decimal requestTotal = 0m,
        IReadOnlyList<AuditAttempt>? attempts = null, IReadOnlyList<AuditToolCall>? tools = null, bool cacheHit = false,
        DataClassification cls = DataClassification.Public, DataClassification? baseline = DataClassification.Public, string? signal = null,
        AuditRedaction? redaction = null, DateTimeOffset? at = null, string? eventType = null, int? version = 3, bool legacy = false,
        string? servedBy = null, string? costBasis = null)
        => new(
            Id: "req_" + Guid.NewGuid().ToString("n"), TimestampUtc: at ?? T0, Principal: "p", Tags: new AttributionTags(team, "wf", "uc"),
            Classification: cls, Effect: effect, Reason: "r", Usage: new Usage(10, 5, requestTotal),
            CacheHit: cacheHit, SchemaVersion: 2, DenialKind: denial, Signal: signal, ApplicationId: app, ProfileId: profile,
            BaselineClassification: baseline, Attempts: legacy ? null : attempts ?? [], ToolCalls: tools, Redaction: redaction, EventType: eventType,
            ConfigVersion: version, ServedByModel: servedBy, CostBasis: costBasis);

    private static AuditAttempt Att(string conn, decimal? cost, string? basis, string outcome = "served")
        => new("att_1", conn, "m", outcome, null, 10, cost, basis);

    private static AuditToolCall Search(decimal? cost, string basis)
        => new("tool_1", "web_search", "tavily", "succeeded", null, 5, cost, basis, 0.008m, 1);

    private static Boundary? Boundaries(string id, int? version) => id switch { "ollama" => Boundary.Local, "anthropic" => Boundary.Cloud, _ => null };

    private static UsageQuery Day(string group = "team") => new() { From = T0.AddHours(-12), To = T0.AddHours(12), GroupBy = group };

    [Fact]
    public void Inference_and_search_are_summed_once_from_their_own_records_and_unknown_is_not_zero()
    {
        var records = new[]
        {
            // Served via a fallback: failed cloud attempt (estimated) + served local attempt; plus one search. Request total 0.020.
            Rec(requestTotal: 0.020m,
                attempts: [Att("anthropic", 0.004m, "estimated_failed_call", "failed"), Att("ollama", 0m, "zero_priced")],
                tools: [Search(0.016m, "reported")]),
            // Failed with an unknown charge: counted, never $0.
            Rec(effect: PolicyEffect.Deny, denial: "UpstreamFailure", attempts: [Att("anthropic", null, "unknown_failed_call", "failed")]),
            // Refused before any call: counts as a request with no charge.
            Rec(effect: PolicyEffect.Deny, denial: "Governance"),
            // Cache hit: no call.
            Rec(cacheHit: true),
        };
        var (totals, groups) = UsageAggregator.Aggregate(records, Day(), Boundaries);

        Assert.Equal(4, totals.Requests);
        Assert.Equal(2, totals.Served);
        Assert.Equal(1, totals.Failed);
        Assert.Equal(1, totals.Refused);
        Assert.Equal(1, totals.CacheHits);
        Assert.Equal(0.004m, totals.InferenceChargeUsd);          // not 0.020 + 0.004 (no request-total double count)
        Assert.Equal(0.016m, totals.SearchChargeUsd);
        Assert.Equal(0.020m, totals.TotalChargeUsd);
        Assert.Equal(1, totals.EstimatedChargeRequests);
        Assert.Equal(1, totals.UnknownChargeRequests);
        Assert.Equal(1, totals.LocalCalls);
        Assert.Equal(2, totals.ExternalCalls);
        Assert.Equal(1, totals.ExternalRetrievals);
        Assert.Single(groups);
    }

    [Fact]
    public void Detected_signals_and_sensitive_baseline_labels_are_separate_facts()
    {
        var records = new[]
        {
            Rec(cls: DataClassification.Pii, baseline: DataClassification.Internal, signal: "US SSN pattern", effect: PolicyEffect.Deny, denial: "Governance"),
            Rec(cls: DataClassification.Pii, baseline: DataClassification.Pii, signal: null,
                redaction: new AuditRedaction("redact-v1", "sanitized-support", "applied", "redaction_applied", [new("email", 1)], DataClassification.Pii, DataClassification.Internal)),
            Rec(cls: DataClassification.Pii, baseline: DataClassification.Pii, signal: null, effect: PolicyEffect.Deny, denial: "Governance",
                redaction: new AuditRedaction("redact-v1", "sanitized-support", "refused", "redaction_unknown_field", [], DataClassification.Pii, null)),
        };
        var (t, _) = UsageAggregator.Aggregate(records, Day(), Boundaries);
        Assert.Equal(1, t.SensitiveDetected);
        Assert.Equal(2, t.SensitiveBaseline);
        Assert.Equal(1, t.RedactionsApplied);
        Assert.Equal(1, t.RedactionsRefused);
    }

    [Fact]
    public void Grouping_window_and_event_filtering()
    {
        var records = new[]
        {
            Rec(team: "research", app: "research-assistant"),
            Rec(team: "engineering", app: null),
            Rec(team: "research", at: T0.AddDays(-3)),                        // outside the window
            Rec(team: "platform", eventType: AuditEntryKinds.ConfigActivated), // control-plane event, never usage
        };
        var (t, byTeam) = UsageAggregator.Aggregate(records, Day(), Boundaries);
        Assert.Equal(2, t.Requests);
        Assert.Equal(new[] { "engineering", "research" }, byTeam.Select(g => g.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());

        var (_, byApp) = UsageAggregator.Aggregate(records, Day("application"), Boundaries);
        Assert.Contains(byApp, g => g.Key == "not recorded");
        Assert.Single(t.Daily);
    }

    [Theory]
    [InlineData(null, null, "7d", "team", true)]
    [InlineData(null, null, null, "profile", true)]
    [InlineData(null, null, "1y", "team", false)]
    [InlineData(null, null, "24h", "employee", false)]                   // no per-person grouping exists
    [InlineData("2026-11-02T00:00:00Z", "2026-11-01T00:00:00Z", null, "team", false)]
    [InlineData("not-a-date", null, null, "team", false)]
    [InlineData(null, "0001-01-01T00:00:00Z", "7d", "team", false)]     // relative window at the earliest instant: refused, never thrown
    [InlineData(null, "0001-01-02T00:00:00Z", "30d", "team", true)]     // clamped to the earliest instant
    [InlineData(null, "0001-01-01T12:00:00Z", "all", "team", true)]
    public void Query_parsing_is_bounded(string? from, string? to, string? window, string? group, bool ok)
    {
        var (q, error) = UsageAggregator.Parse(from, to, window, group, T0);
        Assert.Equal(ok, q is not null);
        Assert.Equal(ok, error is null);
    }

    [Fact]
    public void Limitations_state_what_the_numbers_are_not()
    {
        var text = string.Join(" ", UsageAggregator.Limitations(unknownVersionRecords: 2));
        Assert.Contains("not free", text, StringComparison.Ordinal);
        Assert.Contains("never as $0", text, StringComparison.Ordinal);
        Assert.Contains("not evidence about any individual", text, StringComparison.Ordinal);
        Assert.Contains("could not be loaded", text, StringComparison.Ordinal);
        Assert.Contains("64 MiB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Boundaries_use_each_records_configuration_version_and_a_missing_version_is_unknown()
    {
        var v2 = V2.Hybrid(2, c => c with { Connections = [V2.Ollama(id: "gateway")] });                // gateway = Local in v2
        var v3 = V2.Hybrid(3, c => c with { Connections = [V2.OpenAi() with { Id = "gateway" }] });     // … Cloud in v3
        int loads = 0;
        var boundaries = UsageAggregator.VersionedBoundaries(v => { loads++; return v switch { 2 => v2, 3 => v3, _ => null }; });
        var records = new[]
        {
            Rec(version: 2, attempts: [Att("gateway", 0m, "zero_priced")]),
            Rec(version: 2, attempts: [Att("gateway", 0m, "zero_priced")]),
            Rec(version: 3, attempts: [Att("gateway", 0.01m, "reported")]),
            Rec(version: 9, attempts: [Att("gateway", 0.01m, "reported")]),   // version not loadable → unknown, never "current"
        };
        var (t, _) = UsageAggregator.Aggregate(records, Day(), boundaries);
        Assert.Equal(2, t.LocalCalls);
        Assert.Equal(1, t.ExternalCalls);
        Assert.Equal(1, t.UnknownBoundaryCalls);
        Assert.Equal(3, loads);                                                 // memoized per version
    }

    [Fact]
    public void A_legacy_served_record_keeps_its_charge_even_without_attempts_or_attempted_model()
    {
        var records = new[] { Rec(legacy: true, servedBy: "gpt-4o-mini", requestTotal: 0.002m, costBasis: null, version: null) };
        var (t, _) = UsageAggregator.Aggregate(records, Day(), Boundaries);
        Assert.Equal(0.002m, t.InferenceChargeUsd);
        Assert.Equal(1, t.UnknownBoundaryCalls);
        Assert.Equal(0, t.UnknownChargeRequests);

        var failed = new[] { Rec(legacy: true, effect: PolicyEffect.Deny, denial: "UpstreamFailure", servedBy: null, requestTotal: 0m, costBasis: "unknown_failed_call") };
        var (f, _) = UsageAggregator.Aggregate(failed, Day(), Boundaries);
        Assert.Equal(1, f.UnknownChargeRequests);                               // unknown, not $0
    }

    [Fact]
    public void Segments_above_the_cap_are_refused_before_any_read()
    {
        var path = Path.Combine(Path.GetTempPath(), $"covenant-usage-{Guid.NewGuid():n}.log");
        File.WriteAllText(path, new string('x', 2048));
        try
        {
            Assert.NotNull(UsageAggregator.SizeRefusal(path, 1024, out var big));
            Assert.Equal(2048, big);
            Assert.Null(UsageAggregator.SizeRefusal(path, 4096, out _));
            Assert.Null(UsageAggregator.SizeRefusal(path + ".missing", 1, out var none));
            Assert.Equal(0, none);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_relative_window_near_the_earliest_instant_is_clamped_not_underflowed()
    {
        var (q, error) = UsageAggregator.Parse(null, "0001-01-03T00:00:00Z", "30d", "team", T0);
        Assert.Null(error);
        Assert.Equal(DateTimeOffset.MinValue, q!.From);
        Assert.Equal(DateTimeOffset.Parse("0001-01-03T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), q.To);
    }
}
