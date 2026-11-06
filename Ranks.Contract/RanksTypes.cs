namespace Ranks.Contract;

/// <summary>An immutable view of a player's record, taken at the moment of the call.</summary>
/// <remarks>
/// Carries no leaderboard position: everything here is already resident, so building one is
/// allocation-only, while "47th of 3812" needs a query — <c>GetPositionAsync</c> (FR-API-02).
/// </remarks>
public sealed record RanksPlayerSnapshot(
    ulong SteamId,
    string Name,
    int Points,
    string RankName,
    string RankTag,
    int RankIndex,
    long Kills,
    long Deaths,
    long Assists,
    long RoundWins,
    long RoundLosses,
    long MatchWins,
    long MatchLosses,
    long BombsPlanted,
    long BombsDefused,
    DateTime FirstSeen,
    DateTime LastSeen)
{
    /// <remarks>Properties rather than positional parameters: the record's constructor is part of the frozen contract.</remarks>
    public long Headshots { get; init; }

    /// <summary>Hits plus misses is the shot count — see <c>GetStat</c> for why there is no stored shot total.</summary>
    public long Hits { get; init; }

    public long Misses { get; init; }
}

/// <summary>One rung of the operator's ladder.</summary>
/// <param name="Index">0-based, lowest first — the same index as <see cref="RanksPlayerSnapshot.RankIndex"/>.</param>
/// <param name="Name">The tier's display name.</param>
/// <param name="Tag">The short form core writes into the clan tag.</param>
/// <param name="Points">Inclusive threshold.</param>
/// <param name="Hex">The colour the operator set for menus, e.g. <c>#B0C3D9</c>.</param>
public sealed record RanksTier(int Index, string Name, string Tag, int Points, string Hex);

/// <summary>What the scoreboard draws for a point total.</summary>
/// <param name="Mode">One of <see cref="RanksDisplayModes"/>.</param>
/// <param name="Value">What core writes to the scoreboard: the point total in Premier, an icon index in the other modes, 0 when Off.</param>
public sealed record RanksRankIcon(string Mode, int Value);

/// <summary>One row of a leaderboard query.</summary>
public sealed record RanksLeaderboardEntry(
    int Position,
    ulong SteamId,
    string Name,
    long Value,
    string RankName,
    string RankTag);

/// <summary>Raised after a point change has been applied.</summary>
public sealed record RanksPointsChangedArgs(
    ulong SteamId,
    int OldPoints,
    int NewPoints,
    int Delta,
    string Reason);

/// <summary>Raised when a player crosses a ladder threshold in either direction.</summary>
public sealed record RanksRankChangedArgs(
    ulong SteamId,
    int Points,
    string OldRankName,
    string NewRankName,
    int OldRankIndex,
    int NewRankIndex,
    bool Promoted);

/// <summary>A mutable award, raised before core writes it.</summary>
/// <remarks>
/// A class rather than a record struct because subscribers mutate it — a struct would be copied
/// per subscriber and every change discarded. Fires for core's own awards only, never for
/// <c>AddPoints</c> / <c>SetPoints</c> (FR-API-04).
/// </remarks>
public sealed class RanksPointsChangingArgs
{
    public required ulong SteamId { get; init; }

    /// <summary>Core's event key, e.g. <c>death</c>.</summary>
    public required string EventKey { get; init; }

    /// <summary>Attacker on a death, victim on a kill; null when there is no counterparty.</summary>
    public required ulong? CounterpartySteamId { get; init; }

    /// <summary>Configured value multiplied by occurrence count, before any multiplier.</summary>
    public required int BaseAmount { get; init; }

    /// <remarks>Subscribers run in subscription order and each sees the previous one's value.</remarks>
    public int Amount { get; set; }

    /// <summary>Suppresses the write entirely; <c>PointsChanged</c> does not then fire.</summary>
    public bool Cancel { get; set; }
}

/// <summary>A snapshot of every point-related setting the operator configured.</summary>
/// <remarks>
/// Readable in both modes — with <c>UseCoreRankingLogic</c> off core applies none of these itself,
/// so a rating module reads them as the operator's intent rather than as core's behaviour
/// (FR-API-05).
/// </remarks>
public sealed record RanksPointSettings(
    bool UseCoreRankingLogic,
    int MinPlayers,
    bool WarmupPoints,
    bool BotsEnabled,
    bool FfaMode,
    double VipMultiplier,
    IReadOnlyList<string> VipFlags,
    bool DynamicKillPoints,
    double DynamicKillMin,
    double DynamicKillMax,
    bool DynamicDeathPoints,
    double DynamicDeathMin,
    double DynamicDeathMax,
    int StartPoints,
    int MinPoints,
    IReadOnlyDictionary<string, int> EventValues);

/// <summary>What the scoreboard should show for one player, instead of their stored standing.</summary>
/// <remarks>
/// Every field is optional and independently overridable; a null field keeps core's own answer.
/// This is the mechanism a prestige-style module uses: deduct from <see cref="Points"/> and the
/// tier, icon and tag all follow, because core resolves them from the displayed value rather than
/// the stored one. Nothing here is written to the database (FR-API-07).
/// </remarks>
/// <param name="Points">Points to display and to resolve the ladder tier from.</param>
/// <param name="RankMode">One of <see cref="RanksDisplayModes"/>; unparseable values are ignored.</param>
/// <param name="TagPrefix">Prepended to the tier's clan tag, so core still owns the field.</param>
public sealed record RanksDisplayOverride(
    int? Points = null,
    string? RankMode = null,
    string? TagPrefix = null);

/// <summary>Values accepted by <see cref="RanksDisplayOverride.RankMode"/>.</summary>
/// <remarks>
/// Strings rather than an enum because the enum lives in core's own assembly, which a module does
/// not reference. Core parses these by name, so they must match <c>RankDisplayMode</c> exactly.
/// </remarks>
public static class RanksDisplayModes
{
    public const string Off = "Off";
    public const string Premier = "Premier";
    public const string Competitive = "Competitive";
    public const string Wingman = "Wingman";
    public const string Custom = "Custom";
}
