using Microsoft.Extensions.Logging;
using Ranks.Config;
using Ranks.Contract;
using Ranks.Players;
using Ranks.Ranking;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Players;

namespace Ranks.Scoreboard;

/// <summary>Writes a player's rank onto the scoreboard.</summary>
/// <remarks>
/// Every networked write is paired with its notifier. Without it the value changes server-side and
/// is never networked — the scoreboard keeps rendering the old one, and the bug looks exactly like
/// a failed points calculation.
/// <para>
/// The clan tag and score follow events, but the rank triple is re-asserted from
/// <c>CCSPlayerController::ResourceDataThink</c>: CS2's scoreboard-refresh loop clears those
/// controller fields between snapshots, so writing them only on change leaves the icon blank
/// until the next rank change. That think is where the engine rebuilds the data the scoreboard
/// reads, and it runs per controller on the engine's own schedule rather than once per tick.
/// The change detector in <see cref="WriteRankIcon"/> compares the live fields, so a steady
/// state costs three reads and fires no notifier.
/// </para>
/// </remarks>
internal sealed class ScoreboardService : IDisposable
{
    private readonly ISwiftlyCore _core;
    private readonly ConfigHost<RanksConfig> _config;
    private readonly RankLadder _ladder;
    private readonly PlayerRegistry _registry;
    private readonly ILogger _logger;

    private bool _hooked;

