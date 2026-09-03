using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>Pure projection mapping (ADR-0006): lossless for metadata, never invents relationships — no model edge without a served model, no denial edge for allows.</summary>
public class EvidenceGraphTests
{
    private static AuditEntry Entry(PolicyEffect effect, string? model, string reason) =>
        new(
            Id: "req-1",
            TimestampUtc: new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero),
            Principal: "alice",
            Tags: new AttributionTags("payments", "reporting", "quarter-close"),
            Classification: DataClassification.Pii,
            Effect: effect,
            Reason: reason,
            ServedByModel: model,
            Usage: new Usage(120, 40, 0.0005m),
            DurationMs: 812,
            PromptChars: 480,
            PromptSha256: "abc123",
            Signal: "US SSN pattern");

    [Fact]
    public void Served_request_maps_model_and_no_denial_reason()
    {
        var p = EvidenceGraphProjector.ToParameters([Entry(PolicyEffect.Allow, "llama-3.1-8b-instruct", "allowed")])[0];

        Assert.Equal("req-1", p["id"]);
        Assert.Equal("alice", p["principal"]);
        Assert.Equal("payments", p["team"]);
        Assert.Equal("Pii", p["classification"]);
        Assert.Equal("Allow", p["effect"]);
        Assert.Equal("llama-3.1-8b-instruct", p["model"]);
        Assert.Null(p["denialReason"]);                       // allows never grow a DENIED_FOR edge
        Assert.Equal(120L, p["tokensIn"]);
        Assert.Equal("abc123", p["promptSha256"]);
    }

    [Fact]
    public void Denied_request_maps_reason_and_no_model()
    {
        var p = EvidenceGraphProjector.ToParameters([Entry(PolicyEffect.Deny, null, "no adapter registered for 'local:x'")])[0];

        Assert.Equal("Deny", p["effect"]);
        Assert.Null(p["model"]);                              // refusals never grow a SERVED_BY edge
        Assert.Equal("no adapter registered for 'local:x'", p["denialReason"]);
    }
}
