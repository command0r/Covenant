using Covenant.Ui.Logic;
using Covenant.Ui.Models;
using Xunit;

namespace Covenant.Tests;

public sealed class PreviewFreshnessTests
{
    [Fact]
    public void Changing_policy_configuration_invalidates_preview_even_when_prompt_is_unchanged()
    {
        var form = new PlaygroundForm { ApplicationId = "demo-app", ProfileId = "public-demo", Prompt = "Hello" };
        var preview = new RoutePreview(RoutePreview.KeyOf(form), new SimulateResponseDto { ConfigVersion = 3 }, null, false);
        Assert.True(preview.IsCurrent(form, 3));
        Assert.False(preview.IsCurrent(form, 4));
        form.Prompt = "Different request";
        Assert.False(preview.IsCurrent(form, 3));
    }

    [Fact]
    public void Switching_workload_makes_the_old_preview_stale_and_the_form_still_previewable_with_the_same_prompt()
    {
        // The Internal/Public comparison: the prompt is kept, only the workload changes. The old dry run no longer
        // describes the form, and the form still holds everything a new dry run needs — so the switch re-previews.
        var form = new PlaygroundForm { ApplicationId = "demo-app", ProfileId = "internal-default", Prompt = "SSN on file: 000-12-3456" };
        var preview = new RoutePreview(RoutePreview.KeyOf(form), new SimulateResponseDto { ConfigVersion = 4 }, null, false);
        form.ProfileId = "public-demo";
        Assert.False(preview.IsCurrent(form, 4));
        Assert.True(RoutePreview.CanPreview(form));
        Assert.Equal("SSN on file: 000-12-3456", form.Prompt);
    }

    [Theory]
    [InlineData(null, "Hello", false)]
    [InlineData("", "Hello", false)]
    [InlineData("demo-app", "", false)]
    [InlineData("demo-app", "   ", false)]
    [InlineData("demo-app", "Hello", true)]
    public void A_dry_run_needs_a_sender_and_a_prompt(string? application, string prompt, bool expected)
        => Assert.Equal(expected, RoutePreview.CanPreview(new PlaygroundForm { ApplicationId = application, Prompt = prompt }));
}
