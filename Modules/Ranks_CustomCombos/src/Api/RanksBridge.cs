using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Ranks.Contract;
using Ranks.CustomCombos.Menus;
using Ranks.CustomCombos.Combos;
using Ranks.CustomCombos.Stats;
using SwiftlyS2.Shared;

namespace Ranks.CustomCombos.Api;

/// <summary>The only file in this plugin that names a <c>Ranks.Contract</c> type.</summary>
/// <remarks>
/// This is the one mistake that takes the <b>whole server</b> down rather than just this module.
/// The CLR resolves every type a method <em>names</em> when it JITs that method, so on a box
/// without Ranks a field typed <see cref="IRanksApi"/> is enough to throw FileNotFoundException —
/// at the <em>caller's</em> frame, where this plugin's own try/catch cannot see it — and SwiftlyS2
/// then reports "Hosting failed to start" for every plugin installed.
///
/// So every member that touches a contract type is <c>[MethodImpl(NoInlining)]</c>, and everywhere
/// else reads <see cref="IsAvailable"/>, a plain bool. <see cref="ComboArbiter"/> lives here for
/// the same reason — the rule is "exactly one file", not "exactly one class".
///
/// Audit rule: grep the <em>field name</em> across the whole plugin, not the type name.
/// </remarks>
internal sealed class RanksBridge
{
    private readonly ISwiftlyCore _core;
    private readonly Writer _writer;
    private readonly ComboCounters _counters;
    private readonly ComboMenus _menus;
    private readonly ILogger _logger;

    // Deliberately not IRanksApi: a field of that type would make every method of this class a
    // JIT hazard rather than only the ones marked NoInlining. The arbiter is held the same way —
    // it implements a contract interface, so even constructing it resolves Ranks.Contract.
    private object? _api;
    private object? _module;
    private object? _arbiter;

    private ComboMatcher _matcher;
    private bool _suppress;
    private bool _playGateUnsupported;

    public RanksBridge(ISwiftlyCore core, Writer writer, ComboCounters counters, ComboMenus menus, ComboMatcher matcher, bool suppress, ILogger logger)
    {
        _core = core;
        _writer = writer;
        _counters = counters;
        _menus = menus;
        _matcher = matcher;
        _suppress = suppress;
        _logger = logger;
    }

    public bool IsAvailable { get; private set; }

    /// <summary>Raised once core reports ready, so the plugin can run its one-shot wiring.</summary>
    public event Action? CoreReady;

