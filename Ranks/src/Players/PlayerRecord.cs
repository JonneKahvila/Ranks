using Ranks.Contract;
using Ranks.Database;

namespace Ranks.Players;

/// <summary>One player's resident record. Mutated on the game thread, snapshotted for writes.</summary>
internal sealed class PlayerRecord
{
    public required ulong SteamId { get; init; }

    /// <summary>The period these figures belong to (FR-DB-10).</summary>
    public long Period { get; set; }

    private PlayerHistoryRow[]? _pendingArchives;

    /// <summary>Closed periods whose final figures still await their history write, oldest first.</summary>
    /// <remarks>
    /// Replaced, never mutated, and cleared only by <see cref="SettleArchives"/>: the save pass
    /// settles off the game thread while a rollover may be replacing this on it.
    /// </remarks>
    public PlayerHistoryRow[]? PendingArchives => Volatile.Read(ref _pendingArchives);

    /// <remarks>
    /// Rewritten on every save rather than fixed at first sight: <c>!top</c> renders offline players
    /// from this column, so a stale value makes the ladder look broken to the person most likely to
    /// notice. It is also the only column sourced from the player rather than from play, which is
    /// why the writer isolates each row behind a savepoint.
    /// </remarks>
    public string Name { get; set; } = string.Empty;

    public int Points { get; set; }

    public int Kills { get; set; }

    public int Deaths { get; set; }

    public int Assists { get; set; }

    /// <summary>Enemy kills that were headshots.</summary>
    public int Headshots { get; set; }

    /// <summary>Shots that damaged someone. One blast can land several, so this is not shots.</summary>
    public int Hits { get; set; }

    /// <summary>Trigger pulls that damaged nobody. Never <c>shots - hits</c>; see <see cref="PendingShot"/>.</summary>
    public int Misses { get; set; }

    public int RoundWins { get; set; }

    public int RoundLosses { get; set; }

    public int MatchWins { get; set; }

    public int MatchLosses { get; set; }

    public int BombsPlanted { get; set; }

    public int BombsDefused { get; set; }

    /// <summary>Seconds connected, banked up to the last <see cref="CreditPlaytime"/>.</summary>
    /// <remarks>
    /// Counted as K4-LevelRanks counted it — warmup, spectating and an empty server included — so it
    /// is the one counter outside <c>AwardPipeline.IsPlayCounted</c>: it measures presence, not play.
    /// </remarks>
    public long Playtime { get; set; }

    /// <summary><see cref="Playtime"/> plus the running session's whole seconds not yet banked.</summary>
    /// <remarks>What readers show, so someone who joined minutes ago does not read 0 until the next save.</remarks>
    public long LivePlaytime => Playtime + UnbankedSeconds(DateTime.UtcNow.Ticks, Volatile.Read(ref _playtimeSince));

    /// <remarks>UTC ticks where the unbanked stretch began; 0 while nobody is connected on this record.</remarks>
    private long _playtimeSince;

    public DateTime FirstSeen { get; set; }

    public DateTime LastSeen { get; set; }

    /// <summary>Per-player override of <c>Points.ShowPointMessages</c>, from <c>ranks_settings</c>.</summary>
    public bool ShowPointMessages { get; set; } = true;

    /// <summary>Which ladder tier the player is in, 0-based.</summary>
    public int RankIndex { get; set; }

    /// <summary>Kills this round, for the killstreak tiers.</summary>
    public int Killstreak { get; set; }

    /// <summary>When the last kill landed, for <c>Points.SecondsBetweenKills</c>.</summary>
    public DateTime LastKillAt { get; set; }

    /// <summary>Session-only scoreboard overlay (FR-API-07); never persisted.</summary>
    public RanksDisplayOverride? DisplayOverride { get; set; }

    /// <summary>Buffered point lines awaiting the round-end summary.</summary>
    public int PendingSummaryPoints { get; set; }

