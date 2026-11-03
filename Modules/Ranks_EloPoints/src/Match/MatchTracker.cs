using Microsoft.Extensions.Logging;
using Ranks.EloPoints.Api;
using Ranks.EloPoints.Config;
using Ranks.EloPoints.Rating;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

namespace Ranks.EloPoints.Match;

/// <summary>The match state machine and the only thing that writes a rating.</summary>
/// <remarks>
/// Holds the <c>[GameEventHandler]</c> methods, so it is invisible to the framework until it is
/// passed to <c>Core.Registrator.Register</c> — exactly once. Registering twice double-counts
/// every round.
///
/// Everything here runs on the game thread. Settling detaches before handing anything to the
/// store, so no synchronisation is needed.
/// </remarks>
internal sealed class MatchTracker
{
    private readonly ISwiftlyCore _core;
    private readonly RanksBridge _bridge;
    private readonly RatingStore _store;
    private readonly ILogger _logger;
    private readonly Dictionary<ulong, MatchSession> _sessions = [];

    private volatile EloConfig _config;
    private int _lastRoundsPlayed;
    private bool _settled;
    private bool _playGateUnsupported;

    public MatchTracker(ISwiftlyCore core, RanksBridge bridge, RatingStore store, EloConfig config, ILogger logger)
    {
        _core = core;
        _bridge = bridge;
        _store = store;
        _config = config;
        _logger = logger;
    }

    public void UpdateConfig(EloConfig config) => _config = config;

    /// <summary>Starts tracking a player. Bots deliberately never get a session.</summary>
    /// <remarks>
    /// A bot therefore never owns a rating row — it has no SteamID64 to key one to. Whether bots
    /// may be imputed into a team average is a separate question, answered by core's BotsEnabled.
    /// </remarks>
    public void Adopt(IPlayer player)
    {
        if (player.IsFakeClient || player.SteamID == 0) return;

        // IsAvailable is a plain bool, so reading it resolves nothing. The rating lookup below
        // reaches the bridge, which names a contract type and would throw here with no Ranks.
        if (!_bridge.IsAvailable) return;

        // Rejoining resumes the existing session rather than replacing it: that is what makes a
        // reconnect invisible to the rating.
        if (_sessions.TryGetValue(player.SteamID, out var existing))
        {
            existing.Present = true;
            return;
        }

        var session = new MatchSession { SteamId = player.SteamID };
        _sessions[player.SteamID] = session;

        ResolveRating(session);
    }

    /// <summary>Marks a player absent without discarding their accrual.</summary>
    public void Release(ulong steamId)
    {
        if (_sessions.TryGetValue(steamId, out var session)) session.Present = false;
    }

    /// <summary>Captures the match-start counters for a player whose record just became resident.</summary>
    /// <remarks>
    /// The only moment a mid-match joiner's counters are guaranteed readable. Without it they
    /// would be scored against a zero baseline, which reads as a perfect game.
    /// </remarks>
    public void OnPlayerDataLoaded(ulong steamId, in PerfCounters counters, int corePoints)
    {
        if (!_sessions.TryGetValue(steamId, out var session)) return;

        session.StartCounters ??= counters;

        if (!session.RatingResolved && corePoints > 0)
        {
            session.Rating = corePoints;
            session.RatingResolved = true;
        }
    }

    /// <summary>Throws away an unfinished match. Nothing was written, so there is nothing to undo.</summary>
    public void DiscardMatch(string reason)
    {
        if (_sessions.Count > 0 && _config.General.DetailedLogging)
            _logger.LogInformation("Ranks_EloPoints: discarding {Count} open session(s) — {Reason}.", _sessions.Count, reason);

        StartFreshMatch();
        _settled = false;
    }

