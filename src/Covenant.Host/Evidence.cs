using System.Text.Json;
using System.Text.Json.Serialization;
using Covenant.Core;

namespace Covenant.Host;

/// <summary>Result of walking the audit chain. Invalid means the log cannot be trusted past FirstInvalidLine.</summary>
public sealed record ChainVerification(bool Valid, int EntryCount, int? FirstInvalidLine, string? Failure)
{
    public static ChainVerification Ok(int count) => new(true, count, null, null);
    public static ChainVerification Broken(int count, int line, string failure) => new(false, count, line, failure);
}

/// <summary>Re-walks the log recomputing every hash (format in AuditChain): detects edits, forged hashes,
/// removed/reordered lines, mid-file restarts. Entries return only up to the first invalid line.</summary>
public static class AuditChainVerifier
{
    public static (ChainVerification Verification, IReadOnlyList<AuditEntry> Entries) VerifyAndRead(string path)
        => VerifyAndRead(path, anchorPath: null);

    /// <summary>With an anchor path (ADR-0007), the log must contain every anchored count with exactly
    /// the anchored head hash at that position — end-truncation past any anchor fails verification.</summary>
    public static (ChainVerification Verification, IReadOnlyList<AuditEntry> Entries) VerifyAndRead(string path, string? anchorPath)
    {
        var entries = new List<AuditEntry>();
        var hashes = new List<string>();

        if (File.Exists(path))
        {
            var previous = AuditChain.GenesisHash;
            int lineNo = 0;

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lineNo++;

                var parts = line.Split(AuditChain.Separator, 3);
                if (parts.Length != 3)
                    return (ChainVerification.Broken(entries.Count, lineNo, "malformed line"), entries);

                var (prevHash, entryHash, content) = (parts[0], parts[1], parts[2]);

                if (!string.Equals(prevHash, previous, StringComparison.Ordinal))
                    return (ChainVerification.Broken(entries.Count, lineNo, "chain link mismatch (removed, reordered, or restarted)"), entries);

                if (!string.Equals(entryHash, AuditChain.Hash(previous, content), StringComparison.Ordinal))
                    return (ChainVerification.Broken(entries.Count, lineNo, "content hash mismatch (entry altered)"), entries);

                var entry = JsonSerializer.Deserialize(content, AuditJsonContext.Default.AuditEntry);
                if (entry is null)
                    return (ChainVerification.Broken(entries.Count, lineNo, "entry is not valid JSON"), entries);

                entries.Add(entry);
                hashes.Add(entryHash);
                previous = entryHash;
            }
        }

        if (anchorPath is not null && File.Exists(anchorPath))
        {
            // Anchor failure ⇒ the log itself may be a coherent forgery, so NO prefix is trustworthy:
            // fail closed with an EMPTY entry list (unlike chain-walk breaks, whose prefix is hash-verified).
            int anchorNo = 0;
            long lastCount = 0;
            foreach (var line in File.ReadLines(anchorPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                anchorNo++;

                var parts = line.Split(AuditChain.Separator, 3);
                if (parts.Length != 3 || !long.TryParse(parts[0], out var anchoredCount) || anchoredCount < 1)
                    return (ChainVerification.Broken(0, anchorNo, $"malformed anchor at anchor-file line {anchorNo}"), []);

                if (anchoredCount <= lastCount)
                    return (ChainVerification.Broken(0, anchorNo,
                        $"anchor sequence not increasing at anchor-file line {anchorNo} (anchor file tampered)"), []);
                lastCount = anchoredCount;

                if (anchoredCount > entries.Count)
                    return (ChainVerification.Broken(0, anchorNo,
                        $"log truncated: anchor at anchor-file line {anchorNo} attests {anchoredCount} entries, log has {entries.Count}"), []);

                if (!string.Equals(hashes[(int)anchoredCount - 1], parts[1], StringComparison.Ordinal))
                    return (ChainVerification.Broken(0, anchorNo,
                        $"anchor hash mismatch at entry {anchoredCount} (anchor-file line {anchorNo}: history rewritten)"), []);
            }
        }

        return (ChainVerification.Ok(entries.Count), entries);
    }
}

/// <summary>The compliance-evidence export: chain integrity plus the aggregate story an auditor asks for
/// first. The audit log itself remains the raw evidence; this report is the verifiable summary of it.</summary>
public sealed class EvidenceReport
{
    [JsonPropertyName("generated_utc")] public required DateTimeOffset GeneratedUtc { get; init; }
    [JsonPropertyName("audit_log_path")] public required string AuditLogPath { get; init; }
    [JsonPropertyName("chain_valid")] public required bool ChainValid { get; init; }
    [JsonPropertyName("chain_failure")] public string? ChainFailure { get; init; }
    [JsonPropertyName("first_invalid_line")] public int? FirstInvalidLine { get; init; }
    [JsonPropertyName("entries")] public required int Entries { get; init; }
    [JsonPropertyName("allowed")] public required int Allowed { get; init; }
    [JsonPropertyName("denied")] public required int Denied { get; init; }
    [JsonPropertyName("first_entry_utc")] public DateTimeOffset? FirstEntryUtc { get; init; }
    [JsonPropertyName("last_entry_utc")] public DateTimeOffset? LastEntryUtc { get; init; }
    [JsonPropertyName("total_cost_usd")] public required decimal TotalCostUsd { get; init; }
    [JsonPropertyName("cost_by_team_usd")] public required Dictionary<string, decimal> CostByTeamUsd { get; init; }
    [JsonPropertyName("requests_by_classification")] public required Dictionary<string, int> RequestsByClassification { get; init; }
}

/// <summary>Durable budgets without new infrastructure: the audit log is the event store, the ledger its
/// projection rebuilt by replay at boot. Outgrowing replay is the trigger for the audit-store ADR — not before.</summary>
public static class LedgerReplay
{
    public static void Rebuild(IReadOnlyList<AuditEntry> entries, Covenant.Governance.ISpendLedger ledger)
    {
        foreach (var e in entries)
            if (e.Usage.CostUsd > 0m)
                ledger.Record(e.Tags.Team, e.Usage.CostUsd);
    }
}

public static class EvidenceExport
{
    public static EvidenceReport Build(string auditLogPath, TimeProvider clock, string? anchorPath = null)
    {
        var (verification, entries) = AuditChainVerifier.VerifyAndRead(auditLogPath, anchorPath);

        var costByTeam = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var byClassification = new Dictionary<string, int>();
        int allowed = 0, denied = 0;
        decimal totalCost = 0m;

        foreach (var e in entries)
        {
            if (e.Effect == PolicyEffect.Allow) allowed++; else denied++;
            totalCost += e.Usage.CostUsd;
            costByTeam[e.Tags.Team] = costByTeam.GetValueOrDefault(e.Tags.Team) + e.Usage.CostUsd;
            var cls = e.Classification.ToString();
            byClassification[cls] = byClassification.GetValueOrDefault(cls) + 1;
        }

        return new EvidenceReport
        {
            GeneratedUtc = clock.GetUtcNow(),
            AuditLogPath = Path.GetFullPath(auditLogPath),
            ChainValid = verification.Valid,
            ChainFailure = verification.Failure,
            FirstInvalidLine = verification.FirstInvalidLine,
            Entries = entries.Count,
            Allowed = allowed,
            Denied = denied,
            FirstEntryUtc = entries.Count > 0 ? entries[0].TimestampUtc : null,
            LastEntryUtc = entries.Count > 0 ? entries[^1].TimestampUtc : null,
            TotalCostUsd = totalCost,
            CostByTeamUsd = costByTeam,
            RequestsByClassification = byClassification,
        };
    }
}
