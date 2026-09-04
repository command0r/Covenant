using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>ADR-0007 guarantees: end-truncation past an anchor is detected (the gap the plain chain cannot see), anchor tampering is detected, rotation archives both files, and no anchors = unchanged behavior.</summary>
public sealed class AnchorTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), $"covenant-anchor-test-{Guid.NewGuid():n}.log");
    private readonly string _anchors;

    public AnchorTests() => _anchors = _log + ".anchors";

    public void Dispose()
    {
        foreach (var f in new[] { _log, _anchors })
            if (File.Exists(f)) File.Delete(f);
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_log) + "*.archived"))
            File.Delete(f);
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_anchors) + "*.archived"))
            File.Delete(f);
    }

    private static AuditEntry Entry(string id) =>
        new(id, DateTimeOffset.UtcNow, "tester", new AttributionTags("platform", "w", "u"),
            DataClassification.Internal, PolicyEffect.Allow, "allowed", "gpt-4o-mini", new Usage(10, 5, 0.001m));

    private async Task<FileAuditSink> Write(int count, int anchorEvery)
    {
        var sink = new FileAuditSink(_log, _anchors, anchorEvery);
        await sink.StartAsync(default);
        for (int i = 0; i < count; i++) await sink.EnqueueAsync(Entry($"e{i}"));
        await sink.StopAsync(default);
        return sink;
    }

    [Fact]
    public async Task End_truncation_is_invisible_to_the_plain_chain_but_caught_by_anchors()
    {
        await Write(4, anchorEvery: 2);                                  // anchors at entries 2 and 4

        var lines = await File.ReadAllLinesAsync(_log);
        await File.WriteAllLinesAsync(_log, lines.Take(3));              // silently drop the last entry

        var (plain, _) = AuditChainVerifier.VerifyAndRead(_log);
        Assert.True(plain.Valid);                                        // the documented blind spot…

        var (anchored, _) = AuditChainVerifier.VerifyAndRead(_log, _anchors);
        Assert.False(anchored.Valid);                                    // …closed by ADR-0007
        Assert.Contains("truncated", anchored.Failure);
    }

    [Fact]
    public async Task Rewritten_history_with_forged_log_hashes_is_caught_by_the_anchor_hash()
    {
        await Write(2, anchorEvery: 1);

        // Rewrite the whole log coherently (valid chain, different content) — only the anchor knows.
        File.Delete(_log);
        var sink = new FileAuditSink(_log);                              // no anchors: writes a fresh valid chain
        await sink.StartAsync(default);
        await sink.EnqueueAsync(Entry("forged-1"));
        await sink.EnqueueAsync(Entry("forged-2"));
        await sink.StopAsync(default);

        var (verification, _) = AuditChainVerifier.VerifyAndRead(_log, _anchors);

        Assert.False(verification.Valid);
        Assert.Contains("hash mismatch", verification.Failure);
    }

    [Fact]
    public async Task Rotation_archives_log_and_anchor_file_together()
    {
        var sink = await Write(2, anchorEvery: 1);

        var archived = await sink.RotateAsync();

        Assert.NotNull(archived);
        Assert.False(File.Exists(_log));
        Assert.False(File.Exists(_anchors));                             // anchors retire with their chain
        Assert.True(File.Exists(archived));
    }

    [Fact]
    public async Task Anchor_cadence_only_anchors_every_nth_entry()
    {
        await Write(5, anchorEvery: 2);

        var anchors = (await File.ReadAllLinesAsync(_anchors)).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        Assert.Equal(2, anchors.Count);                                  // at entries 2 and 4; entry 5 unanchored
        Assert.StartsWith("2\t", anchors[0]);
        Assert.StartsWith("4\t", anchors[1]);
    }

    [Fact]
    public async Task Without_anchors_configured_verification_behaves_as_before()
    {
        var sink = new FileAuditSink(_log);
        await sink.StartAsync(default);
        await sink.EnqueueAsync(Entry("a"));
        await sink.StopAsync(default);

        var (verification, entries) = AuditChainVerifier.VerifyAndRead(_log, anchorPath: null);

        Assert.True(verification.Valid);
        Assert.Single(entries);
        Assert.False(File.Exists(_anchors));
    }
}
