using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using Anthropic.Exceptions;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace TasteTest.Services;

/// <summary>
/// Owns one blind comparison for a single Blazor circuit: lane ordering, symmetric history,
/// concurrent streaming, and the reveal.
/// </summary>
/// <remarks>
/// Rendering is deliberately not throttled here. This type reports every update and the component
/// decides how often to re-render, so the SignalR circuit has exactly one throttle.
/// </remarks>
public sealed class TasteTestSession
{
    private readonly IChatClient _gpt;
    private readonly IChatClient _claude;
    private readonly ILaneOrderRandomizer _randomizer;
    private readonly TasteTestOptions _options;
    private readonly ILogger<TasteTestSession> _logger;
    private List<TasteTestLane> _lanes;

    public TasteTestSession(
        [FromKeyedServices("gpt")] IChatClient gpt,
        [FromKeyedServices("claude")] IChatClient claude,
        ILaneOrderRandomizer randomizer,
        IOptions<TasteTestOptions> options,
        ILogger<TasteTestSession> logger)
    {
        ArgumentNullException.ThrowIfNull(gpt);
        ArgumentNullException.ThrowIfNull(claude);
        ArgumentNullException.ThrowIfNull(randomizer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _gpt = gpt;
        _claude = claude;
        _randomizer = randomizer;
        _options = options.Value;
        _logger = logger;
        _lanes = CreateLanes();
    }

    public IReadOnlyList<TasteTestLane> Lanes => _lanes;

    /// <summary>Lanes to render: both while blind, only the winner after the reveal.</summary>
    public IReadOnlyList<TasteTestLane> VisibleLanes =>
        Winner is { } winner ? [winner] : _lanes;

    public TasteTestLane? Winner { get; private set; }

    public bool Revealed => Winner is not null;

    public bool IsBusy { get; private set; }

    public bool HasTranscript => _lanes.Exists(lane => lane.Turns.Count > 0);

    public bool CanPick =>
        !Revealed &&
        !IsBusy &&
        _lanes.TrueForAll(lane => lane.LatestTurn is { IncludedInHistory: true });

    public bool CanRetry => !IsBusy &&
        VisibleLanes.Any(lane => lane.LatestTurn is { IncludedInHistory: false });

    public bool UseSampleResponses => _options.UseSampleResponses;

    public IReadOnlyList<string> SeedPrompts => _options.SeedPrompts;

    public int MaxPromptCharacters => _options.MaxPromptCharacters;

    /// <summary>
    /// Sends the prompt to every active lane at once and streams each answer back.
    /// </summary>
    /// <param name="prompt">The prompt to send to each active lane.</param>
    /// <param name="notifyProgress">
    /// Invoked as content arrives. The argument is <see langword="true"/> for state changes that
    /// must render immediately, such as a lane starting, failing, or finishing.
    /// </param>
    /// <param name="cancellationToken">Cancels every in-flight lane.</param>
    public async Task SendAsync(
        string prompt,
        Func<bool, Task> notifyProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(notifyProgress);

        if (IsBusy)
        {
            throw new InvalidOperationException("A response is already in progress.");
        }

        string normalizedPrompt = prompt.Trim();
        if (normalizedPrompt.Length == 0)
        {
            throw new ArgumentException("Enter a prompt before starting the taste test.", nameof(prompt));
        }

        if (normalizedPrompt.Length > _options.MaxPromptCharacters)
        {
            throw new ArgumentException(
                $"Keep the prompt under {_options.MaxPromptCharacters:N0} characters.",
                nameof(prompt));
        }

        cancellationToken.ThrowIfCancellationRequested();
        IsBusy = true;
        (TasteTestLane Lane, TasteTestTurn Turn)[] attempts = VisibleLanes
            .Select(lane => (Lane: lane, Turn: lane.BeginTurn(normalizedPrompt)))
            .ToArray();

        try
        {
            // Credential acquisition can block synchronously inside a vendor SDK. Keep it off
            // the Blazor circuit so both lanes start independently and Cancel stays responsive.
            ChatResponse?[] responses = await Task.WhenAll(attempts.Select(attempt =>
                Task.Run(() => StreamLaneAsync(
                    attempt.Lane, attempt.Turn, notifyProgress, cancellationToken), CancellationToken.None)));

            cancellationToken.ThrowIfCancellationRequested();

            // A comparison is one transaction: a failed lane must not leave the other lane
            // with extra context when the user retries. UI transcripts retain partial answers.
            if (responses.All(response => response is not null))
            {
                for (int index = 0; index < attempts.Length; index++)
                {
                    if (responses[index] is { } response)
                    {
                        attempts[index].Lane.CommitTurn(attempts[index].Turn, response);
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
            await notifyProgress(true);
        }
    }

    public void PickWinner(TasteTestLane lane)
    {
        ArgumentNullException.ThrowIfNull(lane);

        if (!_lanes.Contains(lane))
        {
            throw new ArgumentException("The selected lane does not belong to this session.", nameof(lane));
        }

        if (!CanPick)
        {
            throw new InvalidOperationException(
                "Wait for both lanes to finish successfully before picking a winner.");
        }

        Winner = lane;
    }

    /// <summary>Model metadata for a lane. Available only after a winner is picked.</summary>
    public ModelIdentity GetIdentity(TasteTestLane lane)
    {
        ArgumentNullException.ThrowIfNull(lane);
        if (!_lanes.Contains(lane))
        {
            throw new ArgumentException("The selected lane does not belong to this session.", nameof(lane));
        }

        return GetIdentity(lane.Provider);
    }

    /// <summary>Model metadata for a provider. Available only after a winner is picked.</summary>
    public ModelIdentity GetIdentity(ProviderKind provider)
    {
        if (!Revealed)
        {
            throw new InvalidOperationException("Model identity is available only after reveal.");
        }

        return ModelCatalog.CreateIdentity(provider, GetClient(provider));
    }

    /// <summary>Starts a fresh blind comparison with a newly randomized lane order.</summary>
    public void Reset()
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("Cancel the active response before resetting.");
        }

        Winner = null;
        _lanes = CreateLanes();
    }

    private async Task<ChatResponse?> StreamLaneAsync(
        TasteTestLane lane,
        TasteTestTurn turn,
        Func<bool, Task> notifyProgress,
        CancellationToken cancellationToken)
    {
        List<ChatResponseUpdate> updates = new List<ChatResponseUpdate>();
        long startedAt = Stopwatch.GetTimestamp();
        bool toolBoundary = false;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        CancellationToken requestToken = deadline.Token;

        try
        {
            await notifyProgress(true);
            requestToken.ThrowIfCancellationRequested();
            IChatClient client = GetClient(lane.Provider);
            ChatOptions chatOptions = new ChatOptions
            {
                Instructions = _options.SystemPrompt,
                MaxOutputTokens = _options.MaxOutputTokens,
                Reasoning = new() { Effort = ReasoningEffort.Low },
                Tools = [DemoSupportCatalog.CreateTool(async (result, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (result.Plan is not null)
                    {
                        turn.RecordCatalogLookup();
                    }
                    else
                    {
                        turn.RecordCatalogLookupError();
                        Activity.Current?.SetStatus(ActivityStatusCode.Error, "Catalog lookup rejected.");
                        TasteTestLog.CatalogLookupFailed(_logger, lane.Label, lane.Provider);
                    }
                    await notifyProgress(true).ConfigureAwait(false);
                })]
            };

            // Neither the SDK nor a failed call can append an incomplete user turn to history.
            List<ChatMessage> messages = new List<ChatMessage>(lane.History)
            {
                new(ChatRole.User, turn.Prompt)
            };

            await foreach (ChatResponseUpdate update in client
                .GetStreamingResponseAsync(messages, chatOptions, requestToken)
                .ConfigureAwait(false))
            {
                requestToken.ThrowIfCancellationRequested();
                updates.Add(update);

                if (update.Role == ChatRole.Tool || update.Contents.Any(content => content is FunctionResultContent))
                {
                    toolBoundary = true;
                }

                if (update.Role != ChatRole.Tool && !string.IsNullOrEmpty(update.Text))
                {
                    if (turn.TimeToFirstToken is null)
                    {
                        turn.TimeToFirstToken = Stopwatch.GetElapsedTime(startedAt);
                    }
                    if (toolBoundary && turn.HasResponse)
                    {
                        turn.AppendResponse("\n\n");
                    }
                    toolBoundary = false;
                    turn.AppendResponse(update.Text);
                }
                await notifyProgress(false);
            }

            // ToChatResponse folds provider-specific usage content into one UsageDetails, so the
            // OpenAI Responses and Anthropic Messages token counts arrive in the same shape.
            ChatResponse response = updates.ToChatResponse();
            requestToken.ThrowIfCancellationRequested();
            turn.Usage = response.Usage;
            turn.FinishReason = response.FinishReason;
            Exception? toolError = response.Messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().FirstOrDefault(content => content.Exception is not null)?.Exception;
            if (toolError is not null || turn.CatalogLookupErrors > 0)
            {
                if (toolError is not null)
                {
                    TasteTestLog.LaneFailed(_logger, toolError, lane.Label, lane.Provider);
                }
                turn.Error = "The catalog lookup did not complete. Retry this prompt.";
                return null;
            }
            if (_logger.IsEnabled(LogLevel.Information))
            {
                TasteTestLog.StreamFinished(_logger, lane.Label, lane.Provider,
                    response.FinishReason, response.Usage?.OutputTokenCount,
                    response.Usage?.ReasoningTokenCount, response.Text.Length);
            }

            if (response.FinishReason == ChatFinishReason.Length)
            {
                turn.Error = "The response reached its token limit before finishing. " +
                    "Reasoning can use this budget before any text appears. " +
                    "Try a shorter question, or increase TASTE_TEST_MAX_OUTPUT_TOKENS and restart the app.";
                return null;
            }

            if (response.FinishReason != ChatFinishReason.Stop)
            {
                turn.Error = response.FinishReason == ChatFinishReason.ContentFilter
                    ? "This response was blocked by a content filter. Rephrase your question and try again."
                    : "The response stream ended without a completed answer. Please retry.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(response.Text))
            {
                turn.Error = "No visible answer was returned. Try again or increase the output token limit.";
                TasteTestLog.EmptyResponse(_logger, lane.Label, lane.Provider);
                return null;
            }

            turn.IsComplete = true;
            turn.IsStreaming = false;
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            turn.Error = "Response canceled. This attempt was not added to conversation history.";
            return null;
        }
        catch (OperationCanceledException exception)
        {
            turn.Error = $"This lane timed out. Requests are limited to {_options.RequestTimeoutSeconds} seconds. Please retry.";
            TasteTestLog.LaneFailed(_logger, exception, lane.Label, lane.Provider);
            return null;
        }
        catch (Exception exception) when (exception is
            AuthenticationFailedException or
            ClientResultException or
            AnthropicApiException or
            HttpRequestException or
            JsonException or
            ArgumentException or
            TimeoutException)
        {
            // Keep the other lane usable, and never surface provider detail to the browser because
            // that would leak the hidden identity before the reveal.
            turn.Error =
                "This lane could not complete. Check model availability, quota, RBAC, and the server logs.";
            TasteTestLog.LaneFailed(_logger, exception, lane.Label, lane.Provider);
            return null;
        }
        finally
        {
            if (turn.IsStreaming && turn.Error is null)
            {
                turn.Error = "This lane stopped unexpectedly. Check the server logs before retrying.";
            }

            turn.Duration = Stopwatch.GetElapsedTime(startedAt);
            turn.IsStreaming = false;
            await notifyProgress(true);
        }
    }

    private IChatClient GetClient(ProviderKind provider) => provider switch
    {
        ProviderKind.OpenAI => _gpt,
        ProviderKind.Anthropic => _claude,
        _ => throw ModelCatalog.UnknownProvider(provider)
    };

    private List<TasteTestLane> CreateLanes()
    {
        (ProviderKind first, ProviderKind second) = _randomizer.PlaceOpenAIFirst()
            ? (ProviderKind.OpenAI, ProviderKind.Anthropic)
            : (ProviderKind.Anthropic, ProviderKind.OpenAI);

        return
        [
            new TasteTestLane("A", first),
            new TasteTestLane("B", second)
        ];
    }
}
