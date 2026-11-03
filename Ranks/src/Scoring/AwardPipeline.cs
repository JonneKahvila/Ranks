using Microsoft.Extensions.Logging;
using Ranks.Api;
using Ranks.Config;
using Ranks.Contract;
using Ranks.Logging;
using Ranks.Players;
using Ranks.Ranking;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace Ranks.Scoring;

/// <summary>Core's award path: the single place a point value becomes a stored number.</summary>
/// <remarks>
/// Not public API — a module reaches AddPoints / SetPoints instead (FR-API-02). The seven steps of
/// FR-PTS-02 live here in one method so the ordering is readable; splitting them across call sites
/// is how rounding gets applied twice.
/// </remarks>
internal sealed class AwardPipeline
{
    private readonly ISwiftlyCore _core;
    private readonly ConfigHost<RanksConfig> _config;
    private readonly ConfigHost<PointsConfig> _points;
    private readonly RankLadder _ladder;
    private readonly PlayerRegistry _registry;
    private readonly RanksEventHub _events;
    private readonly DetailLog _detail;
    private readonly ILogger _logger;
    private IRanksKillArbiter? _killArbiter;

    /// <remarks>Only defeats a tick counter that froze or restarted; no single tick lasts this long.</remarks>
    private const long StaleGateMs = 1000;

    private int? _playCountedTick;
    private long _playCountedAt;
    private bool _playCounted;

    public AwardPipeline(
        ISwiftlyCore core,
        ConfigHost<RanksConfig> config,
        ConfigHost<PointsConfig> points,
        RankLadder ladder,
        PlayerRegistry registry,
        RanksEventHub events,
        DetailLog detail,
        ILogger logger)
    {
        _core = core;
        _config = config;
        _points = points;
        _ladder = ladder;
        _registry = registry;
        _events = events;
        _detail = detail;
        _logger = logger;
    }

    /// <summary>Whether every gate is currently satisfied (FR-API-05).</summary>
    public bool ArePointsEnabled => _config.Current.Rank.UseCoreRankingLogic && IsPlayCounted;

    /// <summary>Whether play counts at all (FR-PTS-03): the server-wide half of every gate.</summary>
    /// <remarks>
    /// Gates every counter as well as every award; a handler writing a counter without it records
    /// play the points refuse. Decided once per server tick, so every handler of one event — core's
    /// and each module's — gets the same answer: a time-based cache could expire between a kill's
    /// stat and its award and split them.
    /// </remarks>
    public bool IsPlayCounted
    {
        get
        {
            var tick = CurrentTick();
            var now = Environment.TickCount64;

            if (tick is not null && tick == _playCountedTick && now - _playCountedAt < StaleGateMs) return _playCounted;

            _playCounted = EvaluatePlayGate(_config.Current.Rank);
            _playCountedTick = tick;
            _playCountedAt = now;

            return _playCounted;
        }
    }

    /// <summary>Whether a death or a hit is a fact about play at all (FR-PTS-03).</summary>
    /// <remarks>
    /// Drops the event outright rather than only its award, so no counter, killstreak or arbiter
    /// claim derives from it. Suppressing only the award left killstreak tiers payable against bots:
    /// <c>AwardKillstreak</c> has no counterparty, so the bot gate never saw one.
    /// </remarks>
    public bool ShouldRecordDeath(bool victimIsBot, bool attackerIsBot) =>
        IsPlayCounted && (_config.Current.Rank.BotsEnabled || !(victimIsBot || attackerIsBot));

    /// <summary>Awards a configured event to a player, applying gates and multipliers.</summary>
    /// <remarks>
    /// Returns silently when core's scoring is off: with core not awarding, its gates and
    /// multipliers have nothing to act on, so they are inert rather than quietly running against
    /// module writes (FR-PTS-00).
    /// </remarks>
    public void Award(
        PlayerRecord record,
        string eventKey,
        int count = 1,
        ulong? counterparty = null,
        bool counterpartyIsBot = false,
        ScalingKind scaling = ScalingKind.None)
    {
        var config = _config.Current;

        if (!config.Rank.UseCoreRankingLogic) return;

        // 1. Gates. A handler counting a fact checks IsPlayCounted before counting it (FR-PTS-03);
        //    these checks guard award-only events and every other caller.
        if (!PassesGates(config, counterpartyIsBot)) return;

        // 2. Base value.
        var configured = _points.Current.Value(eventKey);
        if (configured == 0) return;

        AwardResolved(config, record, eventKey, configured * count, displayText: null, counterparty, scaling);
    }

