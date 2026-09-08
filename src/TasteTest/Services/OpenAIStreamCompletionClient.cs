using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace TasteTest.Services;

internal sealed class OpenAIStreamCompletionClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            // MEAI.OpenAI 10.9 forwards response.incomplete as raw metadata but omits its
            // finish reason and usage. Reuse its native conversion without replaying text.
            if (update.RawRepresentation is StreamingResponseIncompleteUpdate incomplete)
            {
                var terminal = incomplete.Response.AsChatResponse();
                update.FinishReason ??= terminal.FinishReason;
                if (terminal.Usage is { } usage && !update.Contents.Any(content => content is UsageContent))
                {
                    update.Contents.Add(new UsageContent(usage));
                }
            }

            yield return update;
        }
    }
}