    /// <summary>Zeroes every accrual and re-baselines the players still here.</summary>
    /// <remarks>
    /// Replaces clearing the dictionary, which looked equivalent and was not: nothing re-adds a
    /// session for an already-connected player, so after a knife-round restart — or after any
    /// settle — every connected player silently stopped accruing and the whole next match went
    /// unrated. Adopt only runs on connect and on hot reload.
    ///
    /// Re-capturing StartCounters is the other half: they are a match baseline, and the ones taken
    /// at connect would make a second match's performance percentile span both.
    /// </remarks>
    private void StartFreshMatch()
    {
        foreach (var steamId in _sessions.Keys.ToArray())
        {
            var session = _sessions[steamId];

            if (!session.Present)
            {
                _sessions.Remove(steamId);
                continue;
            }

            session.RoundsPresent = 0;
            session.SumActual = 0;
            session.SumExpected = 0;
            session.LastTeam = 0;
            session.StartCounters = _bridge.IsAvailable ? _bridge.GetCounters(steamId) : null;
        }

        _lastRoundsPlayed = 0;
    }

    /// <remarks>
    /// The accrual walks every player and reads native team numbers, which must not happen inside
    /// a game-event dispatch even in Post mode — CS2 can be mid entity-shuffle and a managed
    /// try/catch does not catch the resulting access violation. Only the winner and core's play
    /// gate are read here. The gate adds no native read: core decides it inside this same dispatch
    /// for its own round result, once per tick, so asking here returns the verdict core used.
    /// </remarks>
    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundEnd(EventRoundEnd e)
    {
        var winner = (int)e.Winner;

        // A draw is nobody's result: core records none and skips the gate for it, so this does too.
        bool? counted = winner is TeamT or TeamCt ? PlayGate() : false;

        _core.Scheduler.NextTick(() => AccrueRound(winner, counted));

        return HookResult.Continue;
    }

    /// <summary>Core's play gate, or null when this Ranks cannot report one.</summary>
    /// <remarks>An older contract fails the bridge call at JIT, which surfaces in this frame.</remarks>
    private bool? PlayGate()
    {
        if (_playGateUnsupported || !_bridge.IsAvailable) return null;

        try
        {
            return _bridge.IsPlayCounted();
        }
        catch (MissingMethodException)
        {
            _playGateUnsupported = true;
            _logger.LogWarning(
                "Ranks_EloPoints: this Ranks version cannot report its play gate, so matches are rated under this " +
                "module's own MinPlayers and warmup rules, which can disagree with core's statistics. " +
                "Update Ranks to 1.0.4 or newer.");

            return null;
        }
    }

    private void AccrueRound(int winner, bool? counted)
    {
        if (!_bridge.IsAvailable) return;
        if (_bridge.GetSettings() is not { } settings) return;
        if (!TryReadMatch(out var match)) return;

        if (match.RoundsPlayed < _lastRoundsPlayed)
        {
            DiscardMatch("the round counter moved backwards, so the match was restarted");
            return;
        }

        _lastRoundsPlayed = match.RoundsPlayed;

        // Exactly the rounds core counted (FR-PTS-03), so no rating moves on play core's statistics
        // left out. An older core cannot say, and gets this module's own warmup check instead.
        if (counted is false) return;
        if (counted is null && match.Phase == GamePhase.GAMEPHASE_WARMUP_ROUND && !settings.WarmupPoints) return;

        // A round accruing means a live match, so a previous settle no longer applies.
        _settled = false;

        var (averageT, averageCt) = TeamAverages(settings.StartPoints);

        foreach (var player in _core.PlayerManager.GetAllPlayers())
        {
            if (player.IsFakeClient || !player.IsValid) continue;
            if (!_sessions.TryGetValue(player.SteamID, out var session) || !session.Present) continue;

            var team = TeamOf(player);
            if (team is not (TeamT or TeamCt)) continue;

            session.LastTeam = team;
            session.RoundsPresent++;

            // Classic takes one expectation and one result at settle time, so it accrues only the
            // participation count here.
            if (_config.ResolvedMode != RatingMode.RoundAccumulation) continue;

            var own = team == TeamT ? averageT : averageCt;
            var opponent = team == TeamT ? averageCt : averageT;

            session.SumExpected += EloMath.Expected(own, opponent);
            session.SumActual += team == winner ? 1.0 : 0.0;
        }
    }

