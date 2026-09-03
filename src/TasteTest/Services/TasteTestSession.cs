using System.ClientModel;
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
    private readonly IModelChatClientFactory _clients;
    private readonly ILaneOrderRandomizer _randomizer;
    private readonly TasteTestOptions _options;
    private readonly ILogger<TasteTestSession> _logger;
    private List<TasteTestLane> _lanes;

    public TasteTestSession(
        IModelChatClientFactory clients,
        ILaneOrderRandomizer randomizer,
        IOptions<TasteTestOptions> options,
        ILogger<TasteTestSession> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(randomizer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
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
        _lanes.TrueForAll(lane => lane.Turns.Count > 0 && !lane.HasError);


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

        var normalizedPrompt = prompt.Trim();
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

        IsBusy = true;
        var targets = VisibleLanes;

        try
        {
            await Task.WhenAll(targets.Select(lane =>
                StreamLaneAsync(lane, normalizedPrompt, notifyProgress, cancellationToken)));
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
        return GetIdentity(lane.Provider);
    }

    /// <summary>Model metadata for a provider. Available only after a winner is picked.</summary>
    public ModelIdentity GetIdentity(ProviderKind provider)
    {
        if (!Revealed)
        {
            throw new InvalidOperationException("Model identity is available only after reveal.");
        }

        return _clients.GetIdentity(provider);
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

    private async Task StreamLaneAsync(
        TasteTestLane lane,
        string prompt,
        Func<bool, Task> notifyProgress,
        CancellationToken cancellationToken)
    {
        var turn = lane.BeginTurn(prompt);
        var updates = new List<ChatResponseUpdate>();
        var client = _clients.GetClient(lane.Provider);

        lane.IsStreaming = true;
        await notifyProgress(true);

        try
        {
            var chatOptions = new ChatOptions
            {
                Instructions = _options.SystemPrompt,
                MaxOutputTokens = _options.MaxOutputTokens
            };

            await foreach (var update in client
                .GetStreamingResponseAsync(lane.History, chatOptions, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
                turn.AppendResponse(update.Text);
                await notifyProgress(false);
            }

            var response = updates.ToChatResponse();
            turn.Usage = response.Usage;
            lane.ConversationId = response.ConversationId;

            // Prefer the provider's own assistant messages so tool calls and multi-part content
            // survive into the next turn; fall back to the streamed text when none were returned.
            if (response.Messages.Count > 0)
            {
                lane.History.AddMessages(response);
            }
            else
            {
                lane.History.Add(new ChatMessage(ChatRole.Assistant, turn.Response));
            }
        }
        catch (OperationCanceledException)
        {
            lane.Error = "Response canceled.";
            throw;
        }
        catch (Exception exception) when (exception is
            AuthenticationFailedException or
            ClientResultException or
            AnthropicApiException or
            HttpRequestException or
            TimeoutException)
        {
            // Keep the other lane usable, and never surface provider detail to the browser because
            // that would leak the hidden identity before the reveal.
            lane.Error =
                "This lane could not complete. Check model availability, quota, RBAC, and the server logs.";
            TasteTestLog.LaneFailed(_logger, exception, lane.Label, lane.Provider);
        }
        finally
        {
            lane.IsStreaming = false;
            await notifyProgress(true);
        }
    }

    private List<TasteTestLane> CreateLanes()
    {
        var (first, second) = _randomizer.PlaceOpenAIFirst()
            ? (ProviderKind.OpenAI, ProviderKind.Anthropic)
            : (ProviderKind.Anthropic, ProviderKind.OpenAI);

        return
        [
            new TasteTestLane("A", first),
            new TasteTestLane("B", second)
        ];
    }
}
