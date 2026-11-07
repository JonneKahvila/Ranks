using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Ranks.Contract;
using Ranks.WeaponStats.Menus;
using Ranks.WeaponStats.Stats;
using SwiftlyS2.Shared;

namespace Ranks.WeaponStats.Api;

/// <summary>The only file in this plugin that names a <c>Ranks.Contract</c> type.</summary>
/// <remarks>
/// This is the one mistake that takes the <b>whole server</b> down rather than just this module.
/// The CLR resolves every type a method <em>names</em> when it JITs that method, so on a box
/// without Ranks a field typed <see cref="IRanksApi"/> is enough to throw FileNotFoundException —
/// at the <em>caller's</em> frame, where this plugin's own try/catch cannot see it — and SwiftlyS2
/// then reports "Hosting failed to start" for every plugin installed.
///
/// So every member that touches a contract type is <c>[MethodImpl(NoInlining)]</c>, and everywhere
/// else reads <see cref="IsAvailable"/>, a plain bool.
///
/// Audit rule: grep the <em>field name</em> across the whole plugin, not the type name.
/// <c>_api.PlayersPurged -= handler</c> in Unload is the classic survivor.
/// </remarks>
internal sealed class RanksBridge
{
    private readonly ISwiftlyCore _core;
    private readonly Writer _writer;
    private readonly WeaponStatsMenus _menus;
    private readonly ILogger _logger;

    // Deliberately not IRanksApi: a field of that type would make every method of this class a
    // JIT hazard rather than only the ones marked NoInlining.
    private object? _api;
    private object? _module;

    public RanksBridge(ISwiftlyCore core, Writer writer, WeaponStatsMenus menus, ILogger logger)
    {
        _core = core;
        _writer = writer;
        _menus = menus;
        _logger = logger;
    }

    public bool IsAvailable { get; private set; }

    /// <summary>Raised once core reports ready, so the plugin can run its one-shot checks.</summary>
    public event Action? CoreReady;

    /// <summary>Raised on the game thread when core closes a period, with the id of the one that closed — null while periods are off.</summary>
    public event Action<long?>? PeriodClosed;

    /// <summary>Raised on the game thread after core reset a player, with the period it wiped — null while periods are off.</summary>
    public event Action<ulong, long?>? PlayerReset;

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
                        "Ranks_WeaponStats: '{Key}' is published but did not resolve to this module's IRanksApi. " +
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
            _logger.LogWarning(ex, "Ranks_WeaponStats: could not acquire the Ranks API.");
            _api = null;
            IsAvailable = false;

            return false;
        }
    }

    /// <remarks>Idempotent — the handshake re-runs on every plugin load and unload.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Attach(IRanksApi api)
    {
        _module ??= new WeaponStatsModule(
            api,
            _core,
            _writer,
            _menus,
            () => CoreReady?.Invoke(),
            closed => PeriodClosed?.Invoke(closed),
            (steamId, period) => PlayerReset?.Invoke(steamId, period));

        ((WeaponStatsModule)_module).Register();
        ((WeaponStatsModule)_module).HandOverMigrations(typeof(RanksBridge).Assembly);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unwire()
    {
        if (_module is WeaponStatsModule module) module.Unregister();

        _module = null;
        _api = null;
        IsAvailable = false;
    }

    /// <summary>The core table this module reconciles orphans against.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public string? ResolveTable(string baseName) =>
        _api is IRanksApi api ? api.ResolveTableName(baseName) : null;

    /// <summary>True when core is in FFA mode, null when core is absent or not ready.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool? IsFfaMode() => _api is IRanksApi api ? api.GetPointSettings().FfaMode : null;

    /// <summary>Core's bot policy, null when core is absent or not ready.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool? BotsEnabled() => _api is IRanksApi api ? api.GetPointSettings().BotsEnabled : null;

    /// <summary>Core's play gate, null when core is absent or not ready.</summary>
    /// <remarks>Throws MissingMethodException against a Ranks older than the member; the plugin catches it.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool? IsPlayCounted() => _api is IRanksApi api ? api.IsPlayCounted : null;

    /// <summary>Core's points gate, the fallback for a Ranks that cannot report its play gate.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool? ArePointsEnabled() => _api is IRanksApi api ? api.ArePointsEnabled : null;

    /// <summary>The period a write lands in now: core's current one while periods are on, else null.</summary>
    /// <remarks>
    /// Null while they are off even though core still reports a period, because nothing is written
    /// per period then and a stamp would put counts in a period core is not keeping (FR-DB-10).
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public long? WritePeriod() => _api is IRanksApi { ArePeriodsEnabled: true } api ? api.CurrentPeriod : null;

    /// <summary>Every period holding core data, newest first; empty when core is absent.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public Task<IReadOnlyList<long>> GetPeriodsAsync() =>
        _api is IRanksApi api ? api.GetPeriodsAsync() : Task.FromResult<IReadOnlyList<long>>([]);
}

