using System.ComponentModel;
using Microsoft.Extensions.Configuration;

namespace Ranks.Config;

/// <summary>Everything in <c>config.jsonc</c>.</summary>
/// <remarks>
/// The framework creates the file from this model on first run and only ever reads it
/// afterwards, so these defaults are literally the shipped config — a property without a
/// sensible one ships as a value the operator has to discover and fix.
/// </remarks>
public sealed class RanksConfig
{
    public DatabaseOptions Database { get; set; } = new();
    public RankOptions Rank { get; set; } = new();
    public ScoreboardOptions Scoreboard { get; set; } = new();
    public PointsOptions Points { get; set; } = new();
    public VipOptions Vip { get; set; } = new();
    public GeneralOptions General { get; set; } = new();

    /// <summary>Reads <c>config.jsonc</c>, with <c>Vip.Flags</c> as the file writes it.</summary>
    /// <remarks>
    /// The binder appends a file's array onto a non-empty list, so a replaced flag would still
    /// grant VIP beside the new one. Binding onto an empty list leaves only the file's; the
    /// default stands in only when the file has no Flags key at all.
    /// </remarks>
    internal static RanksConfig Bind(IConfiguration section)
    {
        var config = new RanksConfig { Vip = { Flags = [] } };
        section.Bind(config);
        if (!section.GetSection("Vip:Flags").Exists() && config.Vip.Flags.Count == 0)
            config.Vip.Flags = [VipOptions.DefaultFlag];
        return config;
    }
}

public sealed class DatabaseOptions
{
    /// <remarks>
    /// Names an entry in SwiftlyS2's global <c>configs/database.jsonc</c>; "default" is the
    /// entry a stock install ships. An unknown name silently resolves to the default
    /// connection, so core opens it at boot and throws rather than inheriting that (FR-DB-02).
    /// </remarks>
    public string Connection { get; set; } = "default";

    /// <remarks>Normalised to one trailing underscore, so "awp", "awp_" and "_awp" agree (FR-DB-03).</remarks>
    public string TablePrefix { get; set; } = "";

    /// <remarks>Normalised to one leading underscore (FR-DB-03).</remarks>
    public string TableSuffix { get; set; } = "";

    public int SaveIntervalSeconds { get; set; } = 300;

    /// <remarks><c>0</c> never purges.</remarks>
    public int PurgeDays { get; set; } = 0;

    /// <remarks>
    /// <c>Days</c> follows <c>VirtualPurgeDays</c>; <c>Monthly</c> starts a period on the 1st of
    /// every UTC calendar month (FR-DB-10). Every server sharing a database must use the same value.
    /// <para>
    /// Written to config.jsonc as its number. An out-of-range one binds silently — ConfigValidator
    /// resets it.
    /// </para>
    /// </remarks>
    [Description("0 Days (a new season every VirtualPurgeDays days, none while that is 0), 1 Monthly (a new season at 00:00 UTC on the 1st of every month).")]
    public VirtualPurgeMode VirtualPurgeMode { get; set; } = VirtualPurgeMode.Days;

    /// <remarks>
    /// <c>0</c> never starts a new period. Above zero, standings restart at <c>Rank.StartPoints</c>
    /// every N days on a fixed UTC grid and the closed period is kept as history, not deleted
    /// (FR-DB-10). Under <c>Monthly</c> it only dates the end of a season begun under <c>Days</c>.
    /// Every server sharing a database must use the same value.
    /// </remarks>
    public int VirtualPurgeDays { get; set; } = 0;

    /// <remarks>
    /// <c>0</c> runs roll-up and purge only on a cold load. Above zero, they also run on an
    /// empty server once the interval has elapsed (FR-DB-09).
    /// </remarks>
    public int MaintenanceIntervalHours { get; set; } = 0;
}

public sealed class RankOptions
{
    /// <remarks>
    /// <c>false</c> turns core into a host: its handlers award nothing and its gates and
    /// multipliers go inert, while stat counters, the ladder and the API stay live (FR-PTS-00).
    /// </remarks>
    public bool UseCoreRankingLogic { get; set; } = true;

    /// <remarks>Humans connected, spectators included. Below it nothing is recorded, not only points — playtime aside (FR-PTS-03).</remarks>
    public int MinPlayers { get; set; } = 4;

    /// <remarks>FR-PTS-05's multiplier formula assumes non-negative points, so this cannot go below zero.</remarks>
    public int MinPoints { get; set; } = 0;

    public int StartPoints { get; set; } = 0;

    /// <remarks>
    /// <c>false</c> means warmup does not count at all — no award and no counter of any kind, the
    /// same as play below <see cref="MinPlayers"/>. A warmup round is not the match (FR-PTS-03).
    /// </remarks>
    public bool WarmupPoints { get; set; } = false;

    /// <remarks>
    /// <c>false</c> discards any interaction involving a bot — points, counters, killstreak and
    /// arbiter claims alike — and, outside FFA, all play while a team is made up only of bots: a bot
    /// is not a player, so nothing done against one is a fact about play (FR-PTS-03). Modules read
    /// this through the API rather than carrying their own switch, so one flip covers the whole
    /// plugin set.
    /// </remarks>
    public bool BotsEnabled { get; set; } = false;

    public bool FfaMode { get; set; } = false;
}

/// <summary>How the length of a virtual purge period is decided (FR-DB-10).</summary>
public enum VirtualPurgeMode
{
    /// <summary>Every <c>Database.VirtualPurgeDays</c> days; <c>0</c> turns periods off.</summary>
    Days = 0,

