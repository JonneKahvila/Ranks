using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Ranks.CustomCombos.Api;
using Ranks.CustomCombos.Combos;
using Ranks.CustomCombos.Commands;
using Ranks.CustomCombos.Config;
using Ranks.CustomCombos.Menus;
using Ranks.CustomCombos.Contract;
using Ranks.CustomCombos.Stats;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Plugins;

namespace Ranks.CustomCombos;

[PluginMetadata(
    Id = "Ranks_CustomCombos",
    Version = "1.0.1",
    Name = "Ranks CustomCombos",
    Author = "Shmitzas",
    Description = "Operator-defined kill combinations for the Ranks plugin.")]
public sealed class RanksCustomCombos : BasePlugin
{
    private ILogger _logger = null!;
    private IDisposable? _reload;
    private CombosConfig _config = null!;
    private CommandsConfig _commandsConfig = null!;
    private Writer _writer = null!;
    private ComboCounters _counters = null!;
    private ComboCommands _commands = null!;
    private RoundWatcher _rounds = null!;
    private RanksBridge _bridge = null!;
    private CustomCombosApi _api = null!;
    private ComboMenus _menus = null!;
    private bool _contractLoadFailed;

    private CancellationTokenSource? _flushTimer;

    public RanksCustomCombos(ISwiftlyCore core) : base(core)
    {
    }

    public override void Load(bool hotReload)
    {
        _logger = Core.LoggerFactory.CreateLogger("Ranks_CustomCombos");

        LoadConfiguration();

        _writer = new Writer(Core, _logger);
        _counters = new ComboCounters();

        var matcher = ComboMatcher.Build(_config, _logger);

        _api = new CustomCombosApi(Core, _writer, _counters, matcher, _logger);
        _menus = new ComboMenus(Core, _api, _logger);
        _commands = new ComboCommands(Core, _menus, _logger);

        // The arbiter itself is built inside the bridge: it implements a contract interface, so
        // naming it here would resolve Ranks.Contract on a server with no Ranks installed.
        _bridge = new RanksBridge(Core, _writer, _counters, _menus, matcher, _config.SuppressCoreAwards, _logger);
        _bridge.CoreReady += OnRanksReady;

        _rounds = new RoundWatcher(() => { if (_bridge.IsAvailable) _bridge.ResetRound(); });

        // Exactly once. Registering twice re-arms first_blood mid-round.
        Core.Registrator.Register(_rounds);

        Core.Event.OnMapUnload += OnMapUnload;

        _commands.RegisterAll(_commandsConfig);
        StartFlushTimer();
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
            _logger.LogDebug(ex, "Ranks_CustomCombos: the Ranks contract could not be loaded.");
        }
    }

    /// <remarks>
    /// Published unconditionally, and without the JIT isolation the Ranks API needs: this module
    /// hard-requires the assembly it publishes from, exactly as core does with Ranks.Contract.
    /// The API answers from the database plus the unflushed counts, so it stays correct whether or
    /// not core is present — with core absent there is simply nothing new to count.
    /// </remarks>
    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) =>
        interfaceManager.AddSharedInterface<ICustomCombosApi, CustomCombosApi>(ICustomCombosApi.InterfaceKey, _api);

    /// <remarks>
    /// Absence is reported from here, not from the binder: the binder runs on every rebuild and
    /// would spam, while this runs once per load cycle and is the only point where field state is
    /// authoritative.
    /// </remarks>
    public override void OnAllPluginsLoaded()
    {
        if (_bridge.IsAvailable)
        {
            if (_config.Rules.Count == 0)
                _logger.LogWarning(
                    "Ranks_CustomCombos: no combos are configured, so nothing will be awarded. " +
                    "See resources/examples/combos.jsonc.");

            return;
        }

        // Two different faults with one symptom: core absent, or present but its contract
        // unloadable from here. Saying "not present" for the second sends the operator looking
        // for a plugin that is sitting right there.
        if (_contractLoadFailed)
        {
            _logger.LogWarning(
                "Ranks_CustomCombos: Ranks.Contract could not be loaded, so this module is idle. " +
                "Check that Ranks ships resources/exports/Ranks.Contract.dll and that this module does not.");

            return;
        }

        _logger.LogWarning(
            "Ranks_CustomCombos: the Ranks plugin is not present, so this module is idle. " +
            "No combos are being awarded and no tables were created.");
    }

    public override void Unload()
    {
        _flushTimer?.Cancel();
        _flushTimer = null;

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
            _logger.LogDebug(ex, "Ranks_CustomCombos: the Ranks contract could not be unwired.");
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
            .InitializeJsonWithModel<CombosConfig>("config.jsonc", "Combos", true)
            .InitializeJsonWithModel<CommandsConfig>("commands.jsonc", "Commands", true)
            // The full path matters: AddJsonFile resolves relative to the process working
            // directory, not the plugin folder.
            .Configure(builder =>
            {
                builder.AddJsonFile(Core.Configuration.GetConfigPath("config.jsonc"), optional: false, reloadOnChange: true);
                builder.AddJsonFile(Core.Configuration.GetConfigPath("commands.jsonc"), optional: false, reloadOnChange: true);
            });

        _config = BindConfig();
        _commandsConfig = Core.Configuration.Manager.GetSection("Commands").Get<CommandsConfig>() ?? new CommandsConfig();

        // Combos are re-parsed on every reload, so an operator edits the file and the next kill
        // uses it. Command names are not re-read: renaming one needs the old registration dropped,
        // so it stays restart-only.
        _reload = ChangeToken.OnChange(Core.Configuration.Manager.GetReloadToken, () =>
        {
            var incoming = BindConfig();

            _config = incoming;

            var rebuilt = ComboMatcher.Build(incoming, _logger);

            _bridge?.UpdateRules(rebuilt, incoming.SuppressCoreAwards);
            _api?.UpdateRules(rebuilt);
        });
    }

    private CombosConfig BindConfig()
    {
        var bound = Core.Configuration.Manager.GetSection("Combos").Get<CombosConfig>() ?? new CombosConfig();
        bound.Clamp();

        return bound;
    }

    private void OnRanksReady()
    {
        _bridge.RefreshBotPolicy();

        if (_config.SuppressCoreAwards)
            _logger.LogInformation(
                "Ranks_CustomCombos: SuppressCoreAwards is on — a kill matching a combo pays the combo instead of core's kill award.");
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

            // Polled rather than read per kill: GetPointSettings builds a record and a dictionary,
            // and the arbiter runs inside core's award path.
            if (_bridge.IsAvailable) _bridge.RefreshBotPolicy();
        });
    }

    private void OnMapUnload(IOnMapUnloadEvent e) => FlushNow();

    private void FlushNow()
    {
        var batch = _counters.Drain();
        if (batch.Count == 0) return;

        _ = _writer.FlushAsync(batch);
    }
}
