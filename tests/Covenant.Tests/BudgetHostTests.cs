using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>ADR-0014 in the Host: GET /admin/budgets / {id}/requests from the persistent ledger; an audit archive never
/// reopens a budget; budget id/period/cap edits keep accrual; restart replays the ledger (and recovers open
/// reservations); a pre-ledger audit segment is imported once.</summary>
public sealed class BudgetHostTests
{
    private static async Task<JsonElement> BudgetsAsync(HttpClient admin)
    {
        var (status, body, _) = await V2Admin.SendAsync(admin, HttpMethod.Get, "/admin/budgets");
        Assert.Equal(HttpStatusCode.OK, status);
        return body;
    }

    private static JsonElement Budget(JsonElement list, string id) => list.EnumerateArray().Single(b => b.GetProperty("id").GetString() == id);

    private static async Task<string> PublicRequestAsync(RestartableHybridFactory factory, string prompt = "Summarize this public announcement.")
    {
        using var app = factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo-key");
        app.DefaultRequestHeaders.Add("X-Covenant-Profile", "public-demo");
        using var resp = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat(prompt));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return CorrelationTestKit.HeaderId(resp);
    }

    // Seed cloud price $0.15 / $0.60 per 1M; the stub reports 10 in / 5 out.
    private const decimal OneRequest = 10 / 1_000_000m * 0.15m + 5 / 1_000_000m * 0.60m;

    [Fact]
    public async Task Budgets_report_spent_reserved_remaining_period_and_contributing_requests()
    {
        using var factory = new RestartableHybridFactory(RestartableHybridFactory.NewAuditPath());
        var admin = factory.CreateClient();
        var id = await PublicRequestAsync(factory);
        await CorrelationTestKit.LookupAsync(admin, id);

        var global = Budget(await BudgetsAsync(admin), "global");
        Assert.Equal("global", global.GetProperty("scope").GetString());
        Assert.Equal("monthly", global.GetProperty("period").GetString());
        Assert.Equal(DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), global.GetProperty("period_id").GetString());
        Assert.Equal(OneRequest, global.GetProperty("spent_usd").GetDecimal());
        Assert.Equal(0m, global.GetProperty("reserved_usd").GetDecimal());
        Assert.Equal(100m - OneRequest, global.GetProperty("remaining_usd").GetDecimal());
        Assert.Equal("none", global.GetProperty("warning").GetString());
        Assert.Equal("hard", global.GetProperty("enforcement").GetString());

        var (status, contributions, _) = await V2Admin.SendAsync(admin, HttpMethod.Get, "/admin/budgets/global/requests?limit=10");
        Assert.Equal(HttpStatusCode.OK, status);
        var item = Assert.Single(contributions.GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("request_id").GetString());
        Assert.Equal("att_1", item.GetProperty("attempt_id").GetString());
        Assert.Equal(OneRequest, item.GetProperty("amount_usd").GetDecimal());

        var (missing, _, _) = await V2Admin.SendAsync(admin, HttpMethod.Get, "/admin/budgets/nope/requests");
        Assert.Equal(HttpStatusCode.NotFound, missing);
        using var noToken = await admin.GetAsync("/admin/budgets");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        // Status reads the same ledger.
        var (_, statusBody, _) = await V2Admin.SendAsync(admin, HttpMethod.Get, "/admin/status");
        Assert.Equal(OneRequest, statusBody.GetProperty("budget").GetProperty("global_spend_usd").GetDecimal());
    }

    [Fact]
    public async Task Audit_archive_does_not_reopen_a_budget()
    {
        using var factory = new RestartableHybridFactory(RestartableHybridFactory.NewAuditPath());
        var admin = factory.CreateClient();
        await CorrelationTestKit.LookupAsync(admin, await PublicRequestAsync(factory));

        var (resetStatus, _, _) = await V2Admin.SendAsync(admin, HttpMethod.Post, "/admin/reset");
        Assert.Equal(HttpStatusCode.OK, resetStatus);
        Assert.Equal(OneRequest, Budget(await BudgetsAsync(admin), "global").GetProperty("spent_usd").GetDecimal());

        await CorrelationTestKit.LookupAsync(admin, await PublicRequestAsync(factory, "a second public request"));
        Assert.Equal(2 * OneRequest, Budget(await BudgetsAsync(admin), "global").GetProperty("spent_usd").GetDecimal());
    }

    [Fact]
    public async Task Renaming_a_budget_and_changing_its_period_and_cap_preserves_accrual_and_a_cap_below_spend_blocks_paid_work()
    {
        using var factory = new RestartableHybridFactory(RestartableHybridFactory.NewAuditPath());
        var admin = factory.CreateClient();
        await CorrelationTestKit.LookupAsync(admin, await PublicRequestAsync(factory));

        var (version, cfg, _) = await V2Admin.GetConfigAsync(admin);
        var edited = cfg with
        {
            Budgets = [new BudgetConfigV2 { Id = "appliance-total", Scope = "global", CapUsd = OneRequest / 2, Period = BudgetPeriod.None }],
        };
        await V2Admin.ApplyAsync(admin, edited, version, "rename + period + cap below spend");

        var renamed = Budget(await BudgetsAsync(admin), "appliance-total");
        Assert.Equal(OneRequest, renamed.GetProperty("spent_usd").GetDecimal());     // accrual kept across id/period/cap edits
        Assert.Equal("all", renamed.GetProperty("period_id").GetString());
        Assert.Equal(0m, renamed.GetProperty("remaining_usd").GetDecimal());
        Assert.Equal("exceeded", renamed.GetProperty("warning").GetString());

        // New paid work is refused at the budget stage; spend is kept; zero-priced local work still runs.
        using var app = factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo-key");
        using var paidReq = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = CorrelationTestKit.Chat("one more public request") };
        paidReq.Headers.Add("X-Covenant-Profile", "public-demo");
        using var paid = await app.SendAsync(paidReq);
        Assert.Equal(HttpStatusCode.Forbidden, paid.StatusCode);
        var refused = await CorrelationTestKit.LookupAsync(admin, CorrelationTestKit.HeaderId(paid));
        Assert.Equal("budget", refused.StoppedAtStage);
        Assert.Equal(1, factory.Cloud.Calls);

        using var local = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("an internal question"));
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
    }

    [Fact]
    public async Task Restart_replays_the_ledger_and_recovers_an_open_reservation_conservatively()
    {
        var auditPath = RestartableHybridFactory.NewAuditPath();
        try
        {
            using (var first = new RestartableHybridFactory(auditPath, cleanup: false))
            {
                var admin = first.CreateClient();
                await CorrelationTestKit.LookupAsync(admin, await PublicRequestAsync(first));
            }

            // Simulate a crash mid-attempt: a reservation with no settle/release in the journal.
            var store = new FileLedgerStore(auditPath + ".ledger.jsonl");
            store.Open();
            store.Append([new LedgerRecord
            {
                Op = LedgerOps.Reserve, RequestId = "req_crashed", AttemptId = "att_1", Scopes = ["global", "team:platform"],
                AmountUsd = 0.25m, TsUtc = DateTimeOffset.UtcNow,
            }]);

            using var second = new RestartableHybridFactory(auditPath, cleanup: false);
            var admin2 = second.CreateClient();
            var global = Budget(await BudgetsAsync(admin2), "global");
            Assert.Equal(OneRequest + 0.25m, global.GetProperty("spent_usd").GetDecimal());   // settled at the reserved amount
            Assert.Equal(0m, global.GetProperty("reserved_usd").GetDecimal());
            var events = await V2Admin.AuditAsync(auditPath, l => l.Any(e => e.EventType == AuditEntryKinds.BudgetRecovered));
            Assert.Contains(events, e => e.EventType == AuditEntryKinds.BudgetRecovered && e.Reason.Contains("1 reservation", StringComparison.Ordinal));
            Assert.Contains(File.ReadAllLines(auditPath + ".ledger.jsonl"), l => l.Contains("\"settle-recovered\"", StringComparison.Ordinal));
        }
        finally
        {
            CorrelationTestKit.DeleteWithSiblings(auditPath);
        }
    }

    [Fact]
    public async Task Without_a_ledger_the_current_audit_segment_spend_is_imported_once()
    {
        var auditPath = RestartableHybridFactory.NewAuditPath();
        try
        {
            using (var first = new RestartableHybridFactory(auditPath, cleanup: false))
            {
                var admin = first.CreateClient();
                await CorrelationTestKit.LookupAsync(admin, await PublicRequestAsync(first));
            }
            File.Delete(auditPath + ".ledger.jsonl");                      // a pre-T04 deployment: audit log, no ledger

            using (var second = new RestartableHybridFactory(auditPath, cleanup: false))
            {
                var admin = second.CreateClient();
                Assert.Equal(OneRequest, Budget(await BudgetsAsync(admin), "global").GetProperty("spent_usd").GetDecimal());
                var lines = File.ReadAllLines(auditPath + ".ledger.jsonl");
                Assert.Single(lines, l => l.Contains("\"legacy-import\"", StringComparison.Ordinal));
            }
            using (var third = new RestartableHybridFactory(auditPath, cleanup: false))
            {
                var admin = third.CreateClient();
                Assert.Equal(OneRequest, Budget(await BudgetsAsync(admin), "global").GetProperty("spent_usd").GetDecimal());   // not twice
            }
        }
        finally
        {
            CorrelationTestKit.DeleteWithSiblings(auditPath);
        }
    }
}
