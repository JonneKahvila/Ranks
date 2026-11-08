using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging;
using Ranks.WeaponStats.Contract;
using Ranks.WeaponStats.Api;
using Ranks.WeaponStats.Commands;
using Ranks.WeaponStats.Config;
using Ranks.WeaponStats.Menus;
using Ranks.WeaponStats.Stats;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Plugins;

namespace Ranks.WeaponStats;

[PluginMetadata(
    Id = "Ranks_WeaponStats",
    Version = "1.1.2",
    Name = "Ranks WeaponStats",
    Author = "Shmitzas",
    Description = "Per-weapon and per-hitgroup statistics for the Ranks plugin.")]
public sealed class RanksWeaponStats : BasePlugin
{
    private ILogger _logger = null!;
    private IDisposable? _reload;
    private WeaponStatsConfig _config = null!;
    private CommandsConfig _commandsConfig = null!;
    private Writer _writer = null!;
    private Collector _collector = null!;
    private StatsCommands _commands = null!;
    private WeaponStatsMenus _menus = null!;
    private RanksBridge _bridge = null!;
    private WeaponStatsApi _api = null!;

    private CancellationTokenSource? _flushTimer;
    private bool _reconciled;
    private bool _hotReload;
    private bool _contractLoadFailed;
    private bool _playGateUnsupported;
    private volatile bool _periodsUnsupported;

    public RanksWeaponStats(ISwiftlyCore core) : base(core)
    {
    }

    public override void Load(bool hotReload)
    {
        _hotReload = hotReload;
        _logger = Core.LoggerFactory.CreateLogger("Ranks_WeaponStats");

        LoadConfiguration();

        _writer = new Writer(Core, _logger);
        _collector = new Collector(Core, _config, IsPlayCounted, WritePeriod, _logger);

        // The API comes first now: the menus read through it, and the command and the bridge both
        // hand the same menus on, so nothing downstream can build a second view of these numbers.
        _api = new WeaponStatsApi(Core, _writer, _collector, WritePeriod, _logger);
        _menus = new WeaponStatsMenus(Core, _api, _config, WritePeriod, ListPeriodsAsync, _logger);
        _commands = new StatsCommands(Core, _writer, _menus, _logger);
        _bridge = new RanksBridge(Core, _writer, _menus, _logger);
        _bridge.CoreReady += OnRanksReady;
        _bridge.PeriodClosed += OnPeriodClosed;
        _bridge.PlayerReset += OnPlayerReset;

        // Exactly once. Registering twice double-counts every shot.
        Core.Registrator.Register(_collector);

        Core.Event.OnClientPutInServer += OnPutInServer;
        Core.Event.OnClientDisconnected += OnDisconnected;
        Core.Event.OnMapUnload += OnMapUnload;

        _commands.RegisterAll(_commandsConfig);
        StartFlushTimer();

        if (hotReload) AdoptConnectedPlayers();
    }