    /// <remarks>Deferred for the same reason as the round accrual: settling reads native state.</remarks>
    [GameEventHandler(HookMode.Post)]
    public HookResult OnWinPanelMatch(EventCsWinPanelMatch e)
    {
        _core.Scheduler.NextTick(Settle);

        return HookResult.Continue;
    }

    /// <remarks>
    /// Moving to spectator is leaving the match. Without that, the correct play when losing would
    /// be to spectate, and the ladder would be worthless. Team None (0) is excluded because every
    /// player passes through it while connecting.
    ///
    /// Not deferred, unlike the two handlers above: the only native read is this player's own
    /// SteamID, whose pointer is stable for the event that names them, and deferring would risk
    /// them being gone by the next tick.
    /// </remarks>
    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerTeam(EventPlayerTeam e)
    {
        if (e.IsBot || e.UserIdPlayer is not { IsValid: true } player || player.SteamID == 0) return HookResult.Continue;
        if (!_sessions.TryGetValue(player.SteamID, out var session)) return HookResult.Continue;

        session.Present = e.Team is TeamT or TeamCt;

        return HookResult.Continue;
    }

    /// <summary>Settles the match once. Idempotent, so the event and the phase backstop may both fire.</summary>
    public void Settle()
    {
        if (!_bridge.IsAvailable) return;
        if (_settled) return;
        _settled = true;

        if (_sessions.Count == 0) return;

        if (_bridge.GetSettings() is not { } settings)
        {
            _logger.LogWarning("Ranks_EloPoints: core settings were unavailable at match end, so {Count} session(s) went unrated.", _sessions.Count);
            StartFreshMatch();
            return;
        }

        var matchRounds = ResolveMatchRounds();

        if (!IsRatable(settings, out var reason))
        {
            if (_config.General.DetailedLogging)
                _logger.LogInformation("Ranks_EloPoints: match left unrated — {Reason}.", reason);

            StartFreshMatch();
            return;
        }

        var rated = _sessions.Values.Where(s => s.RoundsPresent > 0).ToArray();
        var scores = rated.Select(ScoreOf).ToArray();
        var writes = new List<RatingWrite>(rated.Length);
        var penalty = _config.Abandon.Penalty;

        for (var i = 0; i < rated.Length; i++)
        {
            var session = rated[i];
            var inputs = BuildInputs(session, matchRounds, scores, i, settings);
            var abandoned = !session.Present;

            var matchDelta = EloMath.MatchDelta(inputs);
            var updated = EloMath.Apply(session.Rating, matchDelta, abandoned ? penalty : 0, settings.MinPoints);

            writes.Add(new RatingWrite(session.SteamId, updated, abandoned));

            // Core's points is the display and ladder value; the module's DECIMAL is the rating.
            // Rounded, never truncated: truncation never rounds back, so the error would
            // accumulate in one direction forever.
            _bridge.SetPoints(session.SteamId, (int)Math.Round(updated), "elopoints.match");
            _bridge.SavePlayer(session.SteamId);

            if (_config.General.DetailedLogging)
                _logger.LogInformation(
                    "Ranks_EloPoints: {SteamId} {Rating:N2} -> {Updated:N2} over {Rounds} round(s){Abandon}.",
                    session.SteamId, session.Rating, updated, session.RoundsPresent, abandoned ? $", abandoned (-{penalty})" : "");
        }

        _ = _store.CommitAsync(writes);
        StartFreshMatch();
    }

    private EloInputs BuildInputs(MatchSession session, int matchRounds, double[] scores, int index, in CoreSettings settings)
    {
        var (sumActual, sumExpected) = _config.ResolvedMode == RatingMode.RoundAccumulation
            ? (session.SumActual, session.SumExpected)
            : ClassicScores(session, settings.StartPoints);

        return new EloInputs(
            _config.Rating.K,
            _config.Rating.ConfidenceRounds,
            session.RoundsPresent,
            matchRounds,
            sumActual,
            sumExpected,
            _config.Rating.PerformanceBonus,
            session.StartCounters is null ? Performance.NeutralPercentile : Performance.Percentile(scores, scores[index]));
    }

