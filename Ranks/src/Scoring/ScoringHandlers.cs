using Microsoft.Extensions.Logging;
using Ranks.Config;
using Ranks.Contract;
using Ranks.Logging;
using Ranks.Players;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace Ranks.Scoring;

/// <summary>Core's game-event handlers: the only thing that decides a core award happened.</summary>
/// <remarks>
/// These live outside the plugin class, so the instance <b>must</b> be passed to
/// <c>Core.Registrator.Register</c> in Load — without it every handler here is silently inert.
/// All are <c>HookMode.Post</c>: core observes an outcome, it never cancels one, and from a post
/// hook neither <c>Stop</c> nor <c>Handled</c> does anything at all.
/// </remarks>
internal sealed class ScoringHandlers
{
    private readonly ISwiftlyCore _core;
    private readonly ConfigHost<RanksConfig> _config;
    private readonly AwardPipeline _awards;
    private readonly PlayerRegistry _registry;
    private readonly DetailLog _detail;
    private readonly ILogger _logger;

    public ScoringHandlers(
        ISwiftlyCore core,
        ConfigHost<RanksConfig> config,
        AwardPipeline awards,
        PlayerRegistry registry,
        DetailLog detail,
        ILogger logger)
    {
        _core = core;
        _config = config;
        _awards = awards;
        _registry = registry;
        _detail = detail;
        _logger = logger;
    }

    /// <summary>Raised at round end so the host can run its save pass and maintenance check.</summary>
    public event Action? RoundEnded;

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDeath(EventPlayerDeath e)
    {
        var victimIsBot = IsBot(e.UserIdPlayer);
        var attackerIsBot = IsBot(e.AttackerPlayer);

        // Uncounted play (MinPlayers, warmup, a team of only bots) and bot involvement disregard the
        // event entirely — no counters, no killstreak, no arbiter claim. Returning here rather than
        // per-award is what keeps a streak from being built on bots, which AwardKillstreak cannot
        // gate on because it has no counterparty.
        if (!_awards.ShouldRecordDeath(victimIsBot, attackerIsBot)) return HookResult.Continue;

        var victim = Record(e.UserIdPlayer);
        var attacker = Record(e.AttackerPlayer);
        var assister = Record(e.AssisterPlayer);

        var selfInflicted = attacker is null || attacker.SteamId == victim?.SteamId;
        var friendlyFire = !selfInflicted && IsSameTeam(e.UserIdPlayer, e.AttackerPlayer);

        if (victim is not null)
        {
            victim.Deaths++;
            victim.Dirty = true;
            victim.Killstreak = 0;
        }

        // Enemy kills only. A team kill is its own event, and counting it here would make
        // !top kills farmable by killing teammates.
        if (attacker is not null && !selfInflicted && !friendlyFire)
        {
            attacker.Kills++;
            if (e.Headshot) attacker.Headshots++;
            attacker.Dirty = true;
        }

        if (assister is not null && assister.SteamId != attacker?.SteamId)
        {
            assister.Assists++;
            assister.Dirty = true;
        }

        AwardVictim(victim, attacker, e.AttackerPlayer, selfInflicted);
        AwardAttacker(e, attacker, victim, selfInflicted, friendlyFire);
        AwardAssister(e, assister, attacker, victim);

        return HookResult.Continue;
    }

