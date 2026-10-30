using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;
using TasteTest.Services;

namespace TasteTest.Tests;

public sealed class SampleChatClientTests
{
    [Fact]
    public async Task Preview_ExercisesTheSharedCatalogWithoutCallingAzure()
    {
        TasteTestOptions options = new() { UseSampleResponses = true };
        using IChatClient gpt = new SampleChatClient(ProviderKind.OpenAI, "gpt-preview")
            .AsBuilder().UseFunctionInvocation().Build();
        using IChatClient claude = new SampleChatClient(ProviderKind.Anthropic, "claude-preview")
            .AsBuilder().UseFunctionInvocation().Build();
        TasteTestSession session = new(
            gpt, claude, new CryptoLaneOrderRandomizer(), Options.Create(options), NullLogger<TasteTestSession>.Instance);

        await session.SendAsync(options.SeedPrompts[3], _ => Task.CompletedTask);

        Assert.True(session.UseSampleResponses);
        Assert.True(session.CanPick);
        Assert.All(session.Lanes, lane =>
        {
            Assert.Equal(2, lane.LatestTurn!.CatalogLookups);
            Assert.Contains("$6,000", lane.LatestTurn.Response, StringComparison.Ordinal);
            Assert.True(lane.LatestTurn.IsComplete);
            Assert.NotNull(lane.LatestTurn.Usage);
        });
    }
}
