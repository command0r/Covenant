using System.Net;
using System.Text.Json;
using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>Spec §5 archive navigation: segments are the current audit file plus its archived siblings, addressed only by
/// opaque ids from the enumerated set (no caller paths); cursor pagination per segment; per-request export without
/// prompt/response content.</summary>
public sealed class ArchiveExportTests : IDisposable
{
    private readonly RestartableHybridFactory _factory = new(RestartableHybridFactory.NewAuditPath());
    private readonly HttpClient _admin;

    public ArchiveExportTests() => _admin = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<string> RunAsync(string prompt)
    {
        using var resp = await Playground.PostAsync(_admin, Playground.Body("demo-app", prompt));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);
        await CorrelationTestKit.LookupAsync(_admin, id);
        return id;
    }

    private async Task<(HttpStatusCode, JsonElement)> GetAsync(string path)
    {
        var (status, body, _) = await V2Admin.SendAsync(_admin, HttpMethod.Get, path);
        return (status, body);
    }

    [Fact]
    public async Task Segments_enumerate_current_and_archived_and_requests_page_through_one_segment_by_cursor()
    {
        var old1 = await RunAsync("first request before the archive");
        var old2 = await RunAsync("second request before the archive");
        var (resetStatus, _, _) = await V2Admin.SendAsync(_admin, HttpMethod.Post, "/admin/reset");
        Assert.Equal(HttpStatusCode.OK, resetStatus);
        var current = await RunAsync("after the archive");

        var (status, segments) = await GetAsync("/admin/audit/segments");
        Assert.Equal(HttpStatusCode.OK, status);
        var list = segments.EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("current", list[0].GetProperty("id").GetString());
        Assert.True(list[0].GetProperty("current").GetBoolean());
        Assert.Equal(1, list[0].GetProperty("entries").GetInt32());
        var archivedId = list[1].GetProperty("id").GetString()!;
        Assert.Matches("^a-[0-9a-f]{16}$", archivedId);                    // opaque: no path, no file name
        Assert.Equal(2, list[1].GetProperty("entries").GetInt32());
        Assert.True(list[1].GetProperty("chain_valid").GetBoolean());
        Assert.DoesNotContain(Path.GetDirectoryName(_factory.AuditPath)!, segments.GetRawText(), StringComparison.Ordinal);

        var (_, page1) = await GetAsync($"/admin/requests?segment={archivedId}&limit=1");
        Assert.Equal(archivedId, page1.GetProperty("segment").GetProperty("id").GetString());
        Assert.Equal(old2, page1.GetProperty("items")[0].GetProperty("Id").GetString());       // newest first
        var cursor = page1.GetProperty("next_cursor").GetString()!;
        var (_, page2) = await GetAsync($"/admin/requests?cursor={Uri.EscapeDataString(cursor)}&limit=1");
        Assert.Equal(old1, page2.GetProperty("items")[0].GetProperty("Id").GetString());
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("next_cursor").ValueKind);

        var (_, now) = await GetAsync("/admin/requests");
        Assert.Equal(current, Assert.Single(now.GetProperty("items").EnumerateArray()).GetProperty("Id").GetString());
        Assert.Equal("current", now.GetProperty("segment").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/tmp/covenant-audit.log")]
    [InlineData("covenant-audit.log.20260719-014211.archived")]
    [InlineData("a-0000000000000000")]
    [InlineData("current/..")]
    public async Task Unknown_segment_ids_and_paths_are_rejected(string segment)
    {
        var (status, body) = await GetAsync($"/admin/requests?segment={Uri.EscapeDataString(segment)}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("unknown_segment", body.GetProperty("error").GetString());
        var (exportStatus, _) = await GetAsync($"/admin/requests/req_00000000000000000000000000000000/export?segment={Uri.EscapeDataString(segment)}");
        Assert.Equal(HttpStatusCode.BadRequest, exportStatus);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("YzF8Y3VycmVudHwtMQ")]        // "c1|current|-1"
    [InlineData("eHx5")]                      // "x|y"
    public async Task Malformed_cursor_is_rejected(string cursor)
    {
        var (status, body) = await GetAsync($"/admin/requests?cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_cursor", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Export_carries_version_attempts_trace_costs_and_integrity_but_no_content()
    {
        // Opt in to a prompt preview so the record itself holds content: the export must still exclude it.
        var (version, cfg, _) = await V2Admin.GetConfigAsync(_admin);
        await V2Admin.ApplyAsync(_admin, cfg with { Settings = cfg.Settings with { PromptPreviewChars = 200 } }, version, "preview on");
        const string secretPrompt = "ZEBRA-PROMPT-MARKER internal question";
        var id = await RunAsync(secretPrompt);
        var record = await CorrelationTestKit.LookupAsync(_admin, id);
        Assert.Contains("ZEBRA-PROMPT-MARKER", record.PromptPreview, StringComparison.Ordinal);   // the record has it…

        var (status, export) = await GetAsync($"/admin/requests/{id}/export");
        Assert.Equal(HttpStatusCode.OK, status);
        var raw = export.GetRawText();
        Assert.DoesNotContain("ZEBRA-PROMPT-MARKER", raw, StringComparison.Ordinal);               // …the export does not
        Assert.DoesNotContain("local reply", raw, StringComparison.Ordinal);
        Assert.False(export.GetProperty("content_included").GetBoolean());
        Assert.Equal(2, export.GetProperty("config_version").GetInt32());
        Assert.Equal(id, export.GetProperty("record").GetProperty("Id").GetString());
        Assert.Equal(1, export.GetProperty("attempts").GetArrayLength());
        Assert.True(export.GetProperty("trace").GetArrayLength() > 0);
        var integrity = export.GetProperty("integrity");
        Assert.Equal("verified", integrity.GetProperty("status").GetString());
        Assert.True(integrity.GetProperty("chain_valid").GetBoolean());
        Assert.Matches("^[0-9a-f]{64}$", integrity.GetProperty("entry_hash").GetString()!);
        Assert.Equal("current", integrity.GetProperty("segment_id").GetString());

        // Archived records stay exportable.
        await V2Admin.SendAsync(_admin, HttpMethod.Post, "/admin/reset");
        var (archivedStatus, archived) = await GetAsync($"/admin/requests/{id}/export");
        Assert.Equal(HttpStatusCode.OK, archivedStatus);
        Assert.StartsWith("a-", archived.GetProperty("integrity").GetProperty("segment_id").GetString(), StringComparison.Ordinal);

        var (missing, _) = await GetAsync("/admin/requests/req_ffffffffffffffffffffffffffffffff/export");
        Assert.Equal(HttpStatusCode.NotFound, missing);
    }
}

/// <summary>Savings are reported only against a configured, priced baseline; cache and route avoidance separately;
/// estimated/unknown records never feed a claim.</summary>
public sealed class SavingsTests
{
    private static AuditEntry Served(string model, string? basis, bool cacheHit = false) => new(
        $"req_{Guid.NewGuid():n}", DateTimeOffset.UtcNow, "p", new AttributionTags("t", "w", "u"),
        DataClassification.Public, PolicyEffect.Allow, "allowed", model, new Usage(1000, 1000, cacheHit ? 0m : 0.001m),
        CacheHit: cacheHit, SchemaVersion: 2, CostBasis: basis);

    private static readonly Dictionary<string, (decimal, decimal)> Prices = new()
    {
        ["gpt-4o"] = (0.0025m, 0.01m),
        ["gpt-4o-mini"] = (0.00015m, 0.0006m),
        ["qwen3:8b"] = (0m, 0m),
    };

    [Fact]
    public void Without_a_baseline_savings_are_hidden_and_zero()
    {
        var f = FinOps.Build([Served("gpt-4o-mini", "reported"), Served("gpt-4o-mini", "no_call", cacheHit: true)], "qwen3:8b", "qwen3:8b", Prices, savingsBaseline: null);
        Assert.Null(f.Savings);
        Assert.Equal(0m, f.EstimatedSavingsUsd);
        Assert.Equal(0m, f.SavingsCacheUsd);
    }

    [Fact]
    public void With_a_priced_baseline_route_and_cache_avoidance_are_reported_separately_excluding_estimates()
    {
        var f = FinOps.Build(
        [
            Served("gpt-4o-mini", "reported"),
            Served("gpt-4o-mini", "no_call", cacheHit: true),
            Served("gpt-4o-mini", "estimated_from_text"),
        ], "qwen3:8b", "gpt-4o", Prices, savingsBaseline: "openai/gpt-4o");

        var s = Assert.IsType<SavingsSummary>(f.Savings);
        Assert.Equal("openai/gpt-4o", s.Baseline);
        Assert.Equal("Estimated provider charge avoided", s.Label);
        decimal routeDelta = 1m * (0.0025m - 0.00015m) + 1m * (0.01m - 0.0006m);   // per request vs the baseline
        Assert.Equal(2 * routeDelta, s.RouteAvoidedUsd);                            // the reported + the cache hit, not the estimate
        Assert.Equal(1m * 0.00015m + 1m * 0.0006m, s.CacheAvoidedUsd);
        Assert.Equal(s.RouteAvoidedUsd + s.CacheAvoidedUsd, s.TotalUsd);
        Assert.Equal(1, s.ExcludedRecords);
    }
}
