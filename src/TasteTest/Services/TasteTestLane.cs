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

    public TasteTestTurn? LatestTurn => _turns.Count > 0 ? _turns[^1] : null;

    public bool IsStreaming => LatestTurn?.IsStreaming ?? false;

    public bool HasError => Error is not null;

    public string? Error => LatestTurn?.Error;

    public string? ConversationId { get; internal set; }

    internal ProviderKind Provider { get; }

    internal List<ChatMessage> History => _history;

    internal TasteTestTurn BeginTurn(string prompt)
    {
        var turn = new TasteTestTurn(prompt);
        _turns.Add(turn);
        return turn;
    }

    internal void CommitTurn(TasteTestTurn turn, ChatResponse response)
    {
        _history.Add(new ChatMessage(ChatRole.User, turn.Prompt));
        _history.AddMessages(response);
        ConversationId = response.ConversationId;
        turn.IncludedInHistory = true;
    }
}