    /// <summary>Every UTC calendar month, from 00:00 on the 1st.</summary>
    Monthly = 1,
}

/// <summary>How the scoreboard renders a player's rank.</summary>
/// <remarks>
/// Plugin-level values, not engine ones. <c>m_iCompetitiveRankType</c> takes a different
/// set and these are translated to it by <c>ScoreboardService.EngineRankType</c> — a wrong
/// engine value renders nothing at all, with no error (FR-SCB-03).
/// </remarks>
public enum RankDisplayMode
{
    Off = 0,
    Premier = 1,
    Competitive = 2,
    Wingman = 3,

    /// <remarks>
    /// Competitive's icon slots driven by ladder position through <c>Scoreboard.CustomRank*</c>,
    /// for servers shipping a replacement icon set. Matches K4-LevelRanks' mode 0 exactly so an
    /// existing icon addon and its base/margin values port over unchanged.
    /// <para>
    /// Was <c>5</c> while <c>4</c> held the removed Danger Zone slot, whose icons stock CS2 does
    /// not ship. A config carrying <c>5</c> is now out of range, so ConfigValidator resets it to
    /// Premier and says so.
    /// </para>
    /// </remarks>
    Custom = 4,
}

public sealed class ScoreboardOptions
{
    /// <remarks>
    /// Premier renders the point total itself as a CS Rating, coloured by CS2's own bands,
    /// which is what makes the seven-band ladder legible without mapping anything.
    /// <para>
    /// Written to config.jsonc as its number. An out-of-range one binds silently — ConfigValidator
    /// resets it (FR-SCB-03).
    /// </para>
    /// </remarks>
    [Description("0 Off, 1 Premier, 2 Competitive, 3 Wingman, 4 Custom.")]
    public RankDisplayMode RankMode { get; set; } = RankDisplayMode.Premier;

    public bool ScoreSync { get; set; } = false;

    public bool Clantags { get; set; } = false;

    /// <remarks>Read by the icon modes (Competitive, Wingman); Premier and Custom ignore it.</remarks>
    public int SkillGroupOffset { get; set; } = 0;

    /// <remarks>First icon index of a replacement set. Custom mode only.</remarks>
    public int CustomRankBase { get; set; } = 0;

    /// <remarks>Ladder position past which every player shares the top icon. Custom mode only.</remarks>
    public int CustomRankMax { get; set; } = 18;

    /// <remarks>Ladder positions skipped before the first icon is used. Custom mode only.</remarks>
    public int CustomRankMargin { get; set; } = 0;

    public RevealOptions Reveal { get; set; } = new();
}

/// <summary>When everyone's rank icon is revealed to everyone else (FR-SCB-04).</summary>
/// <remarks>
/// All three triggers default on because the reveal does not persist: CS2 sends it once at map
/// load, so a single broadcast works for one round and then quietly stops. There is no matching
/// "hide" message, which is why every key here is about <em>when</em> to re-send rather than what.
/// </remarks>
public sealed class RevealOptions
{
    [Description("Re-broadcast at the start of every round.")]
    public bool OnRoundStart { get; set; } = true;

    [Description("Send to a player as they join, so they see ranks without waiting for the next round.")]
    public bool OnPlayerJoin { get; set; } = true;

    [Description("Re-broadcast when a player's rank changes, since it is everyone else's view that is now stale.")]
    public bool OnRankChange { get; set; } = true;

    [Description("Seconds to wait before sending to a joining player. Too early and the client is not ready to act on it.")]
    public float JoinDelaySeconds { get; set; } = 2.0f;
}

public sealed class PointsOptions
{
    public bool DynamicKillPoints { get; set; } = true;

    public double DynamicKillMin { get; set; } = 0.5;

    public double DynamicKillMax { get; set; } = 3.0;

    public bool DynamicDeathPoints { get; set; } = true;

    public double DynamicDeathMin { get; set; } = 0.5;

    public double DynamicDeathMax { get; set; } = 3.0;

    public int LongDistanceUnits { get; set; } = 2000;

    /// <remarks><c>0</c> puts no time limit on a streak; the round already bounds it.</remarks>
    public int SecondsBetweenKills { get; set; } = 0;

    /// <remarks>Streaks are per-round accumulation, so carrying one across a round boundary is the exception.</remarks>
    public bool KillstreakResetOnRoundEnd { get; set; } = true;

    public bool KillStreaksEnabled { get; set; } = true;

    /// <remarks>Buffers the per-action lines and emits one aggregated line at round end instead.</remarks>
    public bool RoundEndSummary { get; set; } = false;

    /// <remarks>The server default; a player overrides it under Settings in the <c>!rank</c> menu.</remarks>
    public bool ShowPointMessages { get; set; } = true;

    public bool ShowRankChangeMessages { get; set; } = true;
}

public sealed class VipOptions
{
    public double Multiplier { get; set; } = 1.25;

    /// <remarks>
    /// Namespaced to this plugin so it cannot collide with another's grants, and awards
    /// nothing until an operator grants it — discoverable without being on by accident.
    /// The value seeds the generated file; <see cref="RanksConfig.Bind"/> keeps the file's list
    /// from being appended to it.
    /// </remarks>
    public List<string> Flags { get; set; } = [DefaultFlag];

    internal const string DefaultFlag = "@ranks/vip";
}

public sealed class GeneralOptions
{
    /// <remarks>
    /// Gates successful-flow narration only. Warnings, errors and thrown exceptions are
    /// never suppressed by it.
    /// </remarks>
    public bool DetailedLogging { get; set; } = false;
}