    /// <summary>A fired shot not yet seen to damage anyone; never persisted.</summary>
    /// <remarks>
    /// Resolved into <see cref="Misses"/> by the next shot. Player-scoped rather than per weapon,
    /// so a grenade whose damage lands after the next rifle shot is attributed to that shot —
    /// Ranks_WeaponStats latches per weapon because it reports per weapon, and this does not.
    /// </remarks>
    public bool PendingShot { get; set; }

    public bool Dirty { get; set; }

    /// <summary>Zeroes every counter and sets the total — the one place progression is cleared.</summary>
    /// <remarks>
    /// Shared by the reset and the period rollover so the two cannot drift: a separate copy of this
    /// list already shipped without <see cref="Headshots"/>, <see cref="Hits"/> and
    /// <see cref="Misses"/> once. <see cref="Name"/>, <see cref="FirstSeen"/> and preferences are
    /// identity, not progression, and survive. A running playtime session restarts here, so time
    /// from before the clear is never banked after it.
    /// </remarks>
    public void ClearProgress(int points, int rankIndex)
    {
        Points = points;
        Kills = 0;
        Deaths = 0;
        Assists = 0;
        Headshots = 0;
        Hits = 0;
        Misses = 0;
        RoundWins = 0;
        RoundLosses = 0;
        MatchWins = 0;
        MatchLosses = 0;
        BombsPlanted = 0;
        BombsDefused = 0;
        Playtime = 0;
        RestartRunningPlaytime();
        Killstreak = 0;
        PendingSummaryPoints = 0;
        RankIndex = rankIndex;
        Dirty = true;
    }

    /// <summary>Banks the playtime so far, queues the figures held now as <see cref="Period"/>'s final ones, then moves into <paramref name="period"/>.</summary>
    public void StartPeriod(long period, int points, int rankIndex)
    {
        CreditPlaytime(DateTime.UtcNow);
        Volatile.Write(ref _pendingArchives, [.. PendingArchives ?? [], PlayerHistoryRow.From(this)]);
        Period = period;
        ClearProgress(points, rankIndex);
    }

    /// <summary>Starts counting connected time, unless a session is already running.</summary>
    /// <remarks>
    /// A no-op on a running clock because both connect triggers reach the same record, and the second
    /// would otherwise discard the seconds since the first.
    /// </remarks>
    public void StartPlaytime(DateTime utcNow) => Interlocked.CompareExchange(ref _playtimeSince, utcNow.Ticks, 0);

    /// <summary>Banks the whole seconds since the session started or was last banked; the remainder carries over.</summary>
    /// <remarks>Game thread only, like every other counter write.</remarks>
    public void CreditPlaytime(DateTime utcNow)
    {
        var since = Volatile.Read(ref _playtimeSince);
        var seconds = UnbankedSeconds(utcNow.Ticks, since);
        if (seconds <= 0) return;

        Playtime += seconds;
        Volatile.Write(ref _playtimeSince, since + seconds * TimeSpan.TicksPerSecond);
        Dirty = true;
    }

    /// <summary>Banks the session so far and stops the clock until the next <see cref="StartPlaytime"/>.</summary>
    public void StopPlaytime(DateTime utcNow)
    {
        CreditPlaytime(utcNow);
        Volatile.Write(ref _playtimeSince, 0);
    }

    private void RestartRunningPlaytime()
    {
        if (Volatile.Read(ref _playtimeSince) != 0) Volatile.Write(ref _playtimeSince, DateTime.UtcNow.Ticks);
    }

    private static long UnbankedSeconds(long nowTicks, long sinceTicks) =>
        sinceTicks == 0 ? 0 : Math.Max(0, (nowTicks - sinceTicks) / TimeSpan.TicksPerSecond);

    /// <summary>Drops the archives a committed write carried, unless a rollover replaced them meanwhile.</summary>
    /// <remarks>
    /// Compare-and-swap rather than a plain clear: a rollover replacing the array between the write
    /// and this call would otherwise lose the period that write never carried. When the swap fails
    /// the replacement still holds what was written, so the cost is one idempotent re-write.
    /// </remarks>
    public void SettleArchives(PlayerHistoryRow[]? written)
    {
        if (written is null) return;

        Interlocked.CompareExchange(ref _pendingArchives, null, written);
    }
}
