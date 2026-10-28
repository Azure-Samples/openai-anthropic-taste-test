using System.Text;
using Microsoft.Extensions.AI;

namespace TasteTest.Services;

/// <summary>
/// One prompt and its streamed answer for a single lane.
/// </summary>
/// <remarks>
/// The answer accumulates in a <see cref="StringBuilder"/> because a streaming turn appends
/// hundreds of small fragments. The lock keeps the streaming task and the Blazor renderer, which
/// run on different threads, from observing a torn buffer.
/// </remarks>
public sealed class TasteTestTurn
{
    private readonly StringBuilder _response = new();
    private readonly Lock _gate = new();
    private int _catalogLookups;
    private int _catalogLookupErrors;

    internal TasteTestTurn(string prompt) => Prompt = prompt;

    public string Prompt { get; }

    public string Response
    {
        get
        {
            lock (_gate)
            {
                return _response.ToString();
            }
        }
    }

    public bool HasResponse
    {
        get
        {
            lock (_gate)
            {
                return _response.Length > 0;
            }
        }
    }

    public UsageDetails? Usage { get; internal set; }

    public int CatalogLookups => Volatile.Read(ref _catalogLookups);

    public int CatalogLookupErrors => Volatile.Read(ref _catalogLookupErrors);

    public ChatFinishReason? FinishReason { get; internal set; }

    public bool IsComplete { get; internal set; }

    /// <summary>
    /// Time from request start to the first streamed token. Measured identically for both lanes so
    /// the OpenAI Responses stream and the Anthropic Messages stream stay directly comparable.
    /// </summary>
    public TimeSpan? TimeToFirstToken { get; internal set; }

    /// <summary>Total time from request start until the lane stopped streaming.</summary>
    public TimeSpan? Duration { get; internal set; }

    public bool IsStreaming { get; internal set; } = true;

    public bool IncludedInHistory { get; internal set; }

    public string? Error { get; internal set; }

    internal void AppendResponse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            _response.Append(text);
        }
    }

    internal void RecordCatalogLookup() => Interlocked.Increment(ref _catalogLookups);

    internal void RecordCatalogLookupError() => Interlocked.Increment(ref _catalogLookupErrors);
}
