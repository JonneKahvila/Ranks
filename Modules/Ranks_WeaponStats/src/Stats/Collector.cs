using Microsoft.Extensions.Logging;
using Ranks.WeaponStats.Config;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

namespace Ranks.WeaponStats.Stats;

/// <summary>The hot path: three game-event handlers and the slot array they write into.</summary>
/// <remarks>
/// Holds the <c>[GameEventHandler]</c> methods, so it is invisible to the framework until it is
/// passed to <c>Core.Registrator.Register</c> — exactly once. Registering twice double-counts
/// every shot.
/// </remarks>
internal sealed class Collector
{
    private const int MaxSlots = 64;

    private readonly ISwiftlyCore _core;
    private readonly ILogger _logger;
    private readonly SessionStats?[] _slots = new SessionStats?[MaxSlots];

    /// <remarks>
    /// A volatile snapshot swapped by the reload handler, not re-bound per access: weapon_fire
    /// alone runs at roughly 200/second on a busy server.
    /// </remarks>
    private volatile WeaponStatsConfig _config;

    /// <summary>Core's <c>Rank.BotsEnabled</c>, pushed in rather than configured here.</summary>
    /// <remarks>
    /// One switch for the whole plugin set: a module carrying its own would let an operator set it
    /// differently from core's and silently break the reconciliation this module documents.
    /// Polled rather than read per event — GetPointSettings builds a record and a dictionary, and
    /// weapon_fire alone runs at roughly 200/second.
    /// </remarks>
    public volatile bool BotsEnabled;

    /// <summary>Core's play gate — <c>MinPlayers</c>, warmup and bots-only teams — asked per event.</summary>
    /// <remarks>
    /// A delegate so this class names no contract type. Asked rather than polled like
    /// <see cref="BotsEnabled"/>: polling would lag core, which decides it once per tick.
    /// </remarks>
    private readonly Func<bool> _isPlayCounted;

    /// <summary>The period a batch cut now belongs to, or null while core's periods are off.</summary>
    /// <remarks>
    /// A delegate for the same reason as <see cref="_isPlayCounted"/>, and its target checks
    /// <c>RanksBridge.IsAvailable</c> before reaching core. Asked once per cut, never per event.
    /// </remarks>
    private readonly Func<long?> _writePeriod;

    public Collector(ISwiftlyCore core, WeaponStatsConfig config, Func<bool> isPlayCounted, Func<long?> writePeriod, ILogger logger)
    {
        _core = core;
        _config = config;
        _isPlayCounted = isPlayCounted;
        _writePeriod = writePeriod;
        _logger = logger;
    }

    public void UpdateConfig(WeaponStatsConfig config) => _config = config;

    /// <summary>Starts tracking a player. Bots deliberately never get a session.</summary>
    /// <remarks>
    /// A bot therefore never owns a row: its slot stays null and every handler returns on the null
    /// check it already has. Whether a bot counts as the <em>counterparty</em> is a separate
    /// question, answered by core's <c>Rank.BotsEnabled</c>.
    /// </remarks>
    public void Adopt(IPlayer player)
    {
        if (player.IsFakeClient || (uint)player.Slot >= MaxSlots) return;

        _slots[player.Slot] = new SessionStats { SteamId = player.SteamID };
    }

    /// <summary>Stops tracking a slot and returns its final deltas, stamped with the current period.</summary>
    /// <remarks>Detaches before clearing the slot, in that order. Game thread only.</remarks>
    public StatsDelta? Release(int slot)
    {
        if ((uint)slot >= MaxSlots || _slots[slot] is not { } stats) return null;

        var delta = stats.Detach(_writePeriod());
        _slots[slot] = null;

        return delta;
    }

    /// <summary>Detaches every live session's deltas, stamped with the current period. Game thread only.</summary>
    public List<StatsDelta> DetachAll() => DetachAll(_writePeriod());

    /// <summary>Detaches every live session's deltas, stamped with <paramref name="period"/>. Game thread only.</summary>
    /// <remarks>For a period that has just closed, which core no longer reports as current.</remarks>
    public List<StatsDelta> DetachAll(long? period)
    {
        var batch = new List<StatsDelta>();

        foreach (var session in _slots)
            if (session?.Detach(period) is { } delta)
                batch.Add(delta);

        return batch;
    }

    /// <summary>Throws away one player's unwritten deltas, for a reset that must not be undone by them. Game thread only.</summary>
    public void Discard(ulong steamId)
    {
        foreach (var session in _slots)
            if (session is not null && session.SteamId == steamId)
                _ = session.Detach(period: null);
    }

