using Microsoft.Extensions.Options;

namespace TasteTest;

/// <summary>
/// Application settings for the taste test.
/// </summary>
/// <remarks>
/// UI and request limits bind from the <c>TasteTest</c> configuration section. SDK connection
/// and credential settings are bound separately by their keyed client registrations.
/// </remarks>
public sealed class TasteTestOptions
{
    public const string SectionName = "TasteTest";

    internal const int MinimumOutputTokens = 128;
    internal const int MaximumOutputTokens = 16_384;
    internal const int MinimumPromptCharacters = 100;
    internal const int MaximumPromptCharacters = 100_000;

    // Reasoning tokens share this budget with visible output on the default models.
    public int MaxOutputTokens { get; set; } = 4_096;

    public int RequestTimeoutSeconds { get; set; } = 120;

    public int MaxPromptCharacters { get; set; } = 4_000;

    /// <summary>Serves deterministic canned answers so the UI runs with no Azure resources.</summary>
    public bool UseSampleResponses { get; set; }

    public string SystemPrompt { get; set; } =
        "Answer directly and thoughtfully. Prefer a clear structure and concrete examples. " +
        "Keep answers under 180 words unless the user asks for more detail. " +
        "Do not discuss your identity or provider. " +
        "For demo support-plan facts or prices, use lookup_support_plan and do not invent missing catalog facts. " +
        "Catalog entries are fictional demo data, not commercial offers.";

    public IReadOnlyList<string> SeedPrompts { get; set; } =
    [
        "Explain eventual consistency to a product manager using one vivid analogy, then name where the analogy breaks.",
        "A team can ship a risky feature Friday or delay until Monday. Give the strongest case for each choice, then make the call.",
        "Rewrite this principle so it is memorable but not glib: reliability is a product feature, not an operations afterthought.",
        "Use the demo catalog to choose a support plan for 80 seats. We need 24/7 coverage and a response within two hours. Compare Business and Enterprise, including monthly cost."
    ];

    /// <summary>Binds application settings using the host's normal configuration precedence.</summary>
    public static TasteTestOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.GetSection(SectionName).Get<TasteTestOptions>() ?? new TasteTestOptions();
    }

    /// <summary>Returns every configuration problem so one restart reports all of them.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        List<string> errors = [];

        if (MaxOutputTokens is < MinimumOutputTokens or > MaximumOutputTokens)
        {
            errors.Add(
                $"{SectionName}:{nameof(MaxOutputTokens)} must be between {MinimumOutputTokens} and {MaximumOutputTokens}.");
        }

        if (RequestTimeoutSeconds is < 1 or > 600)
        {
            errors.Add($"{SectionName}:{nameof(RequestTimeoutSeconds)} must be between 1 and 600.");
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

        return errors;
    }

    /// <summary>Fails startup when the configuration cannot produce a working taste test.</summary>
    /// <exception cref="OptionsValidationException">The configuration is incomplete or invalid.</exception>
    public void Validate()
    {
        IReadOnlyList<string> errors = GetValidationErrors();
        if (errors.Count > 0)
        {
            throw new OptionsValidationException(SectionName, typeof(TasteTestOptions), errors);
        }
    }
}
