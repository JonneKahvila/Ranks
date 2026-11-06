using Ranks.EloPoints.Rating;

namespace Ranks.EloPoints.Match;

/// <summary>One player's accrual for one match.</summary>
/// <remarks>
/// Keyed on SteamID rather than slot, unlike the slot array a per-bullet hot path would want: a
/// session has to survive a disconnect so a reconnect resumes it, and the slot is cleared and
/// reissued on disconnect. Touched once per player per round, so the dictionary lookup is free.
///
/// Game thread only. Settling detaches, so the store never sees a live session.
/// </remarks>
internal sealed class MatchSession
{
    public required ulong SteamId { get; init; }

    /// <summary>The current period's rating: the module's table, else core's points, else StartPoints.</summary>
    public double Rating;

    /// <summary>False until the read lands — an unresolved player is left out of team averages.</summary>
    public bool RatingResolved;

    /// <summary>Bumped whenever the rating is seeded again, so a read begun before that cannot land on top of it.</summary>
    /// <remarks>A new period and a reset both re-seed; a read still in flight from before either holds the wiped rating.</remarks>
    public int RatingGeneration;

    public int RoundsPresent;

    public double SumActual;

    public double SumExpected;

    /// <summary>Core's counters at the moment this player entered the match.</summary>
    /// <remarks>Null when the player joined before core's record was resident; they score at the median.</remarks>
    public PerfCounters? StartCounters;

    /// <summary>Last team seen, so a disconnected player can still be attributed at settle time.</summary>
    public int LastTeam;

    /// <summary>False once disconnected or moved to spectator; true again on rejoin.</summary>
    public bool Present = true;
}

/// <summary>One player's settled rating, handed to the store.</summary>
/// <remarks>
/// <see cref="Period"/> is captured on the game thread at settle time and travels with the write, so
/// a retry that lands after a rollover still goes to the period the match was played in (FR-DB-10).
/// </remarks>
internal readonly record struct RatingWrite(ulong SteamId, long Period, double Rating, bool Abandoned);
