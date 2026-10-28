using System.Text.Json;
using Microsoft.Extensions.AI;
using TasteTest.Services;

namespace TasteTest.Tests;

public sealed class DemoSupportCatalogTests
{
    [Theory]
    [InlineData("Business", 40, 4)]
    [InlineData(" enterprise ", 75, 1)]
    public void Lookup_ReturnsOnlyFictionalCatalogFacts(string name, int price, int responseHours)
    {
        var plan = DemoSupportCatalog.Lookup(name);
        Assert.Equal(price, plan.UsdPerSeatPerMonth);
        Assert.Equal(responseHours, plan.FirstResponseHours);
        Assert.Equal("24/7", plan.Coverage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public void Lookup_RejectsUnknownPlans(string name) =>
        Assert.Throws<ArgumentException>(() => DemoSupportCatalog.Lookup(name));

    [Fact]
    public async Task CreateTool_InvokesTheLookupAndObserver()
    {
        DemoSupportCatalog.SupportPlan? observed = null;
        var tool = DemoSupportCatalog.CreateTool((plan, _) =>
        {
            observed = plan.Plan;
            return Task.CompletedTask;
        });

        var response = await tool.InvokeAsync(new AIFunctionArguments { ["plan"] = "Enterprise" });

        Assert.NotNull(observed);
        Assert.Equal(6_000m, observed.UsdPerSeatPerMonth * 80);
        Assert.Equal(DemoSupportCatalog.ToolName, tool.Name);
        Assert.Equal("Enterprise", Assert.IsType<JsonElement>(response).GetProperty("plan").GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateTool_HonorsCancellationBeforeLookup()
    {
        var observed = false;
        var tool = DemoSupportCatalog.CreateTool((_, _) =>
        {
            observed = true;
            return Task.CompletedTask;
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["plan"] = "Enterprise" }, cancellation.Token));

        Assert.False(observed);
    }

    [Fact]
    public async Task CreateTool_ReturnsAnExplicitRejectionForTheModel()
    {
        DemoSupportCatalog.LookupResult? observed = null;
        var tool = DemoSupportCatalog.CreateTool((result, _) =>
        {
            observed = result;
            return Task.CompletedTask;
        });

        await tool.InvokeAsync(new AIFunctionArguments { ["plan"] = "Premium" });

        Assert.NotNull(observed);
        Assert.Null(observed.Plan);
        Assert.Contains("Business or Enterprise", observed.Error!, StringComparison.Ordinal);
    }
}
