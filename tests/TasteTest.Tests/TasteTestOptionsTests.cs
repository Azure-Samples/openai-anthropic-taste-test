using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

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
    public void FromConfiguration_PrefersEnvironmentVariablesOverSectionValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TasteTestOptions.SectionName}:AoaiDeploymentName"] = "from-section",
                [$"{TasteTestOptions.SectionName}:MaxPromptCharacters"] = "1234",
                ["AOAI_DEPLOYMENT_NAME"] = "from-environment"
            })
            .Build();

        var options = TasteTestOptions.FromConfiguration(configuration);

        Assert.Equal("from-environment", options.AoaiDeploymentName);
        Assert.Equal(1234, options.MaxPromptCharacters);
    }

    [Fact]
    public void Validate_ReportsEveryProblemAtOnce()
    {
        var options = new TasteTestOptions
        {
            MaxOutputTokens = 1,
            ClaudeDeploymentName = string.Empty
        };

        var errors = options.GetValidationErrors();

        Assert.Contains(errors, error => error.Contains(nameof(TasteTestOptions.MaxOutputTokens), StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("AZURE_FOUNDRY_ENDPOINT", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("CLAUDE_DEPLOYMENT_NAME", StringComparison.Ordinal));

        var exception = Assert.Throws<OptionsValidationException>(options.Validate);
        Assert.Contains("AZURE_FOUNDRY_ENDPOINT", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AllowsLocalSampleModeWithoutAzure()
    {
        var options = new TasteTestOptions
        {
            UseSampleResponses = true
        };

        options.Validate();

        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void Validate_AcceptsProvisionedConfiguration()
    {
        var options = new TasteTestOptions
        {
            FoundryEndpoint = "https://sample.services.ai.azure.com",
            FoundryResourceName = "sample"
        };

        options.Validate();
    }
}
