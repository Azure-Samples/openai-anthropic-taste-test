using Microsoft.Extensions.AI;

namespace TasteTest.Services;

/// <summary>
/// Source-generated log messages so the hot streaming path allocates nothing when the level is
/// disabled.
/// </summary>
internal static partial class TasteTestLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Error,
        Message = "Taste-test lane {Lane} failed for provider {Provider}.")]
    public static partial void LaneFailed(
        ILogger logger,
        Exception exception,
        string lane,
        ProviderKind provider);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Taste-test lane {Lane} returned no visible answer for provider {Provider}.")]
    public static partial void EmptyResponse(ILogger logger, string lane, ProviderKind provider);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "The taste-test request could not be completed.")]
    public static partial void RequestFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Lane {Lane} ({Provider}) finished: {FinishReason}; output tokens {OutputTokens}, reasoning tokens {ReasoningTokens}, visible characters {VisibleCharacters}.")]
    public static partial void StreamFinished(
        ILogger logger, string lane, ProviderKind provider, ChatFinishReason? finishReason,
        long? outputTokens, long? reasoningTokens, int visibleCharacters);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Debug, Message = "Ignored a duplicate submit while a request was active.")]
    public static partial void DuplicateSubmit(ILogger logger);
}