    /// <summary>Swaps the rule set in after a config reload.</summary>
    /// <remarks>Held even while core is absent, so the arbiter is built with current rules.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void UpdateRules(ComboMatcher matcher, bool suppress)
    {
        _matcher = matcher;
        _suppress = suppress;

        if (_arbiter is ComboArbiter arbiter) arbiter.UpdateRules(matcher, suppress);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ResetRound()
    {
        if (_arbiter is ComboArbiter arbiter) arbiter.ResetRound();
    }

    /// <summary>Refreshes core's bot policy onto the arbiter. No-op while core is absent.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void RefreshBotPolicy()
    {
        if (_api is not IRanksApi api || _arbiter is not ComboArbiter arbiter) return;

        arbiter.UpdateBotsEnabled(api.GetPointSettings().BotsEnabled);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryAcquire(IInterfaceManager interfaceManager)
    {
        try
        {
            // HasSharedInterface matches on the key string; TryGetSharedInterface resolves by Type.
            // The two disagreeing is the one symptom of a second Ranks.Contract.dll — core publishes
            // under its IRanksApi and this module asks for a different one — and the typed call alone
            // reports it as a bare false, indistinguishable from core not being installed.
            var published = interfaceManager.HasSharedInterface(IRanksApi.InterfaceKey);

            // Fail-closed on the miss: leaving a stale reference behind would point at a disposed
            // provider after the next handshake rebuild.
            if (!interfaceManager.TryGetSharedInterface<IRanksApi>(IRanksApi.InterfaceKey, out var api) || api is null)
            {
                if (published)
                    _logger.LogWarning(
                        "Ranks_CustomCombos: '{Key}' is published but did not resolve to this module's IRanksApi. " +
                        "Ranks.Contract.dll is loaded twice — it must ship only in Ranks/resources/exports.",
                        IRanksApi.InterfaceKey);

                _api = null;
                IsAvailable = false;

                return false;
            }

            _api = api;
            IsAvailable = true;

            Attach(api);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks_CustomCombos: could not acquire the Ranks API.");
            _api = null;
            IsAvailable = false;

            return false;
        }
    }

    /// <remarks>Idempotent — the handshake re-runs on every plugin load and unload.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Attach(IRanksApi api)
    {
        _module ??= new CustomCombosModule(api, _core, _writer, _counters, _menus, () => CoreReady?.Invoke());

        // Constructed here rather than in the plugin's Load: ComboArbiter implements a contract
        // interface, so merely naming it outside an isolated method would resolve Ranks.Contract
        // on a server that has no Ranks installed.
        _arbiter ??= new ComboArbiter(_core, _matcher, _suppress, _counters, PlayCounted, _logger);

        ((CustomCombosModule)_module).Register();
        ((CustomCombosModule)_module).HandOverMigrations(typeof(RanksBridge).Assembly);

        // Registering the arbiter is what makes core consult us before it awards a kill.
        api.SetKillArbiter((ComboArbiter)_arbiter);
    }

    /// <summary>Core's play gate, asked by the arbiter before it counts anything.</summary>
    /// <remarks>
    /// A Ranks older than the member fails the read at JIT and lands in the catch; its points gate
    /// is then the stand-in, which is the same answer whenever that core is scoring.
    /// </remarks>
    private bool PlayCounted()
    {
        if (_api is null) return false;

        if (!_playGateUnsupported)
        {
            try
            {
                return ReadPlayGate();
            }
            catch (MissingMethodException)
            {
                _playGateUnsupported = true;
                _logger.LogWarning(
                    "Ranks_CustomCombos: this Ranks version cannot report its play gate, so combos count only " +
                    "while it awards points. Update Ranks to 1.0.4 or newer.");
            }
        }

        return ReadPointsGate();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ReadPlayGate() => _api is IRanksApi api && api.IsPlayCounted;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ReadPointsGate() => _api is IRanksApi api && api.ArePointsEnabled;

    /// <summary>Core's current period while it runs periods; null while they are off or core is absent.</summary>
    /// <remarks>
    /// The stamp a count cut now carries (FR-DB-10). Throws MissingMethodException against a Ranks older
    /// than 1.1.0; the plugin catches it, and a caller on an event path checks <see cref="IsAvailable"/> first.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public long? CurrentPeriod() => _api is IRanksApi { ArePeriodsEnabled: true } api ? api.CurrentPeriod : null;

    /// <summary>Every period core holds, newest first; empty while core is absent.</summary>
    /// <remarks>Throws MissingMethodException against a Ranks older than 1.1.0; the plugin catches it.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public Task<IReadOnlyList<long>> ListPeriodsAsync() =>
        _api is IRanksApi api ? api.GetPeriodsAsync() : Task.FromResult<IReadOnlyList<long>>([]);

    /// <summary>The core table this module reconciles orphans against.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public string? ResolveTable(string baseName) => _api is IRanksApi api ? api.ResolveTableName(baseName) : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unwire()
    {
        // Cleared before the module unregisters: core holding a reference into an unloaded
        // assembly is the one failure here that outlives this plugin.
        if (_api is IRanksApi api) api.SetKillArbiter(null);

        if (_module is CustomCombosModule module) module.Unregister();

        _module = null;
        _arbiter = null;
        _api = null;
        IsAvailable = false;
    }
}

/// <summary>This module's hook into core's lifecycle.</summary>
internal sealed class CustomCombosModule : RanksModuleBase
{
    private readonly Writer _writer;
    private readonly ComboCounters _counters;
    private readonly ComboMenus _menus;
    private readonly Action _onCoreReady;

    public CustomCombosModule(IRanksApi api, ISwiftlyCore core, Writer writer, ComboCounters counters, ComboMenus menus, Action onCoreReady)
        : base(api, core)
    {
        _writer = writer;
        _counters = counters;
        _menus = menus;
        _onCoreReady = onCoreReady;
    }

    public override string ModuleId => "Ranks_CustomCombos";

    /// <summary>Hands this assembly's migrations to core.</summary>
    public void HandOverMigrations(Assembly assembly) => RegisterMigrations(assembly);

    protected override void OnCoreReady()
    {
        _writer.Configure(ConnectionName, Table("ranks_combos"), Table("ranks_combos_period"));

        // The label is this module's own translation — core ships no key for a screen it does not
        // know exists.
        RegisterMenuEntry(RanksMenuSections.Statistics, Core.Localizer["menu.combos.title"], _menus.Build, order: 30);

        _onCoreReady();
    }

    /// <remarks>
    /// Cut at once and stamped with the old period: <c>CurrentPeriod</c> already answers the new one,
    /// and every count still held was earned before the boundary (FR-DB-10). Null while periods are
    /// off — core still moves when another server sharing the database starts a period, and a stamp
    /// would write the frozen one per period after all.
    /// </remarks>
    protected override void OnPeriodChanged(RanksPeriodChangedArgs e) =>
        _ = _writer.FlushAsync(_counters.Drain(Api.ArePeriodsEnabled ? e.OldPeriod : null));

    /// <remarks>
    /// Core wiped only the current period, so with periods on only that period's counts go and past
    /// periods stay, as they do in core. The unflushed counts are dropped here, on the game thread
    /// that owns them; the writer's queue orders the rest after any write already under way.
    /// </remarks>
    protected override void OnPlayerReset(RanksPlayerResetArgs e)
    {
        _counters.Discard(e.SteamId);
        _ = _writer.ResetAsync(e.SteamId, Api.ArePeriodsEnabled ? Api.CurrentPeriod : null);
    }

    /// <remarks>
    /// Core has already removed its own rows by the time this fires, so the module must not expect
    /// to look the player up (FR-DB-08).
    /// </remarks>
    protected override void OnPlayersPurged(IReadOnlyList<ulong> steamIds) => _ = _writer.PurgeAsync(steamIds);
}

/// <summary>Answers core's per-kill question (FR-API-11).</summary>
/// <remarks>
/// Converts core's context into a contract-free <see cref="KillFacts"/> immediately, so the
/// matcher never names a contract type.
/// </remarks>
internal sealed class ComboArbiter : IRanksKillArbiter
{
    private readonly ISwiftlyCore _core;
    private readonly ComboCounters _counters;
    private readonly Func<bool> _isPlayCounted;
    private readonly ILogger _logger;

    // Same-tick runs, keyed by attacker then combo. Cleared on the next tick rather than compared
    // against a tick number: every death of one grenade detonation is dispatched within the frame,
    // so "until NextTick runs" is exactly the window a Count combo means.
    private readonly Dictionary<ulong, Dictionary<string, int>> _sameTick = [];
    private bool _clearScheduled;

    private ComboMatcher _matcher;
    private bool _suppress;
    // Core's own default, so a policy that never arrives under-counts rather than over-counts.
    private bool _countBots;
    private bool _firstBloodTaken;

    public ComboArbiter(ISwiftlyCore core, ComboMatcher matcher, bool suppress, ComboCounters counters, Func<bool> isPlayCounted, ILogger logger)
    {
        _core = core;
        _matcher = matcher;
        _suppress = suppress;
        _counters = counters;
        _isPlayCounted = isPlayCounted;
        _logger = logger;
    }

    public void UpdateRules(ComboMatcher matcher, bool suppress)
    {
        _matcher = matcher;
        _suppress = suppress;
    }

    /// <remarks>Refreshed on the flush timer rather than read per kill: <c>GetPointSettings</c>
    /// builds a record and a dictionary, and this runs inside core's award path.</remarks>
    public void UpdateBotsEnabled(bool botsEnabled) => _countBots = botsEnabled;

    public void ResetRound() => _firstBloodTaken = false;

    public RanksKillClaim? ClaimKill(RanksKillContext context)
    {
        try
        {
            // Core consults the arbiter only for counted play; an older one asked below MinPlayers
            // too, where a combo, or first blood, would be a stat the points refused (FR-PTS-03).
            if (!_isPlayCounted()) return null;

            var firstBlood = !_firstBloodTaken;

            var facts = new KillFacts(
                context.AttackerSteamId,
                context.VictimSteamId,
                context.VictimIsBot,
                context.Weapon,
                context.Distance,
                context.HitGroup,
                context.Attributes,
                firstBlood);

            _firstBloodTaken = true;

            ScheduleTickClear();

            var rule = _matcher.Select(facts, r => Bump(context.AttackerSteamId, r.Key));
            if (rule is null) return null;

            // Counted before the award: a cancelled award or core scoring being off does not unmake
            // the combo, and every gate has been passed by now. The bot guard is only load-bearing
            // against an older core that still called this for bot kills.
            if (_countBots || !context.VictimIsBot)
                _counters.Record(context.AttackerSteamId, rule.Key);

            return new RanksKillClaim(
                $"combo.{rule.Key}",
                rule.Points,
                Describe(rule.MessageKey),
                _suppress);
        }
        catch (Exception ex)
        {
            // Core treats a throw as "no claim", but logging it here names the module.
            _logger.LogError(ex, "Ranks_CustomCombos: failed to evaluate a kill; core is awarding it.");
            return null;
        }
    }

    private int Bump(ulong attacker, string comboKey)
    {
        if (!_sameTick.TryGetValue(attacker, out var combos))
        {
            combos = new Dictionary<string, int>(StringComparer.Ordinal);
            _sameTick[attacker] = combos;
        }

        var next = combos.GetValueOrDefault(comboKey) + 1;
        combos[comboKey] = next;

        return next;
    }

    private void ScheduleTickClear()
    {
        if (_clearScheduled) return;

        _clearScheduled = true;

        _core.Scheduler.NextTick(() =>
        {
            _sameTick.Clear();
            _clearScheduled = false;
        });
    }

    /// <remarks>
    /// Combos are operator-defined, so a missing translation is the normal case rather than an edge
    /// case — and the localizer answers a miss with <c>{lang}.{key}</c>. Falling back to the bare
    /// key is ugly but readable, where the marker is neither.
    /// </remarks>
    private string Describe(string messageKey)
    {
        var resolved = _core.Localizer[messageKey];

        return string.IsNullOrEmpty(resolved)
               || string.Equals(resolved, messageKey, StringComparison.Ordinal)
               || resolved.EndsWith('.' + messageKey, StringComparison.Ordinal)
            ? messageKey
            : resolved;
    }
}
