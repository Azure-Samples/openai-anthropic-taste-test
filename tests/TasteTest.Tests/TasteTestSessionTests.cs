using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TasteTest.Services;

namespace TasteTest.Tests;

public sealed class TasteTestSessionTests
{
    [Fact]
    public async Task SendAsync_TracksActualCatalogLookupsPerLaneAndTurn()
    {
        using var factory = new TrackingClientFactory();
        factory.OpenAI.InvokeCatalog = true;
        var session = CreateSession(factory);

        await session.SendAsync("Compare support plans.", _ => Task.CompletedTask);
        factory.OpenAI.InvokeCatalog = false;
        await session.SendAsync("A separate question.", _ => Task.CompletedTask);

        Assert.Equal(1, session.Lanes[0].Turns[0].CatalogLookups);
        Assert.Equal(0, session.Lanes[1].Turns[0].CatalogLookups);
        Assert.All(session.Lanes, lane => Assert.Equal(0, lane.LatestTurn!.CatalogLookups));
        Assert.True(session.CanPick);
    }

    [Fact]
    public async Task SendAsync_CatalogRejectionsCannotWinOrEnterHistory()
    {
        using var factory = new TrackingClientFactory();
        factory.OpenAI.InvokeCatalog = true;
        factory.OpenAI.CatalogPlan = "Premium";
        var session = CreateSession(factory);

        await session.SendAsync("Compare support plans.", _ => Task.CompletedTask);

        Assert.Equal(0, session.Lanes[0].LatestTurn!.CatalogLookups);
        Assert.Equal(1, session.Lanes[0].LatestTurn!.CatalogLookupErrors);
        Assert.Equal("The catalog lookup did not complete. Retry this prompt.", session.Lanes[0].Error);
        Assert.False(session.CanPick);
        Assert.True(session.CanRetry);
        Assert.All(session.Lanes, lane => Assert.False(lane.LatestTurn!.IncludedInHistory));
    }

    [Fact]
    public async Task SendAsync_ToolExceptionsCannotBecomeCompletedAnswers()
    {
        using var factory = new TrackingClientFactory();
        factory.OpenAI.ToolResultError = new InvalidOperationException("private tool failure");
        var session = CreateSession(factory);

        await session.SendAsync("Compare support plans.", _ => Task.CompletedTask);

        Assert.NotNull(session.Lanes[0].Error);
        Assert.False(session.Lanes[0].LatestTurn!.IsComplete);
        Assert.False(session.CanPick);
    }

    [Fact]
    public async Task SendAsync_SeparatesToolPreamblesFromTheFinalMarkdownAnswer()
    {
        using var factory = new TrackingClientFactory();
        factory.OpenAI.InvokeCatalog = true;
        factory.OpenAI.ToolPreamble = "I'll check the catalog.";
        factory.OpenAI.ResponseText = "## Choose Enterprise\n\nIt meets the target.";
        var session = CreateSession(factory);

        await session.SendAsync("Compare support plans.", _ => Task.CompletedTask);

        Assert.Equal("I'll check the catalog.\n\n## Choose Enterprise\n\nIt meets the target.",
            session.Lanes[0].LatestTurn!.Response);
        Assert.True(session.CanPick);
    }

    [Fact]
    public async Task SendAsync_MalformedToolArgumentsFailALaneWithoutEscapingTheCircuit()
    {
        using var factory = new TrackingClientFactory();
        factory.OpenAI.FailWith(new JsonException("malformed generated tool arguments"));
        var session = CreateSession(factory);

        await session.SendAsync("Compare support plans.", _ => Task.CompletedTask);

        Assert.True(session.Lanes[0].HasError);
        Assert.False(session.IsBusy);
        Assert.False(session.CanPick);
    }

