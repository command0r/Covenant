using Covenant.Core;
using Microsoft.Extensions.Hosting;
using Neo4j.Driver;

namespace Covenant.Host;

/// <summary>ADR-0006: optional projection of the audit log into in-perimeter Neo4j; never load-bearing — the chained log stays the evidence of record, the graph is rebuildable derived data.
/// Idempotent (MERGE on stable keys), so restarts/replays are safe; if Neo4j is down, governance is unaffected and the projector catches up.</summary>
public sealed class EvidenceGraphProjector(string auditPath, string uri, string user, string password, string? anchorPath = null)
    : BackgroundService
{
    private const string UpsertCypher =
        """
        UNWIND $entries AS e
        MERGE (p:Principal { name: e.principal })
        MERGE (t:Team { name: e.team })
        MERGE (p)-[:MEMBER_OF]->(t)
        MERGE (r:Request { id: e.id })
          SET r.ts = e.ts, r.effect = e.effect, r.tokensIn = e.tokensIn, r.tokensOut = e.tokensOut,
              r.costUsd = e.costUsd, r.durationMs = e.durationMs, r.promptChars = e.promptChars,
              r.promptSha256 = e.promptSha256, r.workflow = e.workflow, r.useCase = e.useCase
        MERGE (r)-[:BY]->(p)
        MERGE (c:Classification { name: e.classification })
        MERGE (r)-[:CLASSIFIED_AS]->(c)
        FOREACH (_ IN CASE WHEN e.model IS NULL THEN [] ELSE [1] END |
          MERGE (m:Model { name: e.model })
          MERGE (r)-[:SERVED_BY]->(m))
        FOREACH (_ IN CASE WHEN e.denialReason IS NULL THEN [] ELSE [1] END |
          MERGE (d:DenialReason { text: e.denialReason })
          MERGE (r)-[:DENIED_FOR]->(d))
        """;

    private int _projected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IDriver driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
        await using var _ = driver;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Only verified prefix is projected — nothing past a chain break enters the graph.
                var (_, entries) = AuditChainVerifier.VerifyAndRead(auditPath, anchorPath);
                if (entries.Count > _projected)
                {
                    var batch = ToParameters(entries.Skip(_projected).ToList());
                    await using var session = driver.AsyncSession();
                    await session.ExecuteWriteAsync(async tx =>
                    {
                        var cursor = await tx.RunAsync(UpsertCypher, new { entries = batch });
                        await cursor.ConsumeAsync(); // consume inside the tx — the cursor dies with it
                    });
                    _projected = entries.Count;
                }
                else if (entries.Count < _projected)
                {
                    _projected = 0; // log was rotated/reset — reproject the fresh chain from scratch
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Neo4j unreachable or transient failure: governance is unaffected; retry next cycle.
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Pure mapping from audit entries to Cypher parameters — unit-tested separately from
    /// any live database.</summary>
    public static List<Dictionary<string, object?>> ToParameters(IReadOnlyList<AuditEntry> entries)
        => entries.Select(e => new Dictionary<string, object?>
        {
            ["id"] = e.Id,
            ["ts"] = e.TimestampUtc.UtcDateTime.ToString("o"),
            ["principal"] = e.Principal,
            ["team"] = e.Tags.Team,
            ["workflow"] = e.Tags.Workflow,
            ["useCase"] = e.Tags.UseCase,
            ["classification"] = e.Classification.ToString(),
            ["effect"] = e.Effect.ToString(),
            ["model"] = e.ServedByModel,
            ["denialReason"] = e.Effect == PolicyEffect.Deny ? e.Reason : null,
            ["tokensIn"] = e.Usage.InputTokens,
            ["tokensOut"] = e.Usage.OutputTokens,
            ["costUsd"] = (double)e.Usage.CostUsd,
            ["durationMs"] = e.DurationMs,
            ["promptChars"] = e.PromptChars,
            ["promptSha256"] = e.PromptSha256,
        }).ToList();
}
