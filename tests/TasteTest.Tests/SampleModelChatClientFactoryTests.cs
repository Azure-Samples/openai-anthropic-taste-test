using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TasteTest.Services;

namespace TasteTest.Tests;

public sealed class SampleModelChatClientFactoryTests
{
    [Fact]
    public async Task Preview_ExercisesTheSharedCatalogWithoutCallingAzure()
    {
        var options = new TasteTestOptions { UseSampleResponses = true };
        using var factory = new SampleModelChatClientFactory(Options.Create(options));
        var session = new TasteTestSession(
            factory, new CryptoLaneOrderRandomizer(), Options.Create(options), NullLogger<TasteTestSession>.Instance);

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