    /// <summary>Registers the kill arbiter, or clears it (FR-API-11).</summary>
    public void SetKillArbiter(IRanksKillArbiter? arbiter) => _killArbiter = arbiter;

    /// <summary>Asks the arbiter what it wants done with a kill. Null when none is registered.</summary>
    /// <remarks>
    /// Called for every death core counts, even when no points will follow: an arbiter's counters
    /// track core's counters, not its awards (FR-PTS-03).
    /// </remarks>
    public RanksKillClaim? ClaimKill(RanksKillContext context)
    {
        if (_killArbiter is null) return null;

        try
        {
            return _killArbiter.ClaimKill(context);
        }
        catch (Exception ex)
        {
            // A throwing arbiter must not cost the player the kill core was about to award.
            _logger.LogError(ex, "Ranks: the kill arbiter threw; core is awarding the kill itself.");
            return null;
        }
    }

    /// <summary>Awards an arbiter's claim through core's own gates, multipliers and messaging.</summary>
    /// <remarks>
    /// Deliberately not <see cref="AddPoints"/>: routing it here is what stops every arbiter
    /// reimplementing the gates, the VIP multiplier, the single rounding, the per-player
    /// XP-messages preference and round-end batching (FR-API-11). Dynamic kill scaling
    /// is left off — it prices a kill by rank gap, and a claim is already a considered number.
    /// </remarks>
    public void AwardClaim(PlayerRecord record, RanksKillClaim claim, ulong? counterparty, bool counterpartyIsBot)
    {
        var config = _config.Current;

        if (!config.Rank.UseCoreRankingLogic) return;
        if (!PassesGates(config, counterpartyIsBot)) return;
        if (claim.Points == 0) return;

        AwardResolved(config, record, claim.Reason, claim.Points, claim.DisplayText, counterparty, ScalingKind.None);
    }

    private void AwardResolved(
        RanksConfig config,
        PlayerRecord record,
        string eventKey,
        int baseAmount,
        string? displayText,
        ulong? counterparty,
        ScalingKind scaling)
    {
        // 3. Subscribers may rescale or cancel.
        var changing = _events.RaisePointsChanging(new RanksPointsChangingArgs
        {
            SteamId = record.SteamId,
            EventKey = eventKey,
            CounterpartySteamId = counterparty,
            BaseAmount = baseAmount,
            Amount = baseAmount,
        });

        if (changing.Cancel) return;

        // 4. Core's two multipliers, multiplied together.
        var multiplier = VipMultiplier(config, record.SteamId) * ScalingMultiplier(config, scaling, record, counterparty);

        // 5. Round away from zero, once, after every multiplier — rounding per stage compounds
        //    error, and rounding toward zero turns a small penalty into a free death.
        var delta = (int)Math.Round(changing.Amount * multiplier, MidpointRounding.AwayFromZero);
        if (delta == 0) return;

        Apply(record, delta, eventKey, showMessage: true, counterparty, displayText);
    }

    /// <summary>Adds an already-computed amount. Never gated, multiplied or scaled.</summary>
    /// <remarks>
    /// Raw in both modes, which is what makes additive modules composable: the number a module
    /// hands over is the number that lands, so two of them cannot interact through a shared
    /// multiplier stage.
    /// </remarks>
    public void AddPoints(PlayerRecord record, int amount, string reason, bool showMessage)
    {
        if (amount == 0) return;

        Apply(record, amount, reason, showMessage);
    }

    /// <summary>Replaces a player's total outright.</summary>
    /// <remarks>
    /// Absolute, so exactly one authority may be active — core, or one rating module, never both.
    /// Core cannot detect the combination without tracking who is scoring, so that stays an
    /// operator constraint (FR-PTS-00).
    /// </remarks>
    public void SetPoints(PlayerRecord record, int points, string reason)
    {
        var floor = _config.Current.Rank.MinPoints;
        var target = Math.Max(points, floor);

        if (target == record.Points) return;

        Apply(record, target - record.Points, reason, showMessage: false);
    }

