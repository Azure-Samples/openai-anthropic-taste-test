using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace TasteTest.Services;

public sealed class SampleModelChatClientFactory : IModelChatClientFactory, IDisposable
{
    private readonly IChatClient _openAI;
    private readonly IChatClient _anthropic;
    private readonly TasteTestOptions _options;

    public SampleModelChatClientFactory(IOptions<TasteTestOptions> options)
    {
        _options = options.Value;
        _openAI = new SampleChatClient(
            "The strongest explanation starts with a boundary: distributed systems trade immediate agreement for availability. " +
            "Picture several store clerks updating separate copies of the same inventory sheet. They can keep serving customers during a network outage, " +
            "then reconcile their sheets afterward. The analogy breaks because software reconciliation is rule-driven and can preserve exact history; human clerks often cannot.",
            "**Choose Enterprise.** Its one-hour response target meets the two-hour requirement; Business's four-hour target does not. " +
            "Both offer 24/7 coverage.\n\nFor 80 seats, Business costs **$3,200/month** ($40 each), " +
            "while Enterprise costs **$6,000/month** ($75 each). The extra $2,800 buys the shorter response target.\n\nThese are fictional demo catalog prices.")
            .AsBuilder()
            .UseFunctionInvocation(configure: invocation => invocation.MaximumIterationsPerRequest = 4)
            .UseOpenTelemetry(sourceName: TasteTestTelemetry.SourceName,
                configure: telemetry => telemetry.EnableSensitiveData = false)
            .Build();
        _anthropic = new SampleChatClient(
            "Think of eventual consistency as a group chat during a flight. Each phone briefly shows a different latest message, but once connectivity returns, " +
            "everyone converges on the same thread. That framing makes the benefit tangible: progress continues without a central pause. It breaks down when conflicts " +
            "carry business meaning, because databases need explicit merge rules rather than simply sorting messages.",
            "Enterprise is the fit for this team:\n\n- **Coverage:** 24/7 for both plans.\n" +
            "- **First response:** Business is four hours; Enterprise is one hour.\n" +
            "- **80-seat monthly cost:** Business $3,200, Enterprise $6,000.\n\n" +
            "Enterprise costs $2,800 more per month, but it is the only plan that meets the two-hour target. " +
            "This is fictional demo catalog data, not a quote.")
            .AsBuilder()
            .UseFunctionInvocation(configure: invocation => invocation.MaximumIterationsPerRequest = 4)
            .UseOpenTelemetry(sourceName: TasteTestTelemetry.SourceName,
                configure: telemetry => telemetry.EnableSensitiveData = false)
            .Build();
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

    private sealed class SampleChatClient(string response, string catalogResponse) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateResponse(messages, options));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var answer = CreateResponse(messages, options);
            foreach (var content in answer.Messages[0].Contents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (content is TextContent text)
                {
                    foreach (var chunk in Chunk(text.Text, 18))
                    {
                        await Task.Delay(22, cancellationToken);
                        yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
                    }
                }
                else
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);
                }
            }

            // Preview mode still emits usage so the metrics strip renders exactly as it does live.
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                FinishReason = answer.FinishReason,
                Contents = [new UsageContent(answer.Usage!)]
            };
        }

        private ChatResponse CreateResponse(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            var history = messages.ToList();
            var userIndex = history.FindLastIndex(message => message.Role == ChatRole.User);
            var usesCatalog = userIndex >= 0 &&
                (history[userIndex].Text.Contains("catalog", StringComparison.OrdinalIgnoreCase) ||
                 history[userIndex].Text.Contains("support plan", StringComparison.OrdinalIgnoreCase));
            var hasResults = history.Skip(userIndex + 1).Any(message =>
                message.Contents.Any(content => content is FunctionResultContent));
            var lookup = usesCatalog && !hasResults &&
                options?.Tools?.Any(tool => tool is AIFunction function && function.Name == DemoSupportCatalog.ToolName) is true;
            var message = lookup
                ? new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent(Guid.NewGuid().ToString("N"), DemoSupportCatalog.ToolName,
                        new Dictionary<string, object?> { ["plan"] = "Business" }),
                    new FunctionCallContent(Guid.NewGuid().ToString("N"), DemoSupportCatalog.ToolName,
                        new Dictionary<string, object?> { ["plan"] = "Enterprise" })
                ])
                : new ChatMessage(ChatRole.Assistant, usesCatalog ? catalogResponse : response);
            var input = history.Sum(item => EstimateTokens(item.Text));
            var output = lookup ? 20 : EstimateTokens(message.Text);
            return new ChatResponse(message)
            {
                FinishReason = lookup ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
                Usage = new UsageDetails
                {
                    InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output
                }
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("preview", defaultModelId: "canned-preview")
                : serviceType.IsInstanceOfType(this) ? this : null;

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

        private static long EstimateTokens(string? text) =>
            string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);
    }
}
