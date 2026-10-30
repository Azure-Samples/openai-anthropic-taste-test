using Microsoft.Extensions.AI;

namespace TasteTest.Services;

/// <summary>
/// Builds the provider metadata revealed after a vote from the registered client's model.
/// </summary>
internal static class ModelCatalog
{
    public static ModelIdentity CreateIdentity(ProviderKind provider, IChatClient client) =>
        provider switch
        {
            ProviderKind.OpenAI => new ModelIdentity(
                Provider: "OpenAI",
                ModelId: ModelId(client),
                Protocol: "Responses API",
                Sdk: "OpenAI SDK for .NET"),
            ProviderKind.Anthropic => new ModelIdentity(
                Provider: "Anthropic",
                ModelId: ModelId(client),
                Protocol: "Messages API",
                Sdk: "Anthropic C# SDK"),
            _ => throw UnknownProvider(provider)
        };

    private static string ModelId(IChatClient client) =>
        client.GetService<ChatClientMetadata>()?.DefaultModelId ??
        throw new InvalidOperationException("The registered chat client must declare its configured model.");

    public static ArgumentOutOfRangeException UnknownProvider(
        ProviderKind provider,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(provider))] string? parameterName = null) =>
        new(parameterName, provider, "Unknown provider.");
}