    private void Apply(PlayerRecord record, int delta, string reason, bool showMessage, ulong? counterparty = null, string? displayText = null)
    {
        var config = _config.Current;
        var oldPoints = record.Points;
        var oldIndex = record.RankIndex;

        // 6. Floor. A negative-point ladder is not supported, and the dynamic formula assumes
        //    non-negative points.
        record.Points = Math.Max(oldPoints + delta, config.Rank.MinPoints);
        record.Dirty = true;

        var applied = record.Points - oldPoints;
        if (applied == 0) return;

        record.RankIndex = _ladder.IndexFor(record.Points);

        _detail.Write("Ranks: {SteamId} {Delta:+#;-#;0} for {Reason} -> {Points}.", record.SteamId, applied, reason, record.Points);

        _events.RaisePointsChanged(new RanksPointsChangedArgs(record.SteamId, oldPoints, record.Points, applied, reason));

        if (showMessage) ReportPoints(config, record, applied, reason, displayText);

        if (record.RankIndex != oldIndex) ReportRankChange(config, record, oldIndex);
    }

    private bool PassesGates(RanksConfig config, bool counterpartyIsBot)
    {
        if (!IsPlayCounted) return false;

        // Bot-ness is passed in rather than inferred from the registry: a bot is never tracked, so
        // its SteamID is never a resident record and "not in the registry" cannot tell a bot apart
        // from an event that simply has no counterparty. This gate decides whether a human earns
        // from a bot, never whether the bot itself is tracked.
        if (!config.Rank.BotsEnabled && counterpartyIsBot) return false;

        return true;
    }

    private double VipMultiplier(RanksConfig config, ulong steamId)
    {
        foreach (var flag in config.Vip.Flags)
            if (_core.Permission.PlayerHasPermission(steamId, flag))
                return config.Vip.Multiplier;

        return 1.0;
    }

    private double ScalingMultiplier(RanksConfig config, ScalingKind scaling, PlayerRecord record, ulong? counterparty)
    {
        if (scaling == ScalingKind.None) return 1.0;

        var (enabled, min, max) = scaling == ScalingKind.Kill
            ? (config.Points.DynamicKillPoints, config.Points.DynamicKillMin, config.Points.DynamicKillMax)
            : (config.Points.DynamicDeathPoints, config.Points.DynamicDeathMin, config.Points.DynamicDeathMax);

        if (!enabled) return 1.0;

        // No counterparty is suicide, world or fall damage — there is nothing to compare against,
        // so the configured value applies as written.
        if (counterparty is not { } otherId) return 1.0;

        var other = _registry.BySteamId(otherId);
        var otherPoints = other?.Points ?? 0;

        var (victim, attacker) = scaling == ScalingKind.Kill
            ? (otherPoints, record.Points)
            : (record.Points, otherPoints);

        return DynamicScaling.Multiplier(DynamicScaling.Difference(victim, attacker), min, max);
    }

    private void ReportPoints(RanksConfig config, PlayerRecord record, int delta, string reason, string? displayText = null)
    {
        if (!config.Points.ShowPointMessages || !record.ShowPointMessages) return;

        // Buffered rather than sent, so a busy round produces one line instead of forty.
        if (config.Points.RoundEndSummary)
        {
            record.PendingSummaryPoints += delta;
            return;
        }

        var player = FindPlayer(record.SteamId);
        if (player is null) return;

        Chat.Send(_core, player, delta > 0 ? "XP.gained" : "XP.lost",
            Math.Abs(delta), DescribeReason(reason, displayText));
    }

    /// <remarks>
    /// Only core's own keys are translated. A module calling AddPoints with its own reason gets
    /// that string shown verbatim — core cannot ship a translation key for a reason it has never
    /// heard of, and a missing key renders the literal {lang}.{key} marker to the player. An
    /// arbiter avoids that by resolving its own text and passing it as <c>DisplayText</c>.
    /// Membership in the closed catalogue is the test, not a key prefix: core's keys are bare
    /// since the rename, so nothing about the string itself identifies one.
    /// </remarks>
    private string DescribeReason(string reason, string? displayText = null) =>
        displayText
        ?? (PointEvents.Defaults.ContainsKey(reason) ? _core.Localizer[$"event.{reason}"] : reason);