    /// <remarks>
    /// Acquired here rather than in Load: the shared interface does not exist yet in Load, which
    /// is also why RegisterMigrations cannot live there.
    /// </remarks>
    public override void UseSharedInterface(IInterfaceManager interfaceManager) => Acquire(interfaceManager);

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager) => Acquire(interfaceManager);

    /// <remarks>
    /// The catch has to be here rather than inside the bridge. A missing Ranks.Contract.dll fails
    /// when the CLR JITs TryAcquire, which surfaces in this frame before the bridge's own catch can
    /// run — so a catch inside it would never see this. Reported at Debug because OnAllPluginsLoaded
    /// owns the operator-facing message.
    /// </remarks>
    private void Acquire(IInterfaceManager interfaceManager)
    {
        try
        {
            _bridge.TryAcquire(interfaceManager);
        }
        catch (Exception ex)
        {
            _contractLoadFailed = true;
            _logger.LogDebug(ex, "Ranks_WeaponStats: the Ranks contract could not be loaded.");
        }
    }

    /// <remarks>
    /// Published unconditionally, and without the JIT isolation the Ranks API needs: this module
    /// hard-requires the assembly it publishes from, exactly as core does with Ranks.Contract.
    /// The API answers from the database plus the unflushed session delta, so it stays correct
    /// whether or not core is present.
    /// </remarks>
    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) =>
        interfaceManager.AddSharedInterface<IWeaponStatsApi, WeaponStatsApi>(IWeaponStatsApi.InterfaceKey, _api);

    /// <remarks>
    /// Absence is reported from here, not from the binder: the binder runs on every rebuild and
    /// would spam, while this runs once per load cycle and is the only point where field state is
    /// authoritative.
    /// </remarks>
    public override void OnAllPluginsLoaded()
    {
        if (_bridge.IsAvailable) return;

        // Two different faults with one symptom: core absent, or present but its contract
        // unloadable from here. Saying "not present" for the second sends the operator looking
        // for a plugin that is sitting right there.
        if (_contractLoadFailed)
        {
            _logger.LogWarning(
                "Ranks_WeaponStats: Ranks.Contract could not be loaded, so this module is idle. " +
                "Check that Ranks ships resources/exports/Ranks.Contract.dll and that this module does not.");

            return;
        }

        _logger.LogWarning(
            "Ranks_WeaponStats: the Ranks plugin is not present, so this module is idle. " +
            "No statistics are being recorded and no tables were created.");
    }

    public override void Unload()
    {
        _flushTimer?.Cancel();
        _flushTimer = null;

        Core.Event.OnClientPutInServer -= OnPutInServer;
        Core.Event.OnClientDisconnected -= OnDisconnected;
        Core.Event.OnMapUnload -= OnMapUnload;

        _commands?.UnregisterAll();

        FlushNow();

        // Same JIT hazard as Acquire: Unwire names contract types, so a missing Ranks.Contract.dll
        // throws into this frame rather than into Unwire's own body.
        try
        {
            _bridge?.Unwire();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ranks_WeaponStats: the Ranks contract could not be unwired.");
        }

        _reload?.Dispose();
    }

    /// <remarks>
    /// Bound off <c>Core.Configuration.Manager</c>, the root the framework already owns and
    /// watches — no ServiceCollection and no IOptionsMonitor needed to get a live reload.
    /// </remarks>
    private void LoadConfiguration()
    {
        Core.Configuration
            // addMissingKeys: a plugin upgrade inserts new keys into an existing file with their
            // defaults, leaving the operator's values and formatting alone.
            .InitializeJsonWithModel<WeaponStatsConfig>("config.jsonc", "WeaponStats", true)
            .InitializeJsonWithModel<CommandsConfig>("commands.jsonc", "Commands", true)
            // The full path matters: AddJsonFile resolves relative to the process working
            // directory, not the plugin folder.
            .Configure(builder =>
            {
                builder.AddJsonFile(Core.Configuration.GetConfigPath("config.jsonc"), optional: false, reloadOnChange: true);
                builder.AddJsonFile(Core.Configuration.GetConfigPath("commands.jsonc"), optional: false, reloadOnChange: true);
            });

        _config = BindConfig();
        _commandsConfig = CommandsConfig.Bind(Core.Configuration.Manager.GetSection("Commands"));

        // The snapshot is reassigned rather than captured once, which is what keeps DetailedLogging
        // and the two Tracking switches live without a restart. Command names are not re-read:
        // renaming one needs the old registration dropped, so it stays restart-only.
        _reload = ChangeToken.OnChange(Core.Configuration.Manager.GetReloadToken, () =>
        {
            var incoming = BindConfig();

            _config = incoming;
            _collector?.UpdateConfig(incoming);
            _menus?.UpdateConfig(incoming);
        });
    }

    private WeaponStatsConfig BindConfig()
    {
        var bound = Core.Configuration.Manager.GetSection("WeaponStats").Get<WeaponStatsConfig>() ?? new WeaponStatsConfig();
        bound.Clamp();

        return bound;
    }

    private void StartFlushTimer()
    {
        var interval = _config.Database.FlushIntervalSeconds;
        if (interval <= 0) return;

        // Deliberately not paired with StopOnMapChange: the flush must outlive a map change, and
        // OnMapUnload is a flush point rather than a teardown.
        _flushTimer = Core.Scheduler.RepeatBySeconds(interval, () =>
        {
            FlushNow();

            // Picks up an operator hot-reloading core's config, which this module gets no event for.
            if (_bridge.IsAvailable) RefreshBotPolicy();
        });
    }

    private void OnPutInServer(IOnClientPutInServerEvent e)
    {
        if (Core.PlayerManager.GetPlayer(e.PlayerId) is { } player) _collector.Adopt(player);
    }

    /// <remarks>The collector detaches the final delta before clearing the slot, in that order.</remarks>
    private void OnDisconnected(IOnClientDisconnectedEvent e)
    {
        if (_collector.Release(e.PlayerId) is { } delta)
            _ = _writer.FlushAsync([delta]);
    }

    private void OnMapUnload(IOnMapUnloadEvent e) => FlushNow();

    private void FlushNow()
    {
        if (!_writer.IsReady) return;

        _ = _writer.FlushAsync(_collector.DetachAll());
    }

    /// <summary>Writes everything still pending into the period that just closed, or all time only while periods are off (FR-DB-10).</summary>
    /// <remarks>
    /// Stamped from the event rather than from <see cref="WritePeriod"/>, which already answers the new
    /// period by the time core raises it. Skipped like <see cref="FlushNow"/> while the writer is not
    /// configured, though core cannot close a period before it is ready.
    /// </remarks>
    private void OnPeriodClosed(long? closedPeriod)
    {
        if (!_writer.IsReady) return;

        _ = _writer.FlushAsync(_collector.DetachAll(closedPeriod));
    }

    /// <summary>Drops the player's unwritten counts, then clears the stored ones core's reset covered.</summary>
    /// <remarks>
    /// The session goes first and here on the game thread, before the writer queues the reset behind
    /// every batch already cut — so nothing earned before the reset can be written after it.
    /// </remarks>
    private void OnPlayerReset(ulong steamId, long? period)
    {
        _collector.Discard(steamId);
        _ = _writer.ResetAsync(steamId, period);
    }

    private void AdoptConnectedPlayers()
    {
        foreach (var player in Core.PlayerManager.GetAllPlayers())
            if (!player.IsFakeClient)
                _collector.Adopt(player);
    }

    private void OnRanksReady()
    {
        RefreshBotPolicy();
        WarnOnFfaMismatch();
        ReconcileOrphansOnce();
    }

    /// <remarks>
    /// Polled rather than read per event: GetPointSettings builds a record and a dictionary, and
    /// weapon_fire runs at roughly 200/second. A null means core is not ready, which leaves the
    /// last known value rather than silently flipping to counting bots.
    /// </remarks>
    private void RefreshBotPolicy()
    {
        if (_bridge.BotsEnabled() is { } enabled) _collector.BotsEnabled = enabled;
    }

    /// <summary>Core's play gate, asked by the collector per event.</summary>
    /// <remarks>
    /// Closed while core is absent or reloading, because core counts nothing then either. A Ranks
    /// older than the member fails the bridge call at JIT and lands in the catch; its points gate
    /// is then the stand-in, which is the same answer whenever that core is scoring.
    /// </remarks>
    private bool IsPlayCounted()
    {
        if (!_bridge.IsAvailable) return false;

        if (!_playGateUnsupported)
        {
            try
            {
                return _bridge.IsPlayCounted() ?? false;
            }
            catch (MissingMethodException)
            {
                _playGateUnsupported = true;
                _logger.LogWarning(
                    "Ranks_WeaponStats: this Ranks version cannot report its play gate, so weapon stats follow its " +
                    "points instead, and nothing is recorded while Rank.UseCoreRankingLogic is off. " +
                    "Update Ranks to 1.0.4 or newer.");
            }
        }

        return _bridge.ArePointsEnabled() ?? false;
    }

    /// <summary>The period a batch cut now belongs to: core's current one while its periods are on, else null.</summary>
    /// <remarks>
    /// Checks <see cref="RanksBridge.IsAvailable"/> first because the collector asks from game events
    /// and timers, and the published API from any thread — the bridge member it guards names a
    /// contract type. Null while core is absent: nothing is recorded then, and a period is core's.
    /// A Ranks older than 1.1.0 fails the bridge call at JIT and lands in the catch; the module then
    /// keeps all time only, exactly as it did before periods existed.
    /// </remarks>
    private long? WritePeriod()
    {
        if (!_bridge.IsAvailable || _periodsUnsupported) return null;

        try
        {
            return _bridge.WritePeriod();
        }
        catch (MissingMethodException)
        {
            _periodsUnsupported = true;
            _logger.LogWarning(
                "Ranks_WeaponStats: this Ranks version has no seasons, so weapon stats are kept for all time only " +
                "and !resetrank does not clear them. Update Ranks to 1.1.0 or newer.");

            return null;
        }
    }

    /// <summary>Every period core holds data for, newest first; empty while core is absent or has no periods.</summary>
    private Task<IReadOnlyList<long>> ListPeriodsAsync() =>
        _bridge.IsAvailable && !_periodsUnsupported ? _bridge.GetPeriodsAsync() : Task.FromResult<IReadOnlyList<long>>([]);

    /// <remarks>
    /// Ungated by DetailedLogging: it fires once, and it is the only signal between a
    /// misconfigured server and a module that looks broken. It warns rather than auto-correcting —
    /// an operator may run FFA and still not want friendly fire counted, and silently overriding
    /// their config would be the worse surprise. There is no warning for the reverse: counting
    /// total damage output on a team server is a legitimate choice.
    /// </remarks>
    private void WarnOnFfaMismatch()
    {
        if (_bridge.IsFfaMode() is not true || _config.Tracking.CountFriendlyFire) return;

        _logger.LogWarning(
            "Ranks_WeaponStats: core reports FFA mode but Tracking.CountFriendlyFire is false — " +
            "same-team hits and kills will not be recorded, which on an FFA server is nearly all of them. " +
            "Set Tracking.CountFriendlyFire to true.");
    }

    /// <remarks>
    /// Cold start only, matching the rule that heavy work runs when no player can feel it. Skipped
    /// on hot reload, and skipped when core could not resolve its table — deleting against a
    /// half-broken schema is worse than leaving orphans.
    /// </remarks>
    private void ReconcileOrphansOnce()
    {
        if (_reconciled || _hotReload) return;

        _reconciled = true;

        if (_bridge.ResolveTable("ranks_players") is { } playersTable)
            _ = _writer.ReconcileOrphansAsync(playersTable);
    }
}
