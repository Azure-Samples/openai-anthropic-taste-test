using System.Globalization;
using Microsoft.Extensions.Options;

namespace TasteTest;

/// <summary>
/// Application settings for the taste test.
/// </summary>
/// <remarks>
/// Values bind from the <c>TasteTest</c> configuration section and are then overridden by the
/// flat environment variables that <c>azd</c> writes, so a provisioned environment needs no
/// appsettings edits.
/// </remarks>
public sealed class TasteTestOptions
{
    public const string SectionName = "TasteTest";

    internal const int MinimumOutputTokens = 128;
    internal const int MaximumOutputTokens = 16_384;
    internal const int MinimumPromptCharacters = 100;
    internal const int MaximumPromptCharacters = 100_000;

    /// <summary>Foundry account endpoint, for example <c>https://my-account.services.ai.azure.com</c>.</summary>
    public string FoundryEndpoint { get; set; } = string.Empty;

    /// <summary>Foundry account subdomain. Derived from <see cref="FoundryEndpoint"/> when omitted.</summary>
    public string FoundryResourceName { get; set; } = string.Empty;

    public string AoaiDeploymentName { get; set; } = "gpt-5.6-sol";

    public string ClaudeDeploymentName { get; set; } = "claude-opus-5";

    public int MaxOutputTokens { get; set; } = 900;

    public int MaxPromptCharacters { get; set; } = 4_000;

    /// <summary>Serves deterministic canned answers so the UI runs with no Azure resources.</summary>
    public bool UseSampleResponses { get; set; }

    public string SystemPrompt { get; set; } =
        "Answer directly and thoughtfully. Prefer a clear structure, concrete examples, and no discussion of your identity or provider.";

    public IReadOnlyList<string> SeedPrompts { get; set; } =
    [
        "Explain eventual consistency to a product manager using one vivid analogy, then name where the analogy breaks.",
        "A team can ship a risky feature Friday or delay until Monday. Give the strongest case for each choice, then make the call.",
        "Rewrite this principle so it is memorable but not glib: reliability is a product feature, not an operations afterthought.",
        "Design a two-minute exercise that teaches senior engineers why good API naming matters."
    ];

    /// <summary>Binds the options section and applies the flat variables written by <c>azd</c>.</summary>
    public static TasteTestOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(SectionName).Get<TasteTestOptions>() ?? new TasteTestOptions();

        options.FoundryEndpoint =
            configuration["AZURE_FOUNDRY_ENDPOINT"] ?? options.FoundryEndpoint;
        options.FoundryResourceName =
            configuration["AZURE_FOUNDRY_RESOURCE_NAME"] ?? options.FoundryResourceName;
        options.AoaiDeploymentName =
            configuration["AOAI_DEPLOYMENT_NAME"] ?? options.AoaiDeploymentName;
        options.ClaudeDeploymentName =
            configuration["CLAUDE_DEPLOYMENT_NAME"] ?? options.ClaudeDeploymentName;

        if (bool.TryParse(configuration["TASTE_TEST_USE_SAMPLE_RESPONSES"], out var useSamples))
        {
            options.UseSampleResponses = useSamples;
        }

        if (int.TryParse(
                configuration["TASTE_TEST_MAX_OUTPUT_TOKENS"],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var maxOutputTokens))
        {
            options.MaxOutputTokens = maxOutputTokens;
        }

        options.FoundryEndpoint = options.FoundryEndpoint.Trim().TrimEnd('/');

        if (string.IsNullOrWhiteSpace(options.FoundryResourceName) &&
            Uri.TryCreate(options.FoundryEndpoint, UriKind.Absolute, out var endpoint))
        {
            options.FoundryResourceName = endpoint.Host.Split('.')[0];
        }

        return options;
    }

    /// <summary>Returns every configuration problem so one restart reports all of them.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        if (MaxOutputTokens is < MinimumOutputTokens or > MaximumOutputTokens)
        {
            errors.Add(
                $"{SectionName}:{nameof(MaxOutputTokens)} must be between {MinimumOutputTokens} and {MaximumOutputTokens}.");
        }

        if (MaxPromptCharacters is < MinimumPromptCharacters or > MaximumPromptCharacters)
        {
            errors.Add(
                $"{SectionName}:{nameof(MaxPromptCharacters)} must be between {MinimumPromptCharacters} and {MaximumPromptCharacters}.");
        }

        if (SeedPrompts.Count == 0)
        {
            errors.Add($"{SectionName}:{nameof(SeedPrompts)} must contain at least one prompt.");
        }

        // Sample mode never contacts Azure, so the Foundry settings stay optional there.
        if (UseSampleResponses)
        {
            return errors;
        }

        if (!Uri.TryCreate(FoundryEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("Set AZURE_FOUNDRY_ENDPOINT to the HTTPS endpoint emitted by azd provision.");
        }

        if (string.IsNullOrWhiteSpace(FoundryResourceName))
        {
            errors.Add("Set AZURE_FOUNDRY_RESOURCE_NAME to the Foundry account subdomain.");
        }

        if (string.IsNullOrWhiteSpace(AoaiDeploymentName))
        {
            errors.Add("Set AOAI_DEPLOYMENT_NAME to the OpenAI model deployment name.");
        }

        if (string.IsNullOrWhiteSpace(ClaudeDeploymentName))
        {
            errors.Add("Set CLAUDE_DEPLOYMENT_NAME to the Anthropic model deployment name.");
        }

        return errors;
    }

    /// <summary>Fails startup when the configuration cannot produce a working taste test.</summary>
    /// <exception cref="OptionsValidationException">The configuration is incomplete or invalid.</exception>
    public void Validate()
    {
        var errors = GetValidationErrors();
        if (errors.Count > 0)
        {
            throw new OptionsValidationException(SectionName, typeof(TasteTestOptions), errors);
        }
    }
}
