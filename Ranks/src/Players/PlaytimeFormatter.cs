using SwiftlyS2.Shared;

namespace Ranks.Players;

/// <summary>Seconds as K4-LevelRanks wrote them: <c>4d 5h 30m</c>, <c>5h 30m</c> or <c>30m</c>.</summary>
/// <remarks>Each shape is a whole translation string, so a language can reorder the units rather than only rename them.</remarks>
internal static class PlaytimeFormatter
{
    private const long SecondsPerDay = 86_400;
    private const long SecondsPerHour = 3_600;
    private const long SecondsPerMinute = 60;

    /// <summary>Formats a duration in seconds, dropping leading units that are zero.</summary>
    public static string Format(ISwiftlyCore core, long seconds)
    {
        var total = Math.Max(0, seconds);
        var days = total / SecondsPerDay;
        var hours = total % SecondsPerDay / SecondsPerHour;
        var minutes = total % SecondsPerHour / SecondsPerMinute;

        if (days > 0) return core.Localizer["duration.days", days, hours, minutes];
        if (hours > 0) return core.Localizer["duration.hours", hours, minutes];

        return core.Localizer["duration.minutes", minutes];
    }
}