    /// <summary>One result against one expectation, spread over the rounds the player was present for.</summary>
    /// <remarks>
    /// Expressed as sums so Classic and RoundAccumulation share the same delta calculation rather
    /// than growing a second code path through the clamps.
    /// </remarks>
    private (double SumActual, double SumExpected) ClassicScores(MatchSession session, int startPoints)
    {
        var (averageT, averageCt) = TeamAverages(startPoints);
        var own = session.LastTeam == TeamT ? averageT : averageCt;
        var opponent = session.LastTeam == TeamT ? averageCt : averageT;

        var actual = 0.5;

        if (TryReadMatch(out var match) && match.CtScore != match.TScore)
        {
            var winner = match.CtScore > match.TScore ? TeamCt : TeamT;
            actual = session.LastTeam == winner ? 1.0 : 0.0;
        }

        return (actual * session.RoundsPresent, EloMath.Expected(own, opponent) * session.RoundsPresent);
    }

    /// <summary>Core's counters for this match only, scored by the performance weights.</summary>
    private double ScoreOf(MatchSession session)
    {
        if (session.StartCounters is not { } start) return 0.0;
        if (_bridge.GetCounters(session.SteamId) is not { } now) return 0.0;

        return Performance.Score(now.Since(start));
    }

    private bool IsRatable(in CoreSettings settings, out string reason)
    {
        var humans = _sessions.Values.Count(s => s.RoundsPresent > 0);

        if (humans == 0)
        {
            reason = "no round of it counted";
            return false;
        }

        // Core's gate already applied MinPlayers and the bots-only-team rule to every accrued round.
        // A second, match-level check would withhold a rating whose statistics core kept.
        if (!_playGateUnsupported)
        {
            reason = "";
            return true;
        }

        if (humans < settings.MinPlayers)
        {
            reason = $"{humans} human(s) played, below core's Rank.MinPlayers of {settings.MinPlayers}";
            return false;
        }

        // With BotsEnabled off, a side carried entirely by bots is not a side anyone was rated
        // against, so the whole match is unrated rather than half of it.
        if (!settings.BotsEnabled)
        {
            var onT = _sessions.Values.Any(s => s.RoundsPresent > 0 && s.LastTeam == TeamT);
            var onCt = _sessions.Values.Any(s => s.RoundsPresent > 0 && s.LastTeam == TeamCt);

            if (!onT || !onCt)
            {
                reason = "one side had no rated humans and core's Rank.BotsEnabled is false";
                return false;
            }
        }

        reason = "";
        return true;
    }

    /// <summary>Team averages over rated humans, falling back outward when a side has none.</summary>
    /// <remarks>
    /// Own team, then every rated player in the match, then StartPoints. Each fallback is only
    /// reached when the one above it has no rated player at all, which is how bots and
    /// failed-to-load players get imputed rather than counted as zero.
    /// </remarks>
    private (double T, double Ct) TeamAverages(int startPoints)
    {
        double sumT = 0, sumCt = 0;
        var countT = 0;
        var countCt = 0;

        foreach (var player in _core.PlayerManager.GetAllPlayers())
        {
            if (player.IsFakeClient || !player.IsValid) continue;
            if (!_sessions.TryGetValue(player.SteamID, out var session) || !session.RatingResolved) continue;

            switch (TeamOf(player))
            {
                case TeamT: sumT += session.Rating; countT++; break;
                case TeamCt: sumCt += session.Rating; countCt++; break;
            }
        }

        var overall = countT + countCt > 0 ? (sumT + sumCt) / (countT + countCt) : startPoints;

        return (countT > 0 ? sumT / countT : overall, countCt > 0 ? sumCt / countCt : overall);
    }

    /// <summary>Resolves the stored rating, falling back to core's points and then StartPoints.</summary>
    private void ResolveRating(MatchSession session) => _ = ResolveRatingAsync(session);