/// <summary>This module's hook into core's lifecycle.</summary>
/// <remarks>
/// Lives beside the bridge rather than in its own file because it also names contract types, and
/// the isolation rule above is "exactly one file", not "exactly one class".
/// </remarks>
internal sealed class WeaponStatsModule : RanksModuleBase
{
    private readonly Writer _writer;
    private readonly WeaponStatsMenus _menus;
    private readonly Action _onCoreReady;
    private readonly Action<long?> _onPeriodClosed;
    private readonly Action<ulong, long?> _onPlayerReset;

    public WeaponStatsModule(
        IRanksApi api,
        ISwiftlyCore core,
        Writer writer,
        WeaponStatsMenus menus,
        Action onCoreReady,
        Action<long?> onPeriodClosed,
        Action<ulong, long?> onPlayerReset)
        : base(api, core)
    {
        _writer = writer;
        _menus = menus;
        _onCoreReady = onCoreReady;
        _onPeriodClosed = onPeriodClosed;
        _onPlayerReset = onPlayerReset;
    }

    public override string ModuleId => "Ranks_WeaponStats";

    /// <summary>Hands this assembly's migrations to core.</summary>
    /// <remarks>
    /// Core dedupes on (assembly, pluginId), so calling it from every handshake — which re-runs on
    /// every other plugin's load and unload — is harmless.
    /// </remarks>
    public void HandOverMigrations(Assembly assembly) => RegisterMigrations(assembly);

    /// <remarks>
    /// Table names are resolved here rather than in Load: a module's Load runs before the
    /// handshake, so there is no API to ask at that point.
    /// </remarks>
    protected override void OnCoreReady()
    {
        _writer.Configure(
            ConnectionName,
            Table("ranks_weapon_stats"),
            Table("ranks_hit_stats"),
            Table("ranks_weapon_hit_stats"),
            Table("ranks_weapon_stats_period"),
            Table("ranks_hit_stats_period"),
            Table("ranks_weapon_hit_stats_period"));

        // Labels are resolved from this module's own translation file, because core ships no key
        // for a screen it does not know exists.
        RegisterMenuEntry(RanksMenuSections.Statistics, Core.Localizer["menu.weapons.title"], _menus.BuildWeapons, order: 10);
        RegisterMenuEntry(RanksMenuSections.Statistics, Core.Localizer["menu.hitgroups.title"], _menus.BuildHitGroups, order: 20);

        _onCoreReady();
    }

    /// <remarks>
    /// Core has already removed its own rows by the time this fires, so the module must not expect
    /// to look the player up.
    /// </remarks>
    protected override void OnPlayersPurged(IReadOnlyList<ulong> steamIds) => _ = _writer.PurgeAsync(steamIds);

    /// <remarks>
    /// Hands on <c>OldPeriod</c>: core raises this after <c>CurrentPeriod</c> has moved, so the counts
    /// still pending were earned in the period that closed (FR-DB-10). Null while periods are off —
    /// core still moves when another server sharing the database starts a period, and a stamp would
    /// write the frozen one per period after all.
    /// </remarks>
    protected override void OnPeriodChanged(RanksPeriodChangedArgs e) =>
        _onPeriodClosed(Api.ArePeriodsEnabled ? e.OldPeriod : null);

    /// <remarks>
    /// Core wiped the player's current period, which is what this module clears too while periods are
    /// on. While they are off the module keeps nothing but all time, so all time is what goes.
    /// </remarks>
    protected override void OnPlayerReset(RanksPlayerResetArgs e) =>
        _onPlayerReset(e.SteamId, Api.ArePeriodsEnabled ? Api.CurrentPeriod : null);
}
