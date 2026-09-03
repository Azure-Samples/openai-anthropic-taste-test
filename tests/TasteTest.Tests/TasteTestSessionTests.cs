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
        Assert.All(session.Lanes, lane =>
        {
            Assert.Empty(lane.Turns);
            Assert.Null(lane.ConversationId);
        });
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
        public int CallCount { get; private set; }

        public List<int> MessageCounts { get; } = [];

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
