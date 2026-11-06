namespace Ranks.EloPoints.Contract;

/// <summary>One player's Elo standing.</summary>
/// <remarks>
/// <paramref name="Rating"/> is the authoritative value and is fractional — core's <c>points</c>
/// holds the rounded form for the ladder and the scoreboard, so the two legitimately differ by up
/// to half a point.
/// </remarks>
public sealed record EloStanding(
    ulong SteamId,
    decimal Rating,
    int Matches,
    int Abandons,
    DateTime UpdatedAt);

/// <summary>One resolved match, newest first when returned in a list.</summary>
public sealed record EloMatchResult(
    ulong SteamId,
    decimal Delta,
    decimal RatingAfter,
    int RoundsPresent,
    bool Abandoned,
    DateTime SettledAt);

/// <summary>Read access to <c>Ranks_EloPoints</c>'s ratings.</summary>
/// <remarks>
/// Additive only after 1.0, for the same reason as <c>Ranks.Contract</c>: there is no package
/// registry and therefore no version negotiation, so a renamed or re-signed member is a
/// <c>MissingMethodException</c> at call time rather than a build error anyone sees.
///
/// Unlike WeaponStats there is no unflushed-delta problem to solve: a rating is written once, at
/// match end, so the stored value is always current. A match in progress is deliberately not
/// visible here — it has no result until it ends, and exposing a provisional one would invite
/// callers to treat it as settled.
///
/// Ratings follow Ranks' periods (seasons): each period starts everyone unrated, and a closed one
/// keeps its final ratings. A period id is Ranks' own — <c>IRanksApi.CurrentPeriod</c> and
/// <c>IRanksApi.GetPeriodsAsync</c> hand them out; this contract does not reference Ranks.Contract,
/// so it stays independent of the Ranks version. With periods off there is only ever one.
/// </remarks>
public interface IEloPointsApi
{
    /// <summary>The shared-interface key consumers bind against.</summary>
    const string InterfaceKey = "Ranks.EloPoints.Api.v1";

    /// <summary>Ranks' all-time sentinel. A rating has no all-time figure, so every read given it answers null or an empty list.</summary>
    /// <remarks>
    /// The same value as <c>RanksPeriods.AllTime</c>, so one period id can go to both APIs. A rating
    /// is a level rather than a running total — one season's 1450 plus the next season's 1380 means
    /// nothing — so there is no figure to return.
    /// </remarks>
    const long AllTime = -1;

    /// <summary>A player's standing in the current period, or null when they have not completed a rated match in it.</summary>
    Task<EloStanding?> GetStandingAsync(ulong steamId);

    /// <summary>A player's standing in one period — their final one for a closed period — or null when they hold none there.</summary>
    Task<EloStanding?> GetStandingAsync(ulong steamId, long period);

    /// <summary>The current period's highest-rated players, descending.</summary>
    Task<IReadOnlyList<EloStanding>> GetTopAsync(int limit = 10);

    /// <summary>One period's highest-rated players, descending — final ratings for a closed period.</summary>
    Task<IReadOnlyList<EloStanding>> GetTopAsync(int limit, long period);

    /// <summary>True when a rated match is currently in progress and this player is in it.</summary>
    /// <remarks>
    /// Synchronous because it reads the live session set, not the database. Lets a HUD show "rating
    /// pending" rather than a stale number mid-match.
    /// </remarks>
    bool IsInRatedMatch(ulong steamId);
}