    public ScoreboardService(
        ISwiftlyCore core,
        ConfigHost<RanksConfig> config,
        RankLadder ladder,
        PlayerRegistry registry,
        ILogger logger)
    {
        _core = core;
        _config = config;
        _ladder = ladder;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>The tag core would write, or null when tags are off or the player is unknown.</summary>
    /// <remarks>
    /// Read-only and side-effect free by contract: a tag arbiter has to be able to ask without
    /// fighting the thing it is arbitrating (FR-API-02). Honours the display overlay, or an
    /// arbiter and core would disagree about the same player.
    /// </remarks>
    public string? DesiredClanTag(ulong steamId)
    {
        if (!_config.Current.Scoreboard.Clantags) return null;

        return _registry.BySteamId(steamId) is { } record ? ResolveTag(record) : null;
    }

    private string ResolveTag(PlayerRecord record)
    {
        var overlay = record.DisplayOverride;
        var tag = _ladder.TierFor(overlay?.Points ?? record.Points).Tag;

        return overlay?.TagPrefix is { Length: > 0 } prefix ? prefix + tag : tag;
    }

    public void Apply(ulong steamId)
    {
        var player = _core.PlayerManager.GetPlayerFromSteamId(steamId, allowUnauthorized: false);

        if (player is not null) Apply(player);
    }

    public void Apply(IPlayer player)
    {
        var record = _registry.BySlot(player.Slot);
        if (record is null) return;

        var config = _config.Current;

        try
        {
            var controller = player.Controller;
            if (controller is null) return;

            var displayPoints = record.DisplayOverride?.Points ?? record.Points;

            if (config.Scoreboard.Clantags) WriteClanTag(player, controller, ResolveTag(record));

            if (config.Scoreboard.ScoreSync)
            {
                controller.Score = displayPoints;
                controller.ScoreUpdated();
            }

            WriteRankIcon(config, controller, record, displayPoints);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks: could not update the scoreboard for {SteamId}.", player.SteamID);
        }
    }

    public void ApplyAll()
    {
        foreach (var player in _core.PlayerManager.GetAllPlayers())
            if (!player.IsFakeClient)
                Apply(player);
    }

    /// <summary>Starts the rank re-assert on the controller's resource-data think. Idempotent.</summary>
    public void Start()
    {
        if (_hooked) return;

        _core.GameHooks.Datamaps.CCSPlayerController.ResourceDataThink.Post += ReassertRank;
        _hooked = true;
    }

    public void Dispose()
    {
        if (!_hooked) return;

        _core.GameHooks.Datamaps.CCSPlayerController.ResourceDataThink.Post -= ReassertRank;
        _hooked = false;
    }

    /// <remarks>
    /// Deliberately only the rank triple — not the clan tag, and not the score. Score sync has no
    /// change detector of its own, so running it here would fire ScoreUpdated on every think.
    /// <para>
    /// Post, not Pre: this think is what rebuilds the scoreboard's resource data, so a Pre write
    /// is recomputed over before anything renders and the icon never appears.
    /// </para>
    /// </remarks>
    private void ReassertRank(ref CCSPlayerControllerResourceDataThinkPostContext ctx)
    {
        var config = _config.Current;

        if (config.Scoreboard.RankMode == RankDisplayMode.Off) return;

        try
        {
            var controller = ctx.SchemaObject;
            if (controller is not { IsValid: true }) return;

            var player = _core.PlayerManager.GetPlayerFromController(controller);
            if (player is null || player.IsFakeClient) return;

            var record = _registry.BySlot(player.Slot);
            if (record is null) return;

            WriteRankIcon(config, controller, record, record.DisplayOverride?.Points ?? record.Points);
        }
        catch (Exception ex)
        {
            // A throw here would propagate into the engine's think.
            _logger.LogWarning(ex, "Ranks: could not re-assert the scoreboard rank.");
        }
    }

    private void WriteClanTag(IPlayer player, SwiftlyS2.Shared.SchemaDefinitions.CCSPlayerController controller, string tag)
    {
        if (controller.Clan == tag) return;

        controller.Clan = tag;
        controller.ClanUpdated();

        // The scoreboard will not pick the tag up until its next natural redraw without this.
        _core.GameEvent.FireToPlayer<EventNextlevelChanged>(player.Slot);
    }

    /// <remarks>
    /// In Premier mode CompetitiveRanking carries the point total itself and CS2 colours it by its
    /// own band, so SkillGroup and the offset are ignored — and that is what makes a seven-band
    /// ladder legible, since the number moves every round while the colour does not.
    /// <para>
    /// Everything here resolves from <paramref name="displayPoints"/> rather than the stored total,
    /// so a display overlay moves the icon and the tier with the number instead of only the number
    /// (FR-API-07) — a prestige deduction that left the top-tier icon in place was the whole bug.
    /// </para>
    /// </remarks>
    private void WriteRankIcon(
        RanksConfig config,
        SwiftlyS2.Shared.SchemaDefinitions.CCSPlayerController controller,
        PlayerRecord record,
        int displayPoints)
    {
        var mode = ResolveMode(config, record.DisplayOverride);

        if (mode == RankDisplayMode.Off) return;

        var value = IconValue(config, mode, displayPoints);

        var rankType = EngineRankType(mode);

        // CS2 folds the wins count into the icon derivation, so leaving it at whatever the engine
        // last held collapses every player onto the same displayed rank.
        var wins = 10;

        // All three fields are networked, and a module refreshing per points change makes this run
        // every kill. Valve's scoreboard delta detection means the notifier IS the replication
        // trigger, so firing it on an unchanged value is pure cost to every client with the
        // scoreboard open. Comparing the live fields rather than a cached copy is also what lets
        // the per-tick pass notice the engine clearing them and re-assert.
        if (controller.CompetitiveRankType == rankType
            && controller.CompetitiveRanking == value
            && controller.CompetitiveWins == wins) return;

        controller.CompetitiveRankType = rankType;
        controller.CompetitiveRankTypeUpdated();

        controller.CompetitiveRanking = value;
        controller.CompetitiveRankingUpdated();

        controller.CompetitiveWins = wins;
        controller.CompetitiveWinsUpdated();
    }

    /// <summary>The overlay's mode when it names a valid one, else the configured mode.</summary>
    /// <remarks>
    /// An unparseable name is ignored rather than treated as Off: a module typo should not silently
    /// blank every scoreboard, and the string crosses an assembly boundary where no enum can.
    /// IsDefined is part of that: TryParse also accepts numbers, so without it a stale "5" would
    /// resolve to no member at all and EngineRankType would map it to 0 — no rank.
    /// </remarks>
    internal static RankDisplayMode ResolveMode(RanksConfig config, RanksDisplayOverride? overlay) =>
        overlay?.RankMode is { Length: > 0 } name
        && Enum.TryParse<RankDisplayMode>(name, ignoreCase: true, out var mode)
        && Enum.IsDefined(mode)
            ? mode
            : config.Scoreboard.RankMode;

    private const int MaxSkillGroup = 18;

    /// <summary>The value written to <c>CompetitiveRanking</c> for a point total.</summary>
    /// <remarks>Shared with <c>IRanksApi.GetRankIcon</c>, so a menu drawing the icon cannot drift from the scoreboard.</remarks>
    internal int IconValue(RanksConfig config, RankDisplayMode mode, int points)
    {
        var index = _ladder.IndexFor(points);

        return mode switch
        {
            RankDisplayMode.Premier => points,
            RankDisplayMode.Competitive => CompetitiveIcon(config, _ladder.Tiers[index].SkillGroup, MaxSkillGroup),
            RankDisplayMode.Wingman => CompetitiveIcon(config, _ladder.Tiers[index].SkillGroup, MaxSkillGroup),
            RankDisplayMode.Custom => CustomIcon(config.Scoreboard, index + 1),
            _ => points,
        };
    }

    /// <summary>Translates the plugin-level mode to the engine's <c>m_iCompetitiveRankType</c>.</summary>
    /// <remarks>
    /// The engine's set is not <c>0</c>–<c>4</c>, and writing an unrecognised value renders nothing
    /// at all with no error (FR-SCB-03). These are the values K4-LevelRanks ships and runs in
    /// production; confirm any addition on a live server rather than inferring it.
    /// </remarks>
    internal static byte EngineRankType(RankDisplayMode mode) => mode switch
    {
        RankDisplayMode.Premier => 11,
        RankDisplayMode.Competitive => 12,
        RankDisplayMode.Wingman => 7,
        RankDisplayMode.Custom => 12,
        _ => 0,
    };

    /// <remarks>
    /// Reads the ladder entry's own SkillGroup rather than its position, so an 18-tier ladder need
    /// not map 1:1 onto CS2's 18 skill groups and several tiers may deliberately share an icon.
    /// Clamped <em>after</em> the offset, which exists for servers shipping a replacement icon set
    /// whose indices start at a different base.
    /// </remarks>
    private static int CompetitiveIcon(RanksConfig config, int skillGroup, int ceiling) =>
        Math.Clamp(skillGroup + config.Scoreboard.SkillGroupOffset, 0, ceiling);

    /// <summary>K4-LevelRanks' mode-0 mapping, reproduced so an existing icon addon ports over.</summary>
    /// <remarks>
    /// Position-based rather than per-tier because that is what the replacement icon sets in the
    /// wild are indexed against. <paramref name="rankId"/> is 1-based, matching K4's <c>GetRankId</c>.
    /// </remarks>
    internal static int CustomIcon(ScoreboardOptions scoreboard, int rankId)
    {
        var value = rankId > scoreboard.CustomRankMax
            ? scoreboard.CustomRankBase + scoreboard.CustomRankMax - scoreboard.CustomRankMargin
            : scoreboard.CustomRankBase + (rankId - scoreboard.CustomRankMargin - 1);

        return Math.Max(0, value);
    }
}
