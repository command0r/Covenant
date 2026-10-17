using System.Net;
using System.Net.Http.Headers;
using Covenant.Adapters;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>ADR-0011 isolation: every request binds to ONE configuration version. A request in flight on v1 keeps
/// v1's policy and prices while v2 is applied; the next request sees v2. Retired snapshots dispose their provider
/// clients only after their last in-flight request releases them.</summary>
public sealed class SnapshotIsolationTests : IDisposable
{
    private readonly HybridSeedFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task In_flight_request_keeps_v1_policy_and_prices_while_v2_is_applied()
    {
        var admin = _factory.CreateClient();
        using var app = _factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo-key");
        app.DefaultRequestHeaders.Add("X-Covenant-Profile", "public-demo");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Cloud.Gate = gate;
        var first = app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("Summarize this public product announcement."));
        await _factory.Cloud.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));   // provider call genuinely in flight on v1

        // v2: cloud price ×100 AND the Public policy paused.
        var (_, v1, _) = await V2Admin.GetConfigAsync(admin);
        var v2 = v1 with
        {
            Connections = v1.Connections.Select(c => c.Id != "openai" ? c : c with
            {
                Models = c.Models.Select(m => m.ModelId != V2.CloudModel ? m : m with
                {
                    Price = new PriceConfig { InputPer1M = 15m, OutputPer1M = 60m, Basis = "contract" },
                }).ToList(),
            }).ToList(),
            Policies = v1.Policies.Select(p => p.Id == "public" ? p with { Paused = true } : p).ToList(),
        };
        Assert.Equal(2, await V2Admin.ApplyAsync(admin, v2, 1, "reprice + pause public"));

        gate.SetResult();
        using var firstResp = await first;
        Assert.Equal(HttpStatusCode.OK, firstResp.StatusCode);
        var firstEntry = await CorrelationTestKit.LookupAsync(admin, CorrelationTestKit.HeaderId(firstResp));
        Assert.Equal(1, firstEntry.ConfigVersion);
        Assert.Equal(V2.CloudModel, firstEntry.ServedByModel);
        Assert.Equal(1000 / 1_000_000m * 0.15m + 1000 / 1_000_000m * 0.60m, firstEntry.Usage.CostUsd);   // v1 price, not v2's

        using var second = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("Summarize this public product announcement."));
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        var secondEntry = await CorrelationTestKit.LookupAsync(admin, CorrelationTestKit.HeaderId(second));
        Assert.Equal(2, secondEntry.ConfigVersion);
        Assert.Contains("paused", secondEntry.Reason, StringComparison.Ordinal);
        Assert.Equal(1, _factory.Cloud.Calls);
    }

    [Fact]
    public void Retired_snapshot_disposes_its_clients_only_after_the_last_request_releases_it()
    {
        var oldClient = new GatedChatClient();
        var v1 = new RuntimeSnapshot(V2.Hybrid(), new ProviderClientSet(new Dictionary<string, MEAI.IChatClient> { ["ollama:" + V2.LocalTag] = oldClient }));
        var holder = new SnapshotHolder(v1);

        var inFlight = holder.Acquire();
        Assert.Same(v1, inFlight);
        Assert.Equal(2, v1.RefCount);

        var v2 = new RuntimeSnapshot(V2.Hybrid(version: 2));
        holder.Swap(v2);
        Assert.Same(v2, holder.Current);
        Assert.False(v1.IsDisposed);              // still used by the in-flight request
        Assert.False(oldClient.Disposed);
        Assert.True(inFlight.GetResource<IChatClientRegistry>()!.TryResolve("ollama", V2.LocalTag, out _));

        inFlight.Release();
        Assert.True(v1.IsDisposed);
        Assert.True(oldClient.Disposed);

        var next = holder.Acquire();
        Assert.Same(v2, next);
        next.Release();
        Assert.False(v2.IsDisposed);             // the holder still owns the current snapshot
    }

    [Fact]
    public async Task Concurrent_acquire_and_swap_never_hand_out_a_disposed_snapshot()
    {
        var holder = new SnapshotHolder(new RuntimeSnapshot(V2.Hybrid()));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        int handedOutDisposed = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var s = holder.Acquire();
                if (s.IsDisposed) Interlocked.Increment(ref handedOutDisposed);
                s.Release();
            }
        })).ToList();
        int version = 1;
        while (!stop.IsCancellationRequested)
            holder.Swap(new RuntimeSnapshot(V2.Hybrid(version: ++version)));
        await Task.WhenAll(readers);
        Assert.Equal(0, handedOutDisposed);
    }
}
