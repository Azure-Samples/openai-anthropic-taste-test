namespace TasteTest;

public sealed class TasteTestOptions
{
    public const string SectionName = "TasteTest";

    public string FoundryEndpoint { get; set; } = string.Empty;

    public string FoundryResourceName { get; set; } = string.Empty;

    public string AoaiDeploymentName { get; set; } = "gpt-5.6-sol";

    public string ClaudeDeploymentName { get; set; } = "claude-opus-5";

    public int MaxOutputTokens { get; set; } = 900;

    public int MaxPromptCharacters { get; set; } = 4_000;

    public bool UseSampleResponses { get; set; }

    public string SystemPrompt { get; set; } =
        "Answer directly and thoughtfully. Prefer a clear structure, concrete examples, and no discussion of your identity or provider.";

    public string[] SeedPrompts { get; set; } =
    [
        "Explain eventual consistency to a product manager using one vivid analogy, then name where the analogy breaks.",
        "A team can ship a risky feature Friday or delay until Monday. Give the strongest case for each choice, then make the call.",
        "Rewrite this principle so it is memorable but not glib: reliability is a product feature, not an operations afterthought.",
        "Design a two-minute exercise that teaches senior engineers why good API naming matters."
    ];

    public static TasteTestOptions FromConfiguration(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<TasteTestOptions>() ?? new();

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

        if (int.TryParse(configuration["TASTE_TEST_MAX_OUTPUT_TOKENS"], out var maxOutputTokens))
        {
            options.MaxOutputTokens = maxOutputTokens;
        }

        options.FoundryEndpoint = options.FoundryEndpoint.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(options.FoundryResourceName) &&
            Uri.TryCreate(options.FoundryEndpoint, UriKind.Absolute, out var endpoint))
        {
            options.FoundryResourceName = endpoint.Host.Split('.')[0];
        }

        return options;
    }

    public void Validate()
    {
        if (MaxOutputTokens is < 128 or > 16_384)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxOutputTokens)} must be between 128 and 16384.");
        }

        if (MaxPromptCharacters is < 100 or > 100_000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxPromptCharacters)} must be between 100 and 100000.");
        }

        if (UseSampleResponses)
        {
            return;
        }

        if (!Uri.TryCreate(FoundryEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Set AZURE_FOUNDRY_ENDPOINT to the HTTPS endpoint emitted by azd provision.");
        }

        if (string.IsNullOrWhiteSpace(FoundryResourceName))
        {
            throw new InvalidOperationException(
                "Set AZURE_FOUNDRY_RESOURCE_NAME to the Foundry account subdomain.");
        }

        if (string.IsNullOrWhiteSpace(AoaiDeploymentName) ||
            string.IsNullOrWhiteSpace(ClaudeDeploymentName))
        {
            throw new InvalidOperationException(
                "Both AOAI_DEPLOYMENT_NAME and CLAUDE_DEPLOYMENT_NAME are required.");
        }
    }
}
