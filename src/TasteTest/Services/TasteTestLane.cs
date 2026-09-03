using Microsoft.Extensions.AI;

namespace TasteTest.Services;

public sealed class TasteTestLane
{
    private readonly List<ChatMessage> _history = [];
    private readonly List<TasteTestTurn> _turns = [];

    internal TasteTestLane(string label, ProviderKind provider)
    {
        Label = label;
        Provider = provider;
    }

    public string Label { get; }

    public IReadOnlyList<TasteTestTurn> Turns => _turns;

    public bool IsStreaming { get; internal set; }

    public bool HasError => Error is not null;

    public string? Error { get; internal set; }

    public string? ConversationId { get; internal set; }

    internal ProviderKind Provider { get; }

    internal List<ChatMessage> History => _history;

    internal TasteTestTurn BeginTurn(string prompt)
    {
        var turn = new TasteTestTurn(prompt);
        _turns.Add(turn);
        _history.Add(new ChatMessage(ChatRole.User, prompt));
        Error = null;
        return turn;
    }

    internal void Reset()
    {
        _history.Clear();
        _turns.Clear();
        ConversationId = null;
        Error = null;
        IsStreaming = false;
    }
}
