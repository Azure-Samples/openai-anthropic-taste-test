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
}
