using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace TasteTest.Tests;

public sealed class TasteTestOptionsTests
{
    [Fact]
    public void Defaults_LeaveRoomForReasoningAndBoundRequestDuration()
    {
        var options = new TasteTestOptions();
        Assert.Equal(4096, options.MaxOutputTokens);
        Assert.Equal(120, options.RequestTimeoutSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void Validate_RejectsUnboundedRequestTimeout(int seconds)
    {
        var options = new TasteTestOptions { UseSampleResponses = true, RequestTimeoutSeconds = seconds };
        Assert.Throws<OptionsValidationException>(options.Validate);
    }

    [Fact]
    public void FromConfiguration_UsesAzdEnvironmentVariables()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TASTE_TEST_MAX_OUTPUT_TOKENS"] = "512"
            })
            .Build();

        var options = TasteTestOptions.FromConfiguration(configuration);

        Assert.Equal(512, options.MaxOutputTokens);
    }

    [Fact]
    public void FromConfiguration_PrefersEnvironmentVariablesOverSectionValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TasteTestOptions.SectionName}:MaxOutputTokens"] = "4096",
                [$"{TasteTestOptions.SectionName}:MaxPromptCharacters"] = "1234",
                ["TASTE_TEST_MAX_OUTPUT_TOKENS"] = "512"
            })
            .Build();

        var options = TasteTestOptions.FromConfiguration(configuration);

        Assert.Equal(512, options.MaxOutputTokens);
        Assert.Equal(1234, options.MaxPromptCharacters);
    }

    [Fact]
    public void Validate_ReportsEveryProblemAtOnce()
    {
        var options = new TasteTestOptions
        {
            MaxOutputTokens = 1,
            RequestTimeoutSeconds = 0,
            MaxPromptCharacters = 1
        };

        var errors = options.GetValidationErrors();

        Assert.Contains(errors, error => error.Contains(nameof(TasteTestOptions.MaxOutputTokens), StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(nameof(TasteTestOptions.RequestTimeoutSeconds), StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(nameof(TasteTestOptions.MaxPromptCharacters), StringComparison.Ordinal));

        var exception = Assert.Throws<OptionsValidationException>(options.Validate);
        Assert.Contains(nameof(TasteTestOptions.RequestTimeoutSeconds), exception.Message, StringComparison.Ordinal);
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
    public void Validate_OnlyValidatesApplicationLimits()
    {
        TasteTestOptions options = new();

        options.Validate();
    }

    [Theory]
    [InlineData("TASTE_TEST_USE_SAMPLE_RESPONSES", "not-a-boolean")]
    [InlineData("TASTE_TEST_MAX_OUTPUT_TOKENS", "not-a-number")]
    public void FromConfiguration_RejectsMalformedOverrides(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => TasteTestOptions.FromConfiguration(configuration));
        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }
}