    /// <remarks>
    /// The previous shot never reached OnPlayerHurt, so it damaged nobody. Misses cannot be derived
    /// as shots minus hits: one shotgun blast is a single trigger pull that lands up to nine hits,
    /// so that subtraction goes negative. Only a shot fired while play counts is latched, so one
    /// fired below MinPlayers never surfaces as a miss once the gate opens.
    /// </remarks>
    [GameEventHandler(HookMode.Post)]
    public HookResult OnWeaponFire(EventWeaponFire e)
    {
        if (Record(e.UserIdPlayer) is not { } record) return HookResult.Continue;

        if (record.PendingShot)
        {
            record.Misses++;
            record.Dirty = true;
        }

        record.PendingShot = _awards.IsPlayCounted;

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerHurt(EventPlayerHurt e)
    {
        if (Record(e.AttackerPlayer) is not { } attacker) return HookResult.Continue;

        // Cleared ahead of the gates: a bullet that struck a teammate, or a bot on a server not
        // counting them, still landed. Clearing after would score every such shot as a miss.
        attacker.PendingShot = false;

        if (e.AttackerPlayer?.Slot == e.UserIdPlayer?.Slot) return HookResult.Continue;
        if (!_awards.ShouldRecordDeath(IsBot(e.UserIdPlayer), IsBot(e.AttackerPlayer))) return HookResult.Continue;

        attacker.Hits++;
        attacker.Dirty = true;

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundEnd(EventRoundEnd e)
    {
        var winner = e.Winner;

        // A round neither side won is nobody's loss; without the side check a draw booked one for
        // every player on both teams.
        var counted = winner is 2 or 3 && _awards.IsPlayCounted;

        foreach (var player in _core.PlayerManager.GetAllPlayers())
        {
            var record = Record(player);
            if (record is null) continue;

            var team = TeamOf(player);
            if (team is not (2 or 3)) continue;

            if (counted)
            {
                if (team == winner)
                {
                    record.RoundWins++;
                    _awards.Award(record, PointEvents.RoundWin);
                }
                else
                {
                    record.RoundLosses++;
                    _awards.Award(record, PointEvents.RoundLose);
                }

                record.Dirty = true;
            }

            if (_config.Current.Points.KillstreakResetOnRoundEnd) record.Killstreak = 0;
        }

        _awards.FlushRoundSummary();
        RoundEnded?.Invoke();

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundMvp(EventRoundMvp e)
    {
        if (Record(e.UserIdPlayer) is { } record) _awards.Award(record, PointEvents.Mvp);

        return HookResult.Continue;
    }

    /// <summary>Awards the match result by comparing the two team entities' scores.</summary>
    /// <remarks>
    /// <c>EventCsWinPanelMatch</c> carries no fields at all — it is a notification that the match
    /// ended, not a result — so the side has to be derived at the moment it fires. Filtering to
    /// team numbers 2 and 3 is not decoration: the query also returns the spectator and unassigned
    /// team entities, whose scores would otherwise take part in the comparison.
    /// </remarks>
    [GameEventHandler(HookMode.Post)]
    public HookResult OnMatchEnd(EventCsWinPanelMatch e)
    {
        if (!_awards.IsPlayCounted) return HookResult.Continue;

        try
        {
            var teams = _core.EntitySystem.GetAllEntitiesByClass<CCSTeam>()
                .Where(t => t.TeamNum is 2 or 3)
                .ToArray();

            if (teams.Length != 2 || teams[0].Score == teams[1].Score) return HookResult.Continue;

            var winner = teams[0].Score > teams[1].Score ? teams[0].TeamNum : teams[1].TeamNum;

            foreach (var player in _core.PlayerManager.GetAllPlayers())
            {
                var record = Record(player);
                if (record is null) continue;

                var team = TeamOf(player);
                if (team is not (2 or 3)) continue;

                if (team == winner)
                {
                    record.MatchWins++;
                    _awards.Award(record, PointEvents.MatchWin);
                }
                else
                {
                    record.MatchLosses++;
                    _awards.Award(record, PointEvents.MatchLose);
                }

                record.Dirty = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: failed to resolve the match result.");
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnBombPlanted(EventBombPlanted e)
    {
        if (!_awards.IsPlayCounted || Record(e.UserIdPlayer) is not { } record) return HookResult.Continue;

        record.BombsPlanted++;
        record.Dirty = true;
        _awards.Award(record, PointEvents.BombPlant);

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnBombDefused(EventBombDefused e)
    {
        if (!_awards.IsPlayCounted || Record(e.UserIdPlayer) is not { } record) return HookResult.Continue;

        record.BombsDefused++;
        record.Dirty = true;
        _awards.Award(record, PointEvents.BombDefuse);

        // Everyone else on CT is rewarded for the defuse they did not personally make.
        foreach (var player in _core.PlayerManager.GetAllPlayers())
        {
            if (player.SteamID == record.SteamId || TeamOf(player) != 3) continue;

            if (Record(player) is { } other) _awards.Award(other, PointEvents.BombDefuseOthers);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnBombExploded(EventBombExploded e)
    {
        foreach (var player in _core.PlayerManager.GetAllPlayers())
        {
            if (TeamOf(player) != 2) continue;

            if (Record(player) is { } record) _awards.Award(record, PointEvents.BombExploded);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnBombPickup(EventBombPickup e)
    {
        if (Record(e.UserIdPlayer) is { } record) _awards.Award(record, PointEvents.BombPickup);

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnBombDropped(EventBombDropped e)
    {
        if (Record(e.UserIdPlayer) is { } record) _awards.Award(record, PointEvents.BombDrop);

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnHostageRescued(EventHostageRescued e)
    {
        if (Record(e.UserIdPlayer) is { } record) _awards.Award(record, PointEvents.HostageRescue);

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnHostageRescuedAll(EventHostageRescuedAll e)
    {
        foreach (var player in _core.PlayerManager.GetAllPlayers())
        {
            if (TeamOf(player) != 3) continue;

            if (Record(player) is { } record) _awards.Award(record, PointEvents.HostageRescueAll);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnHostageHurt(EventHostageHurt e)
    {
        if (Record(e.UserIdPlayer) is { } record) _awards.Award(record, PointEvents.HostageHurt);

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnHostageKilled(EventHostageKilled e)
    {
        if (Record(e.UserIdPlayer) is { } record) _awards.Award(record, PointEvents.HostageKill);

        return HookResult.Continue;
    }

    private void AwardVictim(PlayerRecord? victim, PlayerRecord? attacker, IPlayer? attackerPlayer, bool selfInflicted)
    {
        if (victim is null) return;

        if (selfInflicted)
        {
            _awards.Award(victim, PointEvents.Suicide);
            return;
        }

        _awards.Award(victim, PointEvents.Death,
            counterparty: attacker?.SteamId, counterpartyIsBot: IsBot(attackerPlayer), scaling: ScalingKind.Death);
    }

    /// <remarks>A null counterparty is world damage, not a bot, and must keep paying out.</remarks>
    private static bool IsBot(IPlayer? player) => player is { IsFakeClient: true };

    private void AwardAttacker(EventPlayerDeath e, PlayerRecord? attacker, PlayerRecord? victim, bool selfInflicted, bool friendlyFire)
    {
        if (attacker is null || selfInflicted) return;

        var victimIsBot = IsBot(e.UserIdPlayer);
        var counterparty = victim?.SteamId;

        var claim = _awards.ClaimKill(new RanksKillContext(
            attacker.SteamId,
            counterparty,
            victimIsBot,
            friendlyFire,
            e.Weapon ?? string.Empty,
            e.Distance,
            (int)e.ActualHitGroup,
            BuildAttributes(e, friendlyFire)));

        var suppressed = claim is { SuppressCoreAwards: true };

        if (friendlyFire)
        {
            if (!suppressed)
                _awards.Award(attacker, PointEvents.TeamKill, counterparty: counterparty, counterpartyIsBot: victimIsBot);

            if (claim is not null) _awards.AwardClaim(attacker, claim, counterparty, victimIsBot);
            return;
        }

        if (!suppressed)
        {
            _awards.Award(attacker, PointEvents.Kill, counterparty: counterparty, counterpartyIsBot: victimIsBot, scaling: ScalingKind.Kill);

            // The flat catalogue stacks: a knife headshot already scores kill + headshot + knife_kill.
            // A value for the combination itself is Ranks_CustomCombos' job, not core's (FR-PTS-09).
            if (e.Headshot) _awards.Award(attacker, PointEvents.Headshot, counterparty: counterparty, counterpartyIsBot: victimIsBot);
            if (e.NoScope) _awards.Award(attacker, PointEvents.NoScope, counterparty: counterparty, counterpartyIsBot: victimIsBot);
            if (e.Penetrated > 0) _awards.Award(attacker, PointEvents.Wallbang, counterparty: counterparty, counterpartyIsBot: victimIsBot);
            if (e.ThruSmoke) _awards.Award(attacker, PointEvents.ThroughSmoke, counterparty: counterparty, counterpartyIsBot: victimIsBot);
            if (e.AttackerBlind) _awards.Award(attacker, PointEvents.BlindKill, counterparty: counterparty, counterpartyIsBot: victimIsBot);

            if (e.Distance >= _config.Current.Points.LongDistanceUnits)
                _awards.Award(attacker, PointEvents.LongDistance, counterparty: counterparty, counterpartyIsBot: victimIsBot);

            AwardWeaponClass(attacker, e.Weapon, counterparty, victimIsBot);
        }

        if (claim is not null) _awards.AwardClaim(attacker, claim, counterparty, victimIsBot);

        // Never suppressed: a streak is a property of the player's round, not of this kill.
        AwardKillstreak(attacker);
    }

    private static IReadOnlySet<string> BuildAttributes(EventPlayerDeath e, bool friendlyFire)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        if (e.Headshot) set.Add(RanksKillAttributes.Headshot);
        if (e.NoScope) set.Add(RanksKillAttributes.NoScope);
        if (e.ThruSmoke) set.Add(RanksKillAttributes.ThruSmoke);
        if (e.AttackerBlind) set.Add(RanksKillAttributes.AttackerBlind);
        if (e.AttackerInAir) set.Add(RanksKillAttributes.AttackerInAir);
        if (e.AssistedFlash) set.Add(RanksKillAttributes.AssistedFlash);
        if (e.Penetrated > 0) set.Add(RanksKillAttributes.Penetrated);
        if (e.Dominated > 0) set.Add(RanksKillAttributes.Dominated);
        if (e.Revenge > 0) set.Add(RanksKillAttributes.Revenge);
        if (friendlyFire) set.Add(RanksKillAttributes.TeamKill);

        return set;
    }

    private void AwardAssister(EventPlayerDeath e, PlayerRecord? assister, PlayerRecord? attacker, PlayerRecord? victim)
    {
        if (assister is null || assister.SteamId == attacker?.SteamId) return;

        var key = IsSameTeam(e.UserIdPlayer, e.AssisterPlayer)
            ? PointEvents.TeamAssist
            : e.AssistedFlash ? PointEvents.AssistFlash : PointEvents.Assist;

        _awards.Award(assister, key, counterparty: victim?.SteamId, counterpartyIsBot: IsBot(e.UserIdPlayer));
    }

    /// <remarks>
    /// Matched on the designer name the event reports. <c>inferno</c> is molotov fire rather than a
    /// weapon entity, which is why it is not prefixed here.
    /// </remarks>
    private void AwardWeaponClass(PlayerRecord attacker, string? weapon, ulong? counterparty, bool counterpartyIsBot)
    {
        if (string.IsNullOrEmpty(weapon)) return;

        var key = weapon switch
        {
            "knife" or "weapon_knife" or "bayonet" => PointEvents.KnifeKill,
            "taser" or "weapon_taser" => PointEvents.TaserKill,
            "hegrenade" or "weapon_hegrenade" => PointEvents.GrenadeKill,
            "inferno" or "molotov" or "incgrenade" => PointEvents.InfernoKill,
            "world" or "trigger_hurt" => PointEvents.ImpactKill,
            _ => weapon.Contains("knife", StringComparison.Ordinal) ? PointEvents.KnifeKill : null,
        };

        if (key is not null) _awards.Award(attacker, key, counterparty: counterparty, counterpartyIsBot: counterpartyIsBot);
    }

    /// <remarks>
    /// Eleven tiers covering 2 through 12-or-more. A streak is a property of the player's round
    /// rather than of any single kill, which is why core keeps this one count-based case; a streak
    /// restricted to a weapon or a placement is a combo and belongs to a module.
    /// </remarks>
    private void AwardKillstreak(PlayerRecord attacker)
    {
        var config = _config.Current;

        if (!config.Points.KillStreaksEnabled) return;

        var now = DateTime.UtcNow;
        var window = config.Points.SecondsBetweenKills;

        if (window > 0 && attacker.LastKillAt != default && (now - attacker.LastKillAt).TotalSeconds > window)
            attacker.Killstreak = 0;

        attacker.LastKillAt = now;
        attacker.Killstreak++;

        var streaks = PointEvents.Streaks;
        if (attacker.Killstreak < streaks[0].Kills) return;

        // 12+ all pay the last tier, so a long streak keeps rewarding rather than falling off.
        var tier = attacker.Killstreak >= streaks[^1].Kills
            ? streaks[^1]
            : streaks[attacker.Killstreak - streaks[0].Kills];

        _awards.Award(attacker, tier.Key);
    }

    private PlayerRecord? Record(IPlayer? player) =>
        player is null ? null : _registry.BySlot(player.Slot);

    /// <remarks>
    /// FFA suppresses team-damage penalties: with everyone hostile, a same-team kill is an ordinary
    /// kill and pricing it as a team kill would zero out most of the server's scoring.
    /// </remarks>
    private bool IsSameTeam(IPlayer? a, IPlayer? b)
    {
        if (a is null || b is null) return false;
        if (_config.Current.Rank.FfaMode) return false;

        var teamA = TeamOf(a);

        return teamA is 2 or 3 && teamA == TeamOf(b);
    }

    private int TeamOf(IPlayer player)
    {
        try
        {
            return player.Controller?.TeamNum ?? 0;
        }
        catch (Exception ex)
        {
            _detail.Write("Ranks: could not read team for slot {Slot}: {Message}", player.Slot, ex.Message);
            return 0;
        }
    }
}