    private async Task ResolveRatingAsync(MatchSession session)
    {
        double? stored = null;

        try
        {
            stored = await _store.ReadAsync(session.SteamId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks_EloPoints: could not read a starting rating for {SteamId}.", session.SteamId);
        }

        // Back onto the game thread before touching anything: the session, core's API and the
        // registry are all game-thread state, and the continuation runs on the thread pool.
        _core.Scheduler.NextTick(() => ApplyResolvedRating(session, stored));
    }

    private void ApplyResolvedRating(MatchSession session, double? stored)
    {
        // The session may have been cleared by a restart or a map change while the read ran.
        if (!_sessions.TryGetValue(session.SteamId, out var live) || !ReferenceEquals(live, session)) return;
        if (session.RatingResolved) return;

        if (stored is { } rating)
        {
            session.Rating = rating;
            session.RatingResolved = true;

            return;
        }

        // Core's points covers a server migrating from core scoring, where the accumulated total
        // is the only starting signal there is.
        var corePoints = _bridge.GetPoints(session.SteamId) ?? 0;

        session.Rating = corePoints > 0 ? corePoints : _bridge.GetSettings()?.StartPoints ?? DefaultStartRating;
        session.RatingResolved = true;
    }

    private int ResolveMatchRounds()
    {
        if (TryReadMatch(out var match) && match.RoundsPlayed > 0) return match.RoundsPlayed;

        var longest = _sessions.Values.Max(s => s.RoundsPresent);

        return Math.Max(longest, MaxRounds());
    }

    /// <summary>The match length to measure participation against while a match is still running.</summary>
    private int MaxRounds()
    {
        try
        {
            if (_core.ConVar.Find<int>("mp_maxrounds")?.Value is > 0 and var rounds) return rounds;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ranks_EloPoints: mp_maxrounds was unreadable.");
        }

        return _config.Rating.ExpectedRounds;
    }

    /// <remarks>
    /// GameRules only exists after world spawn, so reading MatchData between maps, during bootup,
    /// or once the rules entity has been torn down throws — and some SwiftlyS2 builds throw
    /// NullReferenceException rather than anything specific. This module reads at round end and
    /// map unload, which is exactly when that happens.
    /// </remarks>
    private bool TryReadMatch(out MatchSnapshot snapshot)
    {
        try
        {
            var match = _core.Game.MatchData;
            snapshot = new MatchSnapshot(match.ActualRoundsPlayed, match.Phase, match.CTScoreTotal, match.TerroristScoreTotal);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ranks_EloPoints: MatchData was unavailable.");
            snapshot = default;

            return false;
        }
    }

    /// <summary>True once the engine reports the match over, used as a backstop for the win panel.</summary>
    public bool MatchHasEnded() => TryReadMatch(out var match) && match.Phase == GamePhase.GAMEPHASE_MATCH_ENDED;

    /// <summary>True when this player is accruing in a match that has not settled yet.</summary>
    /// <remarks>
    /// Requires an accrued round rather than merely a session: a session exists from the moment a
    /// player connects, which would report "in a rated match" during warmup on an empty server.
    /// </remarks>
    public bool HasOpenSession(ulong steamId) =>
        !_settled && _sessions.TryGetValue(steamId, out var session) && session.RoundsPresent > 0;

    /// <remarks>
    /// <c>IsValid</c> on both the player and the controller is the only guard that works here: a
    /// try/catch does not catch a native access violation, and a non-null controller wrapper can
    /// still sit over a dead pointer.
    /// </remarks>
    private int TeamOf(IPlayer player)
    {
        try
        {
            return player.Controller is { IsValid: true } controller ? controller.TeamNum : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks_EloPoints: could not read a team number; the player is skipped this round.");
            return 0;
        }
    }

    private const int TeamT = (int)Team.T;
    private const int TeamCt = (int)Team.CT;

    /// <summary>Used only when core is unreachable at the moment a rating has to be seeded.</summary>
    private const int DefaultStartRating = 1000;

    private readonly record struct MatchSnapshot(int RoundsPlayed, GamePhase Phase, int CtScore, int TScore);
}
