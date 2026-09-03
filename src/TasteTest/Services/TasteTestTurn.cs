using Microsoft.Extensions.AI;

namespace TasteTest.Services;

public sealed class TasteTestTurn
{
    internal TasteTestTurn(string prompt)
    {
        Prompt = prompt;
    }

    public string Prompt { get; }

    public string Response { get; internal set; } = string.Empty;

    public UsageDetails? Usage { get; internal set; }
}
