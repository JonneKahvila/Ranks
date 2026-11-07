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

    /// <summary>The period in force at <paramref name="unixSeconds"/>: <paramref name="active"/> until it ends, then the grid's.</summary>
    /// <remarks>
    /// Boot and rollover both go through here, so the end a menu shows is when the period turns over.
    /// Never below <paramref name="active"/>: period ids only rise (FR-DB-10).
    /// </remarks>
    public static long Due(long active, long unixSeconds, PeriodGrid grid) =>
        unixSeconds < UnixOf(EndOf(active, grid)) ? active : Math.Max(PeriodFor(unixSeconds, grid), active);

    /// <summary>When the period that started at <paramref name="period"/> closes: a boundary of <paramref name="grid"/>.</summary>
    /// <remarks>
    /// Not simply start plus one period: after the schedule changes, the active period may have
    /// started on the old grid, and only a boundary of the new one closes it.
    /// </remarks>
    public static DateTime EndOf(long period, PeriodGrid grid) =>
        grid.Mode == VirtualPurgeMode.Monthly
            ? MonthlyEndOf(period, grid.Days)
            : DateTimeOffset.FromUnixTimeSeconds(DayBlockEndOf(period, grid.Days)).UtcDateTime;

    /// <summary>Whether the period from <paramref name="start"/> to <paramref name="end"/> is exactly one UTC calendar month.</summary>
    /// <remarks>Judged from the boundaries alone, so a past period is named for what it was, not for today's schedule.</remarks>
    public static bool IsCalendarMonth(long start, long end) =>
        IsMonthStart(start) && UnixOf(MonthStartOf(start).AddMonths(1)) == end;

    /// <summary>The id of the period containing <paramref name="unixSeconds"/>: the Unix time of its start.</summary>
    /// <remarks>Only meaningful while <paramref name="grid"/> is enabled.</remarks>
    private static long PeriodFor(long unixSeconds, PeriodGrid grid) =>
        grid.Mode == VirtualPurgeMode.Monthly
            ? UnixOf(MonthStartOf(unixSeconds))
            : DayBlockStartOf(unixSeconds, grid.Days);

    /// <remarks>
    /// A period not starting on the 1st began under <c>Days</c>. It keeps its N-day end and runs on to the
    /// next 1st, so a switch never resets anyone mid-month; with N at 0 it closes at the first 1st after it began.
    /// </remarks>
    private static DateTime MonthlyEndOf(long period, int days) =>
        IsMonthStart(period) || days <= 0
            ? MonthStartOf(period).AddMonths(1)
            : FirstMonthStartFrom(DayBlockEndOf(period, days));

    private static long DayBlockStartOf(long unixSeconds, int days)
    {
        var length = days * SecondsPerDay;
        var elapsed = unixSeconds - AnchorSeconds;
        var intoPeriod = (elapsed % length + length) % length;

        return unixSeconds - intoPeriod;
    }

    private static long DayBlockEndOf(long period, int days) => DayBlockStartOf(period, days) + days * SecondsPerDay;

    private static bool IsMonthStart(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime is { Day: 1, TimeOfDay.Ticks: 0 };

    private static DateTime FirstMonthStartFrom(long unixSeconds) =>
        IsMonthStart(unixSeconds) ? MonthStartOf(unixSeconds) : MonthStartOf(unixSeconds).AddMonths(1);

    private static DateTime MonthStartOf(long unixSeconds)
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        return new DateTime(moment.Year, moment.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private static long UnixOf(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();
}