    [Fact]
    public async Task SendAsync_StreamsBothLanesConcurrentlyAndCapturesUsage()
    {
        var probe = new ConcurrencyProbe(expectedEntrants: 2);
        using var factory = new TrackingClientFactory(probe);
        var session = CreateSession(factory);

        await session.SendAsync("Compare these answers.", _ => Task.CompletedTask);

        Assert.True(probe.WasConcurrent);
        Assert.True(session.CanPick);
        Assert.All(session.Lanes, lane =>
        {
            var turn = Assert.Single(lane.Turns);
            Assert.NotEmpty(turn.Response);
            Assert.NotNull(turn.Usage);
        });
        Assert.Equal(1, factory.OpenAI.CallCount);
        Assert.Equal(1, factory.Anthropic.CallCount);
    }

    [Fact]
    public async Task SendAsync_RecordsFirstTokenLatencyAndDurationForEachLane()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);

        await session.SendAsync("Compare these answers.", _ => Task.CompletedTask);

        Assert.All(session.Lanes, lane =>
        {
            var turn = Assert.Single(lane.Turns);
            var firstToken = Assert.NotNull(turn.TimeToFirstToken);
            var duration = Assert.NotNull(turn.Duration);
            Assert.True(firstToken >= TimeSpan.Zero);

            // First-token latency is a prefix of the whole stream, so it can never exceed it.
            Assert.True(
                duration >= firstToken,
                $"Duration {duration} must be at least the first-token latency {firstToken}.");
        });
    }

    [Fact]
    public async Task SendAsync_LeavesFirstTokenLatencyUnsetWhenALaneFailsBeforeStreaming()
    {
        using var factory = new TrackingClientFactory();
        factory.Anthropic.FailWith(new HttpRequestException("transport failure"));
        var session = CreateSession(factory);

        await session.SendAsync("Compare these answers.", _ => Task.CompletedTask);

        var failed = Assert.Single(session.Lanes, lane => lane.HasError);
        var turn = Assert.Single(failed.Turns);
        Assert.Null(turn.TimeToFirstToken);

        // Duration is still recorded so a failed attempt can be timed.
        Assert.NotNull(turn.Duration);
    }

    [Fact]
    public async Task PickWinner_RevealsIdentityAndContinuesOnlyWinningHistory()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);

        Assert.Throws<InvalidOperationException>(() => session.GetIdentity(session.Lanes[0]));

        await session.SendAsync("First prompt", _ => Task.CompletedTask);
        var winner = session.Lanes[0];
        session.PickWinner(winner);

        Assert.True(session.Revealed);
        Assert.Same(winner, Assert.Single(session.VisibleLanes));
        Assert.Equal("OpenAI", session.GetIdentity(winner).Provider);

        await session.SendAsync("Follow-up prompt", _ => Task.CompletedTask);

        Assert.Equal(2, factory.OpenAI.CallCount);
        Assert.Equal(1, factory.Anthropic.CallCount);
        Assert.Equal([1, 3], factory.OpenAI.MessageCounts);
        Assert.Equal(2, winner.Turns.Count);
    }

    [Fact]
    public async Task Reset_ClearsRevealAndAllConversationState()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);

        await session.SendAsync("First prompt", _ => Task.CompletedTask);
        session.PickWinner(session.Lanes[0]);
        session.Reset();

        Assert.False(session.Revealed);
        Assert.Null(session.Winner);
        Assert.False(session.HasTranscript);
        Assert.All(session.Lanes, lane =>
        {
            Assert.Empty(lane.Turns);
            Assert.Null(lane.ConversationId);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendAsync_RejectsEmptyPrompt(string prompt)
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.SendAsync(prompt, _ => Task.CompletedTask));

        Assert.False(session.HasTranscript);
        Assert.Equal(0, factory.OpenAI.CallCount);
    }

    [Fact]
    public async Task SendAsync_RejectsPromptOverTheConfiguredLimit()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.SendAsync(new string('a', session.MaxPromptCharacters + 1), _ => Task.CompletedTask));

        Assert.Equal(0, factory.OpenAI.CallCount);
    }

    [Fact]
    public async Task SendAsync_IsolatesAFailingLaneAndBlocksTheVote()
    {
        using var factory = new TrackingClientFactory();
        factory.Anthropic.FailWith(new HttpRequestException("model unavailable"));
        var session = CreateSession(factory);

        await session.SendAsync("Compare these answers.", _ => Task.CompletedTask);

        var healthy = session.Lanes.Single(lane => !lane.HasError);
        var failed = session.Lanes.Single(lane => lane.HasError);

        Assert.NotEmpty(healthy.Turns[0].Response);
        Assert.True(healthy.LatestTurn!.IsComplete);
        Assert.False(healthy.LatestTurn.IncludedInHistory);
        Assert.True(session.CanRetry);
        Assert.False(session.CanPick);
        Assert.Throws<InvalidOperationException>(() => session.PickWinner(healthy));

        // The generic message must not identify the provider before the reveal.
        Assert.DoesNotContain("Anthropic", failed.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model unavailable", failed.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendAsync_RecoversAfterAFailedLaneRetries()
    {
        using var factory = new TrackingClientFactory();
        factory.Anthropic.FailWith(new HttpRequestException("transient"));
        var session = CreateSession(factory);

        await session.SendAsync("First prompt", _ => Task.CompletedTask);
        Assert.False(session.CanPick);

        factory.Anthropic.FailWith(null);
        await session.SendAsync("Second prompt", _ => Task.CompletedTask);

        Assert.True(session.CanPick);
        Assert.All(session.Lanes, lane => Assert.False(lane.HasError));
        Assert.Equal([1, 1], factory.OpenAI.MessageCounts);
        Assert.Equal([1, 1], factory.Anthropic.MessageCounts);
        Assert.All(session.Lanes, lane => Assert.False(lane.Turns[0].IncludedInHistory));
        Assert.All(session.Lanes, lane => Assert.True(lane.Turns[1].IncludedInHistory));
    }

    [Fact]
    public async Task SendAsync_PreservesEarlierHistoryButDiscardsTheEntireFailedComparison()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);
        await session.SendAsync("Completed prompt", _ => Task.CompletedTask);

        factory.Anthropic.FailWith(new HttpRequestException("provider details must stay private"));
        await session.SendAsync("Failed prompt", _ => Task.CompletedTask);
        factory.Anthropic.FailWith(null);
        await session.SendAsync("Retry prompt", _ => Task.CompletedTask);

        Assert.Equal([1, 3, 3], factory.OpenAI.MessageCounts);
        Assert.Equal([1, 3, 3], factory.Anthropic.MessageCounts);
        Assert.All(new[] { factory.OpenAI, factory.Anthropic }, client =>
        {
            var history = client.Requests[^1];
            Assert.Equal("Completed prompt", history[0].Text);
            Assert.Equal(ChatRole.Assistant, history[1].Role);
            Assert.Equal("Retry prompt", history[2].Text);
            Assert.DoesNotContain(history, message => message.Text == "Failed prompt");
            Assert.All(client.ConversationIds, id => Assert.Null(id));
        });
    }

    [Fact]
    public async Task SendAsync_CancellationDiscardsEvenAnAlreadyFinishedLane()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);
        using var cancellation = new CancellationTokenSource();
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Anthropic.AfterText = async token =>
        {
            streaming.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        var pending = session.SendAsync("Canceled prompt", _ =>
        {
            if (session.Lanes[0].LatestTurn is { IsComplete: true })
            {
                firstCompleted.TrySetResult();
            }

            return Task.CompletedTask;
        }, cancellation.Token);
        await streaming.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await firstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.IsBusy);
        Assert.False(session.CanPick);
        Assert.Throws<InvalidOperationException>(session.Reset);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SendAsync("Do not overlap", _ => Task.CompletedTask));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.False(session.IsBusy);
        Assert.False(session.CanPick);
        Assert.All(session.Lanes, lane =>
        {
            Assert.False(lane.IsStreaming);
            Assert.False(lane.Turns[0].IncludedInHistory);
            Assert.NotEmpty(lane.Turns[0].Response);
        });
        Assert.Contains("canceled", session.Lanes[1].Error!, StringComparison.OrdinalIgnoreCase);

        factory.Anthropic.AfterText = null;
        await session.SendAsync("Retry after cancel", _ => Task.CompletedTask);
        Assert.True(session.CanPick);
        Assert.Equal([1, 1], factory.OpenAI.MessageCounts);
        Assert.Equal([1, 1], factory.Anthropic.MessageCounts);
    }

    [Fact]
    public async Task SendAsync_CancelAfterBothAnswersStillOffersRetryWithoutCommitting()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync("Question", _ =>
        {
            if (session.Lanes.All(lane => lane.LatestTurn is { IsComplete: true }))
            {
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        }, cancellation.Token));

        Assert.All(session.Lanes, lane => Assert.False(lane.LatestTurn!.IncludedInHistory));
        Assert.False(session.CanPick);
        Assert.True(session.CanRetry);
    }

    [Fact]
    public async Task SendAsync_CanceledFollowUpKeepsOnlyTheWinnersCompletedHistory()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);
        await session.SendAsync("First prompt", _ => Task.CompletedTask);
        session.PickWinner(session.Lanes[0]);
        using var cancellation = new CancellationTokenSource();
        factory.OpenAI.AfterText = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.SendAsync("Canceled follow-up", _ => Task.CompletedTask, cancellation.Token));
        factory.OpenAI.AfterText = null;
        await session.SendAsync("Replacement follow-up", _ => Task.CompletedTask);

        Assert.Equal([1, 3, 3], factory.OpenAI.MessageCounts);
        Assert.Equal(1, factory.Anthropic.CallCount);
        Assert.DoesNotContain(factory.OpenAI.Requests[^1], message => message.Text == "Canceled follow-up");
    }

    [Fact]
    public async Task SendAsync_PreCanceledRequestDoesNotStartOrChangeEitherLane()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.SendAsync("Already canceled", _ => Task.CompletedTask, cancellation.Token));

        Assert.False(session.IsBusy);
        Assert.False(session.HasTranscript);
        Assert.Equal(0, factory.OpenAI.CallCount);
        Assert.Equal(0, factory.Anthropic.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    public async Task SendAsync_EmptyAnswersCannotWinOrEnterHistory(string answer)
    {
        using var factory = new TrackingClientFactory();
        factory.OpenAI.ResponseText = answer;
        var session = CreateSession(factory);
        await session.SendAsync("First prompt", _ => Task.CompletedTask);

        Assert.False(session.CanPick);
        Assert.Contains("No visible answer", session.Lanes[0].Error!, StringComparison.Ordinal);
        Assert.All(session.Lanes, lane => Assert.False(lane.LatestTurn!.IncludedInHistory));
    }

    [Fact]
    public async Task SendAsync_ProviderTimeoutIsNotReportedAsUserCancellation()
    {
        using var factory = new TrackingClientFactory();
        factory.OpenAI.FailWith(new OperationCanceledException("internal request deadline"));
        var session = CreateSession(factory);
        await session.SendAsync("First prompt", _ => Task.CompletedTask);

        Assert.Contains("timed out", session.Lanes[0].Error!, StringComparison.Ordinal);
        Assert.False(session.CanPick);
        Assert.False(session.IsBusy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("An answer that stops mid")]
    public async Task SendAsync_TokenLimitIsNotACompletedAnswer(string text)
    {
        using var factory = new TrackingClientFactory();
        factory.Anthropic.ResponseText = text;
        factory.Anthropic.FinishReason = ChatFinishReason.Length;
        var session = CreateSession(factory);

        await session.SendAsync("Question", _ => Task.CompletedTask);

        Assert.Contains("token limit", session.Lanes[1].Error!, StringComparison.Ordinal);
        Assert.False(session.Lanes[1].LatestTurn!.IsComplete);
        Assert.NotNull(session.Lanes[1].LatestTurn!.Usage);
        Assert.True(session.Lanes[0].LatestTurn!.IsComplete);
        Assert.False(session.CanPick);
        Assert.True(session.CanRetry);
        Assert.All(session.Lanes, lane => Assert.False(lane.LatestTurn!.IncludedInHistory));

        factory.Anthropic.FinishReason = ChatFinishReason.Stop;
        factory.Anthropic.ResponseText = "Complete answer";
        await session.SendAsync("Question", _ => Task.CompletedTask);
        Assert.True(session.CanPick);
        Assert.False(session.CanRetry);
        Assert.Equal([1, 1], factory.Anthropic.MessageCounts);
    }

    [Fact]
    public async Task SendAsync_StreamWithoutTerminalCompletionCannotWin()
    {
        using var factory = new TrackingClientFactory();
        factory.OpenAI.FinishReason = null;
        var session = CreateSession(factory);

        await session.SendAsync("Question", _ => Task.CompletedTask);

        Assert.Contains("without a completed answer", session.Lanes[0].Error!, StringComparison.Ordinal);
        Assert.False(session.CanPick);
        Assert.True(session.CanRetry);
    }

    [Fact]
    public async Task SendAsync_DeadlineStopsAHungLaneAndAllowsRetry()
    {
        using var factory = new TrackingClientFactory();
        factory.Anthropic.AfterText = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var session = CreateSession(factory, new TasteTestOptions { UseSampleResponses = true, RequestTimeoutSeconds = 1 });

        await session.SendAsync("Question", _ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(session.IsBusy);
        Assert.Contains("timed out", session.Lanes[1].Error!, StringComparison.Ordinal);
        Assert.True(session.CanRetry);
        factory.Anthropic.AfterText = null;
        await session.SendAsync("Retry", _ => Task.CompletedTask);
        Assert.True(session.CanPick);
    }

    [Fact]
    public async Task SendAsync_SynchronousProviderStartupDoesNotBlockCallerOrOtherLane()
    {
        using var factory = new TrackingClientFactory();
        using var release = new ManualResetEventSlim();
        var otherLaneStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.OpenAI.BeforeText = () =>
        {
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Synchronous startup blocked the caller.");
            }
        };
        factory.Anthropic.BeforeText = () => otherLaneStarted.TrySetResult();
        var session = CreateSession(factory);

        var pending = session.SendAsync("Question", _ => Task.CompletedTask);
        try
        {
            Assert.True(session.IsBusy);
            await otherLaneStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.Set();
            await pending;
        }

        Assert.True(session.CanPick);
    }

    [Fact]
    public async Task GetIdentity_ByProviderIsAvailableOnlyAfterReveal()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);

        Assert.Throws<InvalidOperationException>(() => session.GetIdentity(ProviderKind.OpenAI));

        await session.SendAsync("First prompt", _ => Task.CompletedTask);
        session.PickWinner(session.Lanes[0]);

        Assert.Equal("OpenAI", session.GetIdentity(ProviderKind.OpenAI).Provider);
        Assert.Equal("Anthropic", session.GetIdentity(ProviderKind.Anthropic).Provider);
    }

    [Fact]
    public async Task PickWinner_RejectsALaneFromAnotherSession()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);
        using var otherFactory = new TrackingClientFactory();
        var otherSession = CreateSession(otherFactory);

        await session.SendAsync("First prompt", _ => Task.CompletedTask);

        Assert.Throws<ArgumentException>(() => session.PickWinner(otherSession.Lanes[0]));
    }

    [Fact]
    public async Task SendAsync_ForcesARenderWhenEachLaneStartsAndStops()
    {
        using var factory = new TrackingClientFactory();
        var session = CreateSession(factory);
        var forcedRenders = 0;

        await session.SendAsync("First prompt", force =>
        {
            if (force)
            {
                Interlocked.Increment(ref forcedRenders);
            }

            return Task.CompletedTask;
        });

        // Two lanes: start plus completion each, and one final render when the session settles.
        Assert.Equal(5, forcedRenders);
    }

    private static TasteTestSession CreateSession(IModelChatClientFactory factory, TasteTestOptions? options = null) =>
        new(
            factory,
            new FixedLaneOrderRandomizer(),
            Options.Create(options ?? new TasteTestOptions { UseSampleResponses = true }),
            NullLogger<TasteTestSession>.Instance);

    private sealed class FixedLaneOrderRandomizer : ILaneOrderRandomizer
    {
        public bool PlaceOpenAIFirst() => true;
    }

    private sealed class TrackingClientFactory : IModelChatClientFactory, IDisposable
    {
        public TrackingClientFactory(ConcurrencyProbe? probe = null)
        {
            OpenAI = new TrackingChatClient("OpenAI response", probe);
            Anthropic = new TrackingChatClient("Anthropic response", probe);
        }

        public TrackingChatClient OpenAI { get; }

        public TrackingChatClient Anthropic { get; }

        public IChatClient GetClient(ProviderKind provider) =>
            provider == ProviderKind.OpenAI ? OpenAI : Anthropic;

        public ModelIdentity GetIdentity(ProviderKind provider) =>
            provider == ProviderKind.OpenAI
                ? new("OpenAI", "gpt-test", "Responses API", "OpenAI SDK")
                : new("Anthropic", "claude-test", "Messages API", "Anthropic SDK");

        public void Dispose()
        {
            OpenAI.Dispose();
            Anthropic.Dispose();
        }
    }

    private sealed class TrackingChatClient(
        string responseText,
        ConcurrencyProbe? probe) : IChatClient
    {
        private Exception? _failure;

        public int CallCount { get; private set; }

        public List<int> MessageCounts { get; } = [];

        public List<ChatMessage[]> Requests { get; } = [];

        public List<string?> ConversationIds { get; } = [];

        public string ResponseText { get; set; } = responseText;

        public bool InvokeCatalog { get; set; }

        public string CatalogPlan { get; set; } = "Business";

        public string? ToolPreamble { get; set; }

        public Exception? ToolResultError { get; set; }

        public Func<CancellationToken, Task>? AfterText { get; set; }

        public Action? BeforeText { get; set; }

        public ChatFinishReason? FinishReason { get; set; } = ChatFinishReason.Stop;

        public void FailWith(Exception? failure) => _failure = failure;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, ResponseText)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            MessageCounts.Add(messages.Count());
            Requests.Add(messages.ToArray());
            ConversationIds.Add(options?.ConversationId);
            cancellationToken.ThrowIfCancellationRequested();

            if (probe is not null)
            {
                await probe.EnterAsync(cancellationToken);
            }

            if (_failure is not null)
            {
                throw _failure;
            }

            if (InvokeCatalog)
            {
                if (ToolPreamble is not null)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, ToolPreamble);
                }
                var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(options!.Tools!));
                var result = await tool.InvokeAsync(new AIFunctionArguments { ["plan"] = CatalogPlan }, cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Tool, [new FunctionResultContent("catalog_call", result)]);
            }
            if (ToolResultError is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Tool,
                    [new FunctionResultContent("failed_call", null) { Exception = ToolResultError }]);
            }

            BeforeText?.Invoke();
            yield return new ChatResponseUpdate(ChatRole.Assistant, ResponseText);
            if (AfterText is not null)
            {
                await AfterText(cancellationToken);
            }

            yield return new ChatResponseUpdate
            {
                FinishReason = FinishReason,
                Contents = [new UsageContent(new UsageDetails())],
                ConversationId = "provider-state-not-reused-in-v1"
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class ConcurrencyProbe(int expectedEntrants)
    {
        private readonly TaskCompletionSource _allEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entrantCount;

        public bool WasConcurrent { get; private set; }

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entrantCount) == expectedEntrants)
            {
                WasConcurrent = true;
                _allEntered.TrySetResult();
            }

            await _allEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }
}
