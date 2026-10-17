using Covenant.Ui.Logic;
using Covenant.Ui.Models;
using Xunit;

namespace Covenant.Tests;

public sealed class CloudSetupUsabilityTests
{
    [Fact]
    public void New_cloud_connection_offers_standard_reference_without_an_existing_connection()
    {
        var form = ConnectionForm.NewCloud("OpenAi", []);
        Assert.Equal("OpenAI:ApiKey", form.SecretRef);
        Assert.Contains(ConnectionRules.SecretChoices(null), c => c.Ref == form.SecretRef);
        Assert.All(ConnectionRules.SecretChoices(null), c => Assert.False(c.Available));
    }

    [Fact]
    public void Provider_change_updates_standard_reference_but_preserves_custom_reference()
    {
        var form = ConnectionForm.NewCloud("OpenAi", []);
        form.SetKind("Anthropic");
        Assert.Equal("Anthropic:ApiKey", form.SecretRef);
        form.SecretRef = "Secrets:team-provider";
        form.SetKind("OpenAiCompatible");
        Assert.Equal("Secrets:team-provider", form.SecretRef);
    }

    [Fact]
    public void Standard_reference_readiness_comes_from_server_and_choices_are_not_duplicated()
    {
        var choices = ConnectionRules.SecretChoices(new Dictionary<string, SecretAvailabilityDto>
        {
            ["OpenAI:ApiKey"] = new() { Available = true },
            ["Secrets:custom"] = new() { Available = false },
        });
        Assert.Single(choices, c => c.Ref == "OpenAI:ApiKey");
        Assert.True(choices.Single(c => c.Ref == "OpenAI:ApiKey").Available);
        Assert.False(choices.Single(c => c.Ref == "Secrets:custom").Available);
    }
}
