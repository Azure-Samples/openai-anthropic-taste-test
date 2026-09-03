using System.Diagnostics;
using Anthropic.Exceptions;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using System.ClientModel;

namespace TasteTest.Services;

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
        _clients = clients;
        _randomizer = randomizer;
        _options = options.Value;
        _logger = logger;
        _lanes = CreateLanes();
    }

    public IReadOnlyList<TasteTestLane> Lanes => _lanes;

    public IEnumerable<TasteTestLane> VisibleLanes =>
        Revealed && Winner is not null ? [Winner] : _lanes;

    public TasteTestLane? Winner { get; private set; }

    public bool Revealed => Winner is not null;

    public bool IsBusy { get; private set; }

    public bool CanPick =>
        !Revealed &&
        !IsBusy &&
        _lanes.All(lane => lane.Turns.Count > 0 && !lane.HasError);

    public bool UseSampleResponses => _options.UseSampleResponses;

    public IReadOnlyList<string> SeedPrompts => _options.SeedPrompts;

    public int MaxPromptCharacters => _options.MaxPromptCharacters;

    public async Task SendAsync(
        string prompt,
        Func<bool, Task> notifyProgress,
        CancellationToken cancellationToken = default)
    {
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
        var targets = Revealed && Winner is not null ? [Winner] : _lanes.ToArray();

        try
        {
            await Task.WhenAll(
                targets.Select(lane =>
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

        if (!CanPick)
        {
            throw new InvalidOperationException(
                "Wait for both lanes to finish successfully before picking a winner.");
        }

        if (!_lanes.Contains(lane))
        {
            throw new ArgumentException("The selected lane does not belong to this session.", nameof(lane));
        }

        Winner = lane;
    }

    public ModelIdentity GetIdentity(TasteTestLane lane)
    {
        if (!Revealed)
        {
            throw new InvalidOperationException("Model identity is available only after reveal.");
        }

        return _clients.GetIdentity(lane.Provider);
    }

    public void Reset()
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("Cancel the active response before resetting.");
        }

        foreach (var lane in _lanes)
        {
            lane.Reset();
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
        var renderTimer = Stopwatch.StartNew();

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
                turn.Response += update.Text;

                if (renderTimer.Elapsed >= TimeSpan.FromMilliseconds(100))
                {
                    renderTimer.Restart();
                    await notifyProgress(false);
                }
            }

            var response = updates.ToChatResponse();
            turn.Usage = response.Usage;
            lane.ConversationId = response.ConversationId;

            if (response.Messages.Count > 0)
            {
                lane.History.AddMessages(response);
            }
            else
            {
                lane.History.Add(new ChatMessage(ChatRole.Assistant, turn.Response));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lane.Error = "Response canceled.";
            throw;
        }
        catch (AuthenticationFailedException exception)
        {
            await RecordFailureAsync(lane, exception, notifyProgress);
        }
        catch (ClientResultException exception)
        {
            await RecordFailureAsync(lane, exception, notifyProgress);
        }
        catch (AnthropicApiException exception)
        {
            await RecordFailureAsync(lane, exception, notifyProgress);
        }
        catch (HttpRequestException exception)
        {
            await RecordFailureAsync(lane, exception, notifyProgress);
        }
        finally
        {
            lane.IsStreaming = false;
            await notifyProgress(true);
        }
    }

    private async Task RecordFailureAsync(
        TasteTestLane lane,
        Exception exception,
        Func<bool, Task> notifyProgress)
    {
        lane.Error =
            "This lane could not complete. Check model availability, quota, RBAC, and the server logs.";
        _logger.LogError(exception, "Taste-test lane {Lane} failed for {Provider}.", lane.Label, lane.Provider);
        await notifyProgress(true);
    }

    private List<TasteTestLane> CreateLanes()
    {
        var providers = _randomizer.PlaceOpenAIFirst()
            ? new[] { ProviderKind.OpenAI, ProviderKind.Anthropic }
            : new[] { ProviderKind.Anthropic, ProviderKind.OpenAI };

        return
        [
            new TasteTestLane("A", providers[0]),
            new TasteTestLane("B", providers[1])
        ];
    }
}
