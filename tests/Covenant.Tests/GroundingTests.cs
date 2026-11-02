using System.Text;
using Covenant.Core;
using Covenant.Governance;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>Grounded-answer correctness (ADR-0015 follow-up): the query keeps the user's own intent; the provider receives
/// ONE system message (portable layout) with the current time, the place to answer for and the retrieval-vs-observation
/// distinction, and the question verbatim after the untrusted data block — identically on streamed and buffered paths.
/// The reservation bound covers the worst-case grounding.</summary>
public sealed class GroundingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-17T01:30:00Z");

    // ---------------------------------------------------------------- query intent

    [Fact]
    public void A_weather_question_about_now_gets_todays_date_appended_and_keeps_its_words()
    {
        var q = WebSearchPlanner.BuildSearchQuery("What's the weather in Toronto right now, in Celsius?", null, Now);
        Assert.StartsWith("What's the weather in Toronto right now, in Celsius?", q, StringComparison.Ordinal);
        Assert.EndsWith("October 17 2026", q, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Will it rain in Toronto tomorrow?")]
    [InlineData("Toronto weather forecast for next week")]
    [InlineData("What was the weather in Toronto on 2026-10-01?")]
    [InlineData("Weekend forecast for Montreal")]
    [InlineData("Average October temperature in Toronto")]
    public void A_question_naming_its_own_period_is_never_redefined_as_today(string question)
        => Assert.Equal(WebSearchPlanner.BuildQuery(question, null), WebSearchPlanner.BuildSearchQuery(question, null, Now));

    [Fact]
    public void Site_filters_units_and_the_explicit_location_are_preserved()
    {
        var q = WebSearchPlanner.BuildSearchQuery("site:weather.gc.ca current weather in Celsius", "Toronto", Now);
        Assert.Contains("site:weather.gc.ca", q, StringComparison.Ordinal);
        Assert.Contains("Celsius", q, StringComparison.Ordinal);
        Assert.Contains("Toronto", q, StringComparison.Ordinal);
        Assert.Equal("Latest .NET release notes", WebSearchPlanner.BuildSearchQuery("Latest .NET release notes", null, Now));   // not weather: untouched
    }

    // ---------------------------------------------------------------- grounding content

    private static readonly WebSource Snip = new("Toronto forecast", "https://weather.example.org/toronto", "Thursday October 16: rain, high 12.", DateTimeOffset.Parse("2026-10-17T01:29:00Z"));

    [Fact]
    public void Grounding_states_time_place_and_that_retrieval_is_not_observation()
    {
        var original = new List<ChatMessage> { new(ChatRole.System, "Answer briefly."), new(ChatRole.User, "What's the weather today?") };
        var g = WebSearchGrounding.Ground(original, [Snip], Now, Snip.RetrievedUtc, "Toronto", null, weather: true);

        Assert.Equal(2, g.Count);
        Assert.Single(g, m => m.Role == ChatRole.System);                                    // exactly one system message
        var sys = g[0].Content;
        Assert.Contains("ALREADY searched", sys, StringComparison.Ordinal);
        Assert.Contains("Do not say that you cannot browse", sys, StringComparison.Ordinal);
        Assert.Contains("Saturday 2026-10-17 01:30 UTC", sys, StringComparison.Ordinal);    // time, weekday — not only a date
        Assert.Contains("local date and time at the place asked about can differ", sys, StringComparison.Ordinal);
        Assert.Contains("Search location field): Toronto", sys, StringComparison.Ordinal);
        Assert.Contains("NOT when the page's information was observed", sys, StringComparison.Ordinal);
        Assert.Contains("Never invent readings", sys, StringComparison.Ordinal);
        Assert.Contains("Retrieved is not verified", sys, StringComparison.Ordinal);
        Assert.EndsWith("Application instructions:\nAnswer briefly.", sys, StringComparison.Ordinal);   // app system text kept, after ours

        Assert.Equal(ChatRole.User, g[1].Role);
        Assert.Contains("(retrieval time, not the page's observation date)", g[1].Content, StringComparison.Ordinal);
        Assert.EndsWith(WebSearchGrounding.QuestionHeader + "\nWhat's the weather today?", g[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_place_named_in_the_prompt_is_stated_as_such_and_nothing_is_inferred()
    {
        var original = new List<ChatMessage> { new(ChatRole.User, "Weather in Ottawa today?") };
        var named = WebSearchGrounding.Ground(original, [Snip], Now, Snip.RetrievedUtc, null, "Ottawa", weather: true);
        Assert.Contains("named in the user's question): Ottawa", named[0].Content, StringComparison.Ordinal);

        var none = WebSearchGrounding.Ground([new(ChatRole.User, "Latest .NET release?")], [Snip], Now, Snip.RetrievedUtc, null, null, weather: false);
        Assert.DoesNotContain("Place to answer for", none[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("For weather", none[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Earlier_turns_stay_in_order_and_only_the_last_user_turn_carries_the_sources()
    {
        var original = new List<ChatMessage>
        {
            new(ChatRole.User, "Hi"), new(ChatRole.Assistant, "Hello"), new(ChatRole.User, "Weather in Toronto now?"),
        };
        var g = WebSearchGrounding.Ground(original, [Snip], Now, Snip.RetrievedUtc, null, "Toronto", weather: true);
        Assert.Equal(4, g.Count);
        Assert.Same(original[0], g[1]);
        Assert.Same(original[1], g[2]);
        Assert.DoesNotContain(WebSearchGrounding.BlockStart, g[1].Content, StringComparison.Ordinal);
        Assert.StartsWith(WebSearchGrounding.BlockStart, g[3].Content, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- reservation bound

    private static long Bound(IReadOnlyList<ChatMessage> messages) => RequestBounds.InputTokenBound(messages);

    [Fact]
    public void The_reserved_grounding_bound_covers_worst_case_content()
    {
        // Worst case: 3-byte characters at every cap (titles, snippets, location), the longest URL, the longest weekday,
        // weather wording, an application system message to merge, and a place named in the prompt.
        string Fill(int n) => new('€', n);
        var url = "https://example.org/" + new string('a', WebSources.MaxUrlChars - "https://example.org/".Length);
        var sources = Enumerable.Range(0, 3)
            .Select(_ => WebSources.Validate([new WebSource(Fill(900), url, Fill(5000), DateTimeOffset.Parse("2026-09-30T23:59:00Z"))], 1)[0])
            .ToList();
        var original = new List<ChatMessage> { new(ChatRole.System, Fill(50)), new(ChatRole.User, "Weather in " + Fill(100) + " now?") };
        var wednesday = DateTimeOffset.Parse("2026-09-30T23:59:00Z");

        foreach (var (location, place) in new[] { (Fill(WebSearchPlanner.MaxLocationChars), (string?)null), ((string?)null, Fill(WebSearchPlanner.MaxLocationChars)) })
        {
            var grounded = WebSearchGrounding.Ground(original, sources, wednesday, wednesday, location, place, weather: true);
            long added = Bound(grounded) - Bound(original);
            Assert.True(added <= WebSearchGrounding.MaxBytes(3), $"grounding added {added} bytes > reserved {WebSearchGrounding.MaxBytes(3)}");
        }
        Assert.True(WebSearchGrounding.MaxBytes(1) < WebSearchGrounding.MaxBytes(3));
    }

    // ---------------------------------------------------------------- exact provider messages (streamed and buffered)

    private static CovenantConfig Cfg() => V2.Hybrid(tweak: c => c with
    {
        WebSearch = new WebSearchSettings { Enabled = true, PricePerCreditUsd = 0.008m, MaxResults = 3, TimeoutSeconds = 5 },
        Profiles = c.Profiles.Select(p => p.Id == "public-demo" ? p with { WebSearchAllowed = true } : p).ToList(),
    });

    private static (V2Rig Rig, StubChatClient Cloud, StubSearchClient Search) Rig()
    {
        var cloud = new StubChatClient("answer [1]");
        var search = new StubSearchClient();
        var rig = V2Rig.Build(Cfg(), new Dictionary<string, MEAI.IChatClient>
        {
            [$"openai:{V2.CloudModel}"] = cloud,
            [$"ollama:{V2.LocalTag}"] = new StubChatClient("local"),
        }, search: search);
        return (rig, cloud, search);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_location_reaches_the_query_and_the_model(bool stream)
    {
        var (rig, cloud, search) = Rig();
        var (ctx, _) = await rig.RunAsync("What's the weather today?", profile: "public-demo", stream: stream,
            webSearch: new WebSearchRequest(WebSearchMode.Required, "Toronto"));
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Contains("Toronto", search.LastQuery!.Query, StringComparison.Ordinal);
        var sent = cloud.LastMessages;
        Assert.Equal(2, sent.Count);
        Assert.Equal(MEAI.ChatRole.System, sent[0].Role);
        Assert.Contains("Search location field): Toronto", sent[0].Text, StringComparison.Ordinal);
        Assert.EndsWith(WebSearchGrounding.QuestionHeader + "\nWhat's the weather today?", sent[1].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_place_in_the_prompt_is_used_without_a_location_field(bool stream)
    {
        var (rig, cloud, search) = Rig();
        var (ctx, _) = await rig.RunAsync("What's the weather in Ottawa today?", profile: "public-demo", stream: stream,
            webSearch: new WebSearchRequest(WebSearchMode.Auto));
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Contains("Ottawa", search.LastQuery!.Query, StringComparison.Ordinal);
        Assert.Contains("named in the user's question): Ottawa", cloud.LastMessages[0].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_city_is_refused_before_any_external_call(bool stream)
    {
        var (rig, cloud, search) = Rig();
        var (ctx, _) = await rig.RunAsync("What's the weather today?", profile: "public-demo", stream: stream,
            webSearch: new WebSearchRequest(WebSearchMode.Auto));
        Assert.Equal(TraceReasons.LocationRequired, ctx.DenialCode);
        Assert.Equal(0, search.Calls);
        Assert.Equal(0, cloud.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Off_and_untriggered_auto_send_the_original_messages_unchanged(bool stream)
    {
        var (rig, cloud, search) = Rig();
        await rig.RunAsync("What's the weather in Toronto today?", profile: "public-demo", stream: stream, webSearch: new WebSearchRequest(WebSearchMode.Off, "Toronto"));
        Assert.Equal("What's the weather in Toronto today?", Assert.Single(cloud.LastMessages).Text);   // location is search-only: Off ignores it
        await rig.RunAsync("Explain photosynthesis briefly.", profile: "public-demo", stream: stream, webSearch: new WebSearchRequest(WebSearchMode.Auto, "Toronto"));
        Assert.Equal("Explain photosynthesis briefly.", Assert.Single(cloud.LastMessages).Text);
        Assert.Equal(0, search.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sensitive_location_is_denied_before_egress(bool stream)
    {
        var (rig, cloud, search) = Rig();
        var (ctx, _) = await rig.RunAsync("What's the weather today?", profile: "public-demo", stream: stream,
            webSearch: new WebSearchRequest(WebSearchMode.Required, "SSN 000-12-3456"));
        Assert.Equal(TraceReasons.SearchSensitive, ctx.DenialCode);
        Assert.Equal(0, search.Calls);
        Assert.Equal(0, cloud.Calls);
    }
}
