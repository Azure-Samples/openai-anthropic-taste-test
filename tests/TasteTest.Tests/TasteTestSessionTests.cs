using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TasteTest.Services;

namespace TasteTest.Tests;

public sealed class TasteTestSessionTests
{
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

    private static TasteTestSession CreateSession(IModelChatClientFactory factory) =>
        new(
            factory,
            new FixedLaneOrderRandomizer(),
            Options.Create(new TasteTestOptions { UseSampleResponses = true }),
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

        public void FailWith(Exception? failure) => _failure = failure;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, responseText)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            MessageCounts.Add(messages.Count());

            if (probe is not null)
            {
                await probe.EnterAsync(cancellationToken);
            }

            if (_failure is not null)
            {
                throw _failure;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
            yield return new ChatResponseUpdate
            {
                Contents = [new UsageContent(new UsageDetails())]
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
