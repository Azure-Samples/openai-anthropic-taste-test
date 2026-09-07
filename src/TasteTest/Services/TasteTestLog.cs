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
}