    private void ReportRankChange(RanksConfig config, PlayerRecord record, int oldIndex)
    {
        var tiers = _ladder.Tiers;
        var promoted = record.RankIndex > oldIndex;

        _events.RaiseRankChanged(new RanksRankChangedArgs(
            record.SteamId, record.Points,
            tiers[oldIndex].Name, tiers[record.RankIndex].Name,
            oldIndex, record.RankIndex, promoted));

        if (!config.Points.ShowRankChangeMessages) return;

        var player = FindPlayer(record.SteamId);
        if (player is null) return;

        var key = promoted ? "rank.promoted" : "rank.demoted";
        Chat.Send(_core, player, key, tiers[record.RankIndex].Name, record.Points);
    }

    /// <summary>Flushes buffered point lines at round end (FR-PTS-08).</summary>
    public void FlushRoundSummary()
    {
        if (!_config.Current.Points.RoundEndSummary) return;

        foreach (var record in _registry.All)
        {
            if (record.PendingSummaryPoints == 0) continue;

            var delta = record.PendingSummaryPoints;
            record.PendingSummaryPoints = 0;

            if (!record.ShowPointMessages) continue;

            if (FindPlayer(record.SteamId) is { } player)
                Chat.Send(_core, player, "XP.summary", delta > 0 ? "+" : "", delta);
        }
    }

    private IPlayer? FindPlayer(ulong steamId) =>
        _core.PlayerManager.GetPlayerFromSteamId(steamId, allowUnauthorized: false);

    /// <remarks>
    /// <c>MinPlayers</c> counts every connected human, spectators included, as K4-LevelRanks does.
    /// Teams are read only when the bots-only rule can apply, keeping those native reads off servers
    /// that count bots.
    /// </remarks>
    private bool EvaluatePlayGate(RankOptions rank)
    {
        var checkTeams = !rank.BotsEnabled && !rank.FfaMode;
        int humans = 0, humansT = 0, humansCt = 0, botsT = 0, botsCt = 0;

        foreach (var player in _core.PlayerManager.GetAllPlayers())
        {
            var bot = player.IsFakeClient;
            if (!bot) humans++;
            if (!checkTeams) continue;

            switch (TeamOf(player))
            {
                case 2 when bot: botsT++; break;
                case 2: humansT++; break;
                case 3 when bot: botsCt++; break;
                case 3: humansCt++; break;
            }
        }

        if (humans < rank.MinPlayers) return false;
        if (!rank.WarmupPoints && IsWarmup()) return false;

        // A team of only bots leaves nobody to play against. FFA is exempt: teams mean nothing there.
        return !checkTeams || !((botsT > 0 && humansT == 0) || (botsCt > 0 && humansCt == 0));
    }

    /// <remarks>
    /// <c>IsValid</c> on both, as in Ranks_EloPoints: a try/catch cannot catch a native access
    /// violation, and a non-null controller wrapper can still sit over a dead pointer.
    /// </remarks>
    private int TeamOf(IPlayer player)
    {
        try
        {
            return player.IsValid && player.Controller is { IsValid: true } controller ? controller.TeamNum : 0;
        }
        catch (Exception ex)
        {
            _detail.Write("Ranks: could not read team for slot {Slot}: {Message}", player.Slot, ex.Message);
            return 0;
        }
    }

    /// <remarks>Null before the engine has globals to read, which only disables the cache.</remarks>
    private int? CurrentTick()
    {
        try
        {
            return _core.Engine.GlobalVars.TickCount;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <remarks>
    /// Read from the game-rules entity rather than cached on a round-start event: a hot reload
    /// mid-warmup would otherwise start with a stale answer and pay out for the rest of it.
    /// </remarks>
    private bool IsWarmup()
    {
        try
        {
            foreach (var proxy in _core.EntitySystem.GetAllEntitiesByClass<CCSGameRulesProxy>())
                return proxy.GameRules?.WarmupPeriod ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks: could not read warmup state; treating the round as live.");
        }

        return false;
    }
}

/// <summary>Which dynamic bounds an award reads, if any.</summary>
internal enum ScalingKind
{
    None,
    Kill,
    Death,
}
