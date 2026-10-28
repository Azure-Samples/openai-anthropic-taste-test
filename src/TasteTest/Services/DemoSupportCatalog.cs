using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace TasteTest.Services;

public static class DemoSupportCatalog
{
    public const string ToolName = "lookup_support_plan";

    public sealed record SupportPlan(
        string Name, decimal UsdPerSeatPerMonth, int FirstResponseHours, string Coverage);

    public sealed record LookupResult(SupportPlan? Plan, string? Error);

    public static SupportPlan Lookup(string plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return FindPlan(plan) ??
            throw new ArgumentException("The demo catalog contains Business and Enterprise plans only.", nameof(plan));
    }

    public static AIFunction CreateTool(Func<LookupResult, CancellationToken, Task> onLookup)
    {
        ArgumentNullException.ThrowIfNull(onLookup);
        return AIFunctionFactory.Create(
            async ([Description("The demo support plan: Business or Enterprise.")] string plan,
                CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = FindPlan(plan);
                var result = new LookupResult(match,
                    match is null ? "Unknown support plan. Choose Business or Enterprise." : null);
                await onLookup(result, cancellationToken).ConfigureAwait(false);
                return result;
            },
            name: ToolName,
            description: "Read a fictional support plan's USD price per seat per month, first-response target in hours, and coverage. Read-only demo data, not a commercial offer.");
    }

    private static SupportPlan? FindPlan(string? plan) => plan?.Trim().ToUpperInvariant() switch
    {
        "BUSINESS" => new("Business", 40m, 4, "24/7"),
        "ENTERPRISE" => new("Enterprise", 75m, 1, "24/7"),
        _ => null
    };
}
