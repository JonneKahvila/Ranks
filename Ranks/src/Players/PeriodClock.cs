namespace Ranks.Players;

/// <summary>Maps a moment onto the fixed period grid that <c>Database.VirtualPurgeDays</c> defines (FR-DB-10).</summary>
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
    public static long PeriodFor(long unixSeconds, int days)
    {
        var length = days * SecondsPerDay;
        var elapsed = unixSeconds - AnchorSeconds;
        var intoPeriod = (elapsed % length + length) % length;

        return unixSeconds - intoPeriod;
    }

    /// <summary>When the period that started at <paramref name="period"/> closes: the first grid boundary after it.</summary>
    /// <remarks>
    /// Not simply start plus N days: after <c>VirtualPurgeDays</c> changes, the active period may
    /// have started on the old grid, and only a boundary of the new one closes it.
    /// </remarks>
    public static DateTime EndOf(long period, int days) =>
        DateTimeOffset.FromUnixTimeSeconds(PeriodFor(period, days) + days * SecondsPerDay).UtcDateTime;
}
