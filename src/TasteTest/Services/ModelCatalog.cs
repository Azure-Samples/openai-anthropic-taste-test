namespace TasteTest.Services;

/// <summary>
/// Builds the provider metadata revealed after a vote. Both the Foundry and sample
/// factories share this so the reveal panel cannot drift between them.
/// </summary>
internal static class ModelCatalog
{
    public static ModelIdentity CreateIdentity(ProviderKind provider, TasteTestOptions options) =>
        provider switch
        {
            ProviderKind.OpenAI => new ModelIdentity(
                Provider: "OpenAI",
                ModelId: options.AoaiDeploymentName,
                Protocol: "Responses API",
                Sdk: "OpenAI SDK for .NET"),
            ProviderKind.Anthropic => new ModelIdentity(
                Provider: "Anthropic",
                ModelId: options.ClaudeDeploymentName,
                Protocol: "Messages API",
                Sdk: "Anthropic C# SDK"),
            _ => throw UnknownProvider(provider)
        };

    public static ArgumentOutOfRangeException UnknownProvider(
        ProviderKind provider,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(provider))] string? parameterName = null) =>
        new(parameterName, provider, "Unknown provider.");
}
