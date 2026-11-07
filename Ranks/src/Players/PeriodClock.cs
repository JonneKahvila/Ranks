using Ranks.Config;

namespace Ranks.Players;

/// <summary>The period schedule <c>Database.VirtualPurgeMode</c> and <c>VirtualPurgeDays</c> define together.</summary>
internal readonly record struct PeriodGrid(VirtualPurgeMode Mode, int Days)
{
    public static PeriodGrid Of(DatabaseOptions database) => new(database.VirtualPurgeMode, database.VirtualPurgeDays);

    public bool IsEnabled => Mode == VirtualPurgeMode.Monthly || Days > 0;
}

/// <summary>Maps a moment onto the fixed period grid a <see cref="PeriodGrid"/> defines (FR-DB-10).</summary>
/// <remarks>
/// Stateless on purpose: every server sharing a database derives the same boundaries from the same
/// setting with nothing stored and nothing to coordinate.
/// </remarks>
internal static class PeriodClock
{
    private const long SecondsPerDay = 86_400;

    /// <remarks>Monday 5 January 1970 UTC, so a 7-day period starts every Monday rather than on the epoch's Thursday.</remarks>
    private const long AnchorSeconds = 4 * SecondsPerDay;

    /// <summary>The id of the period containing <paramref name="unixSeconds"/>: the Unix time of its start.</summary>
    /// <remarks>Only meaningful while <paramref name="grid"/> is enabled.</remarks>
    public static long PeriodFor(long unixSeconds, PeriodGrid grid) =>
        grid.Mode == VirtualPurgeMode.Monthly
            ? new DateTimeOffset(MonthStartOf(unixSeconds)).ToUnixTimeSeconds()
            : DayBlockStartOf(unixSeconds, grid.Days);

    /// <summary>When the period that started at <paramref name="period"/> closes: the first grid boundary after it.</summary>
    /// <remarks>
    /// Not simply start plus one period: after the schedule changes, the active period may have
    /// started on the old grid, and only a boundary of the new one closes it.
    /// </remarks>
    public static DateTime EndOf(long period, PeriodGrid grid) =>
        grid.Mode == VirtualPurgeMode.Monthly
            ? MonthStartOf(period).AddMonths(1)
            : DateTimeOffset.FromUnixTimeSeconds(DayBlockStartOf(period, grid.Days) + grid.Days * SecondsPerDay).UtcDateTime;

    private static long DayBlockStartOf(long unixSeconds, int days)
    {
        var length = days * SecondsPerDay;
        var elapsed = unixSeconds - AnchorSeconds;
        var intoPeriod = (elapsed % length + length) % length;

        return unixSeconds - intoPeriod;
    }

    private static DateTime MonthStartOf(long unixSeconds)
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        return new DateTime(moment.Year, moment.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