    /// <summary>Copies one player's unflushed deltas without clearing them. Game thread only.</summary>
    /// <remarks>
    /// Scans the slots rather than keeping a SteamID index: at most 64 reference comparisons, run
    /// once per API query rather than per event, which is far cheaper than a second dictionary the
    /// hot path would have to maintain.
    /// </remarks>
    public StatsDelta? PeekFor(ulong steamId)
    {
        foreach (var session in _slots)
            if (session is not null && session.SteamId == steamId)
                return session.Peek();

        return null;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnWeaponFire(EventWeaponFire e)
    {
        // No victim guards: there is no victim to test, so neither core's bot policy nor the
        // friendly-fire rule has anything to say about a shot.
        if (Session(e.UserIdPlayer) is not { } stats) return HookResult.Continue;

        var counters = stats.For(WeaponNames.Normalize(e.Weapon));

        // This weapon's previous shot never reached OnPlayerHurt, so it damaged nobody. The latch
        // is per weapon rather than per player so that a grenade, whose damage lands seconds later
        // and long after the next rifle shot, still resolves against its own trigger pull.
        counters.ResolvePendingShot();

        // Only a counted shot is latched, matching core, so one fired below the gate never later
        // becomes a miss.
        if (!_isPlayCounted()) return HookResult.Continue;

        counters.PendingShot = true;
        counters.Shots++;

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerHurt(EventPlayerHurt e)
    {
        var config = _config;
        var attacker = e.AttackerPlayer;
        var victim = e.UserIdPlayer;

        if (Session(attacker) is not { } stats) return HookResult.Continue;

        var counters = stats.For(WeaponNames.Normalize(e.Weapon));

        // Cleared ahead of the guards: a bullet that struck a teammate, or a bot on a server with
        // BotsEnabled off, landed. That is a hit which goes uncounted, not a miss — clearing
        // after the guards would score every such shot as missed.
        counters.PendingShot = false;

        if (!_isPlayCounted()) return HookResult.Continue;
        if (IsExcluded(config.Tracking, attacker, victim)) return HookResult.Continue;
        if (!CountsAgainst(victim)) return HookResult.Continue;

        // Actual* throughout: DmgHealth is a short and Hitgroup a byte, so a server granting more
        // than 255 HP silently corrupts the narrow ones.
        var damage = e.ActualDmgHealth;

        counters.Hits++;
        counters.Damage += damage;

        // An out-of-range hitgroup is simply not counted — there is no hitgroup to attribute it
        // to, and this table has no reconciliation invariant against core, which stores no hitbox
        // data at all. The weapon row for the same event is still recorded above.
        var hitGroup = (int)e.ActualHitGroup;

        if ((uint)hitGroup < WeaponCounters.HitGroupSlots)
        {
            counters.HitGroupHits[hitGroup]++;
            counters.HitGroupDamage[hitGroup] += damage;
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDeath(EventPlayerDeath e)
    {
        if (!_isPlayCounted()) return HookResult.Continue;

        // Read once at the top: a reload landing between the kill half and the death half would
        // otherwise mix old and new settings within a single event.
        var config = _config;
        var attacker = e.AttackerPlayer;
        var victim = e.UserIdPlayer;
        var weapon = WeaponNames.Normalize(e.Weapon);

        if (!IsExcluded(config.Tracking, attacker, victim) && CountsAgainst(victim) && Session(attacker) is { } killer)
        {
            var counters = killer.For(weapon);
            counters.Kills++;

            if (e.Headshot) counters.Headshots++;
        }

        // Deaths deliberately skip IsExcluded: being killed by a teammate is still a death, and
        // core's ranks_players.deaths counts every death whatever killed the player.
        if (CountsAgainst(attacker) && Session(victim) is { } died)
            died.For(weapon).Deaths++;

        return HookResult.Continue;
    }

    private SessionStats? Session(IPlayer? player) =>
        player is not null && (uint)player.Slot < MaxSlots ? _slots[player.Slot] : null;

    /// <remarks>
    /// Friendly fire and self damage are dropped before core's bot policy is consulted. Self
    /// damage is never combat performance under any setting, so it is not configurable; same-team
    /// is the operator's call.
    /// </remarks>
    private bool IsExcluded(TrackingConfig tracking, IPlayer? attacker, IPlayer? victim)
    {
        // World damage is neither self damage nor friendly fire, and must keep counting.
        if (attacker is null || victim is null) return false;

        if (attacker.Slot == victim.Slot) return true;

        if (tracking.CountFriendlyFire) return false;

        return SameTeam(attacker, victim);
    }

    /// <remarks>
    /// Guarded because it reaches into game memory on a hit-rate path; an unknown team falls
    /// through to counting, which is the safe bias for a module whose counters must reconcile.
    /// </remarks>
    private bool SameTeam(IPlayer attacker, IPlayer victim)
    {
        try
        {
            var attackerTeam = attacker.Controller?.TeamNum ?? 0;
            var victimTeam = victim.Controller?.TeamNum ?? 0;

            return attackerTeam is 2 or 3 && attackerTeam == victimTeam;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks_WeaponStats: could not read team numbers; counting the event.");
            return false;
        }
    }

    /// <summary>A null counterparty is world damage, not a bot, and must keep counting.</summary>
    private static bool IsBot(IPlayer? player) => player is { IsFakeClient: true };

    private bool CountsAgainst(IPlayer? counterparty) => BotsEnabled || !IsBot(counterparty);
}
