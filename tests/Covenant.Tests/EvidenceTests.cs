using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>
/// Tamper-evidence tests: each one removes a guarantee (edit an entry, delete an entry) and asserts
/// the verifier notices. Written through the real FileAuditSink so the writer and verifier are tested
/// against each other, not against a shared assumption.
/// </summary>
public sealed class EvidenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"covenant-audit-test-{Guid.NewGuid():n}.log");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static AuditEntry Entry(string id, PolicyEffect effect, string team = "platform", decimal cost = 0m) =>
        new(
            Id: id,
            TimestampUtc: DateTimeOffset.UtcNow,
            Principal: "tester",
            Tags: new AttributionTags(team, "test-workflow", "test-case"),
            Classification: DataClassification.Internal,
            Effect: effect,
            Reason: effect == PolicyEffect.Allow ? "allowed" : "denied for test",
            ServedByModel: effect == PolicyEffect.Allow ? "gpt-4o-mini" : null,
            Usage: new Usage(10, 5, cost));

    private async Task WriteThroughRealSink(params AuditEntry[] entries)
    {
        var sink = new FileAuditSink(_path);
        await sink.StartAsync(default);
        foreach (var e in entries) await sink.EnqueueAsync(e);
        await sink.StopAsync(default);   // completes the channel and awaits the drain
    }

    [Fact]
    public async Task Untouched_log_verifies_and_the_report_aggregates_the_evidence()
    {
        await WriteThroughRealSink(
            Entry("a", PolicyEffect.Allow, team: "payments", cost: 0.10m),
            Entry("b", PolicyEffect.Allow, team: "platform", cost: 0.20m),
            Entry("c", PolicyEffect.Deny,  team: "platform"));

        var report = EvidenceExport.Build(_path, TimeProvider.System);

        Assert.True(report.ChainValid);
        Assert.Equal(3, report.Entries);
        Assert.Equal(2, report.Allowed);
        Assert.Equal(1, report.Denied);
        Assert.Equal(0.30m, report.TotalCostUsd);
        Assert.Equal(0.10m, report.CostByTeamUsd["payments"]);
        Assert.Equal(0.20m, report.CostByTeamUsd["platform"]);
        Assert.Equal(3, report.RequestsByClassification["Internal"]);
    }

    [Fact]
    public async Task Rewriting_history_is_detected_and_nothing_after_the_break_is_trusted()
    {
        await WriteThroughRealSink(Entry("a", PolicyEffect.Deny), Entry("b", PolicyEffect.Allow));

        var lines = await File.ReadAllLinesAsync(_path);
        lines[0] = lines[0].Replace("\"Deny\"", "\"Allow\"");    // turn a recorded denial into an allow
        await File.WriteAllLinesAsync(_path, lines);

        var (verification, entries) = AuditChainVerifier.VerifyAndRead(_path);

        Assert.False(verification.Valid);
        Assert.Equal(1, verification.FirstInvalidLine);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task Removing_an_entry_is_detected()
    {
        await WriteThroughRealSink(
            Entry("a", PolicyEffect.Allow), Entry("b", PolicyEffect.Deny), Entry("c", PolicyEffect.Allow));

        var lines = await File.ReadAllLinesAsync(_path);
        await File.WriteAllLinesAsync(_path, [lines[0], lines[2]]);   // disappear the denial

        var (verification, entries) = AuditChainVerifier.VerifyAndRead(_path);

        Assert.False(verification.Valid);
        Assert.Equal(2, verification.FirstInvalidLine);
        Assert.Single(entries);                                        // only the pre-break prefix is evidence
    }

    [Fact]
    public async Task Forged_hashes_cannot_hide_an_edit()
    {
        await WriteThroughRealSink(Entry("a", PolicyEffect.Deny), Entry("b", PolicyEffect.Allow));

        // A smarter attacker edits line 1 AND recomputes its hash — the next line's chain link breaks instead.
        var lines = await File.ReadAllLinesAsync(_path);
        var parts = lines[0].Split(AuditChain.Separator, 3);
        var forgedContent = parts[2].Replace("\"Deny\"", "\"Allow\"");
        var forgedHash = AuditChain.Hash(parts[0], forgedContent);
        lines[0] = $"{parts[0]}{AuditChain.Separator}{forgedHash}{AuditChain.Separator}{forgedContent}";
        await File.WriteAllLinesAsync(_path, lines);

        var (verification, _) = AuditChainVerifier.VerifyAndRead(_path);

        Assert.False(verification.Valid);
        Assert.Equal(2, verification.FirstInvalidLine);
    }

    [Fact]
    public async Task Chain_resumes_across_restarts_and_still_verifies()
    {
        await WriteThroughRealSink(Entry("a", PolicyEffect.Allow));
        await WriteThroughRealSink(Entry("b", PolicyEffect.Deny));    // second sink instance = a restart

        var (verification, entries) = AuditChainVerifier.VerifyAndRead(_path);

        Assert.True(verification.Valid);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task Ledger_rebuilt_from_the_audit_log_matches_original_spend()
    {
        await WriteThroughRealSink(
            Entry("a", PolicyEffect.Allow, team: "payments", cost: 0.10m),
            Entry("b", PolicyEffect.Allow, team: "payments", cost: 0.05m),
            Entry("c", PolicyEffect.Allow, team: "platform", cost: 0.20m),
            Entry("d", PolicyEffect.Deny,  team: "platform"));            // denials cost nothing

        var (verification, entries) = AuditChainVerifier.VerifyAndRead(_path);
        Assert.True(verification.Valid);

        var ledger = new Covenant.Governance.InMemorySpendLedger();       // "restarted" appliance
        LedgerReplay.Rebuild(entries, ledger);

        Assert.Equal(0.15m, ledger.TeamSpendUsd("payments"));
        Assert.Equal(0.20m, ledger.TeamSpendUsd("platform"));
        Assert.Equal(0.35m, ledger.GlobalSpendUsd);
    }

    [Fact]
    public async Task Missing_log_is_an_empty_but_valid_report()
    {
        var report = EvidenceExport.Build(_path, TimeProvider.System);

        Assert.True(report.ChainValid);
        Assert.Equal(0, report.Entries);
    }
}
