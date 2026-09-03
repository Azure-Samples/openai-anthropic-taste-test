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
}
