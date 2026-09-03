using Microsoft.Extensions.Configuration;

namespace TasteTest.Tests;

public sealed class TasteTestOptionsTests
{
    [Fact]
    public void FromConfiguration_UsesAzdEnvironmentVariables()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZURE_FOUNDRY_ENDPOINT"] = "https://sample.services.ai.azure.com/",
                ["AOAI_DEPLOYMENT_NAME"] = "gpt-test",
                ["CLAUDE_DEPLOYMENT_NAME"] = "claude-test",
                ["TASTE_TEST_MAX_OUTPUT_TOKENS"] = "512"
            })
            .Build();

        var options = TasteTestOptions.FromConfiguration(configuration);

        Assert.Equal("https://sample.services.ai.azure.com", options.FoundryEndpoint);
        Assert.Equal("sample", options.FoundryResourceName);
        Assert.Equal("gpt-test", options.AoaiDeploymentName);
        Assert.Equal("claude-test", options.ClaudeDeploymentName);
        Assert.Equal(512, options.MaxOutputTokens);
    }

    [Fact]
    public void Validate_RequiresEndpointOutsideSampleMode()
    {
        var options = new TasteTestOptions();

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("AZURE_FOUNDRY_ENDPOINT", exception.Message);
    }

    [Fact]
    public void Validate_AllowsLocalSampleModeWithoutAzure()
    {
        var options = new TasteTestOptions
        {
            UseSampleResponses = true
        };

        options.Validate();
    }
}
