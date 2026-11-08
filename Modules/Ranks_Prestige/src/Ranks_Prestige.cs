using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Ranks.Prestige.Api;
using Ranks.Prestige.Commands;
using Ranks.Prestige.Config;
using Ranks.Prestige.Contract;
using Ranks.Prestige.Menus;
using Ranks.Prestige.Prestiging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;

namespace Ranks.Prestige;

[PluginMetadata(
    Id = "Ranks_Prestige",
    Version = "1.1.1",
    Name = "Ranks Prestige",
    Author = "Shmitzas",
    Description = "Virtual point deduction and prestige levels for the Ranks plugin.")]
public sealed class RanksPrestige : BasePlugin
{
    private ILogger _logger = null!;
    private IDisposable? _reload;
    private PrestigeConfig _config = null!;
    private CommandsConfig _commandsConfig = null!;
    private PrestigeCommands _commands = null!;
    private PrestigeMenus _menus = null!;
    private RanksBridge _bridge = null!;
    private PrestigeApi _api = null!;
    private bool _contractLoadFailed;

    public RanksPrestige(ISwiftlyCore core) : base(core)
    {
    }

    public override void Load(bool hotReload)
    {
        _logger = Core.LoggerFactory.CreateLogger("Ranks_Prestige");

        LoadConfiguration();

        var ladder = PrestigeLadder.Build(_config, _logger);

        _bridge = new RanksBridge(Core, ladder, _config, _logger);
        _api = new PrestigeApi(_bridge, _config);
        _menus = new PrestigeMenus(Core, _bridge, _config);
        _bridge.AttachMenus(_menus);
        _commands = new PrestigeCommands(Core, _menus, _config, _logger);

        _commands.RegisterAll(_commandsConfig);
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) =>
        interfaceManager.AddSharedInterface<IPrestigeApi, PrestigeApi>(IPrestigeApi.InterfaceKey, _api);

    /// <remarks>
    /// Acquired here rather than in Load: the shared interface does not exist yet in Load.
    /// </remarks>
    public override void UseSharedInterface(IInterfaceManager interfaceManager) => Acquire(interfaceManager);

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager) => Acquire(interfaceManager);

    public override void Unload()
    {
        _commands?.UnregisterAll();

        // Before the reload token: Unwire clears every live overlay, and a reload firing midway
        // would hand the bridge a ladder it is in the middle of tearing down.
        // Guarded for the same JIT hazard as Acquire: Unwire names contract types, so a missing
        // Ranks.Contract.dll throws into this frame rather than into Unwire's own body.
        try
        {
            _bridge?.Unwire();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ranks_Prestige: the Ranks contract could not be unwired.");
        }

        _reload?.Dispose();
    }

    /// <remarks>
    /// The catch has to be here rather than inside the bridge. A missing Ranks.Contract.dll fails
    /// when the CLR JITs TryAcquire, which surfaces in this frame before the bridge's own catch can
    /// run — so a catch inside it would never see this.
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
            _logger.LogDebug(ex, "Ranks_Prestige: the Ranks contract could not be loaded.");
        }
    }

    /// <remarks>
    /// Absence is reported from here, not from Acquire: the handshake re-runs on every plugin
    /// load and unload, so warning there repeats once per rebuild.
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
                "Ranks_Prestige: Ranks.Contract could not be loaded, so this module is idle. " +
                "Check that Ranks ships resources/exports/Ranks.Contract.dll and that this module does not.");

            return;
        }

        _logger.LogWarning(
            "Ranks_Prestige: the Ranks plugin is not present, so this module is idle. " +
            "No prestige levels are being applied.");
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
            .InitializeJsonWithModel<PrestigeConfig>("config.jsonc", "Prestige", true)
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

        // Levels are re-read on every reload and re-applied to everyone present. Command names are
        // not: renaming one needs the old registration dropped, so it stays restart-only.
        _reload = ChangeToken.OnChange(Core.Configuration.Manager.GetReloadToken, () =>
        {
            var incoming = BindConfig();

            _config = incoming;
            _commands?.UpdateConfig(incoming);
            _menus?.UpdateConfig(incoming);
            _api?.UpdateConfig(incoming);
            _bridge?.UpdateLadder(PrestigeLadder.Build(incoming, _logger), incoming);
        });
    }

    private PrestigeConfig BindConfig() =>
        Core.Configuration.Manager.GetSection("Prestige").Get<PrestigeConfig>() ?? new PrestigeConfig();
}
