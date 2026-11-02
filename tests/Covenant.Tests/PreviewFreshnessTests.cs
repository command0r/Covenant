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
}
