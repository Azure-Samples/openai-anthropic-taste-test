using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace TasteTest.Services;

public sealed class SampleModelChatClientFactory : IModelChatClientFactory, IDisposable
{
    private readonly SampleChatClient _openAI;
    private readonly SampleChatClient _anthropic;
    private readonly TasteTestOptions _options;

    public SampleModelChatClientFactory(IOptions<TasteTestOptions> options)
    {
        _options = options.Value;
        _openAI = new SampleChatClient(
            "The strongest explanation starts with a boundary: distributed systems trade immediate agreement for availability. " +
            "Picture several store clerks updating separate copies of the same inventory sheet. They can keep serving customers during a network outage, " +
            "then reconcile their sheets afterward. The analogy breaks because software reconciliation is rule-driven and can preserve exact history; human clerks often cannot.");
        _anthropic = new SampleChatClient(
            "Think of eventual consistency as a group chat during a flight. Each phone briefly shows a different latest message, but once connectivity returns, " +
            "everyone converges on the same thread. That framing makes the benefit tangible: progress continues without a central pause. It breaks down when conflicts " +
            "carry business meaning, because databases need explicit merge rules rather than simply sorting messages.");
    }

    public IChatClient GetClient(ProviderKind provider) =>
        provider switch
        {
            ProviderKind.OpenAI => _openAI,
            ProviderKind.Anthropic => _anthropic,
            _ => throw ModelCatalog.UnknownProvider(provider)
        };

    public ModelIdentity GetIdentity(ProviderKind provider) =>
        ModelCatalog.CreateIdentity(provider, _options);

    public void Dispose()
    {
        _openAI.Dispose();
        _anthropic.Dispose();
    }

    private sealed class SampleChatClient(string response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, response)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in Chunk(response, 18))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(22, cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }

        private static IEnumerable<string> Chunk(string text, int size)
        {
            for (var index = 0; index < text.Length; index += size)
            {
                yield return text.Substring(index, Math.Min(size, text.Length - index));
            }
        }
    }
}
