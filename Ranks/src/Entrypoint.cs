using Microsoft.Extensions.Logging;
using Ranks.Api;
using Ranks.Commands;
using Ranks.Config;
using Ranks.Contract;
using Ranks.Database;
using Ranks.Logging;
using Ranks.Menus;
using Ranks.Players;
using Ranks.Ranking;
using Ranks.Scoreboard;
using Ranks.Scoring;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;

namespace Ranks;

[PluginMetadata(
    Id = "Ranks",
    Version = "1.1.0",
    Name = "Ranks",
    Author = "Shmitzas",
    Description = "Extensive ranks and playtime data collection plugin.")]
public sealed partial class Ranks : BasePlugin
{
    /// <remarks>Bounded so a hung database cannot hold the server in shutdown indefinitely.</remarks>
    private static readonly TimeSpan UnloadFlushBudget = TimeSpan.FromSeconds(8);

    /// <summary>Tables core reads, with the columns a missed migration would leave out.</summary>
    private static readonly (string Table, string Columns)[] RequiredReads =
    [
        ("ranks_players", "steam_id, headshots, hits, misses, period"),
        ("ranks_settings", "steam_id"),
        ("ranks_players_history", "steam_id, period"),
    ];

    private ILogger _logger = null!;
    private ConfigHost<RanksConfig> _config = null!;
    private ConfigHost<PointsConfig> _points = null!;
    private ConfigHost<LadderConfig> _ladderConfig = null!;
    private ConfigHost<CommandsConfig> _commandsConfig = null!;
    private DetailLog _detail = null!;
    private TableNames _tables = null!;
    private DatabaseConnection _database = null!;
    private SchemaMigrator _migrator = null!;
    private PlayerStore _store = null!;
    private PlayerRegistry _registry = null!;
    private RankLadder _ladder = null!;
    private RanksEventHub _events = null!;
    private PlayerSessions _sessions = null!;
    private AwardPipeline _awards = null!;
    private ScoringHandlers _scoring = null!;
    private ScoreboardService _scoreboard = null!;
    private RankRevealer _revealer = null!;
    private PlayerReset _reset = null!;
    private MenuEntryRegistry _menuEntries = null!;
    private RankMenus _menus = null!;
    private CommandRegistry _commands = null!;
    private RanksApi _api = null!;

    /// <remarks>
    /// Stays empty: the engine is not up yet, and anything touching it here aborts plugin
    /// loading (implementation guide §2.1).
    /// </remarks>
    public Ranks(ISwiftlyCore core) : base(core)
    {
    }

    /// <remarks>
    /// The API instance is built in Load and only published here. AddSharedInterface throws on a
    /// duplicate key, but the rebuild disposes the manager before re-running registration, so
    /// re-registering is safe.
    /// </remarks>
    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) =>
        interfaceManager.AddSharedInterface<IRanksApi, RanksApi>(IRanksApi.InterfaceKey, _api);

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
    }

    public override void Load(bool hotReload)
    {
        _logger = Core.LoggerFactory.CreateLogger("Ranks");

        LoadConfiguration();
        OpenDatabase();
        BuildServices();

        var schemaError = MigrateSchema();
        var status = new SchemaProbe(_database, _tables, _logger).Probe(RequiredReads, schemaError);
        _sessions.SchemaUsable = status.Usable && _sessions.TryInitializePeriod();

        if (_sessions.SchemaUsable)
        {
            _sessions.Start(hotReload);
            Core.Registrator.Register(_scoring);
            _commands.RegisterAll();
            _scoreboard.Start();
        }

        WarnIfNobodyIsScoring();

        // Outside the probe gate: the reveal is a user message carrying no core data, so a schema
        // core cannot read is no reason to stop showing the ranks CS2 already holds.
        Core.Registrator.Register(_revealer);
        _revealer.Start();

        // A hot reload lands mid-round with players already in, and the reveal they were sent is
        // long gone from the client's state.
        if (hotReload) _revealer.RevealToAll();

        // Fires whether or not the probe passed — a module needs to know core settled either way.
        _api.MarkCoreReady();

        // force: !hotReload, because a hot reload is how an operator deploys a fix mid-session and
        // players are connected for it. Migrations are fine there; a purge is not.
        _ = _sessions.RunMaintenanceAsync(force: !hotReload);
    }

    /// <summary>Migrates modules once every plugin has had a chance to register its assembly.</summary>
    public override void OnAllPluginsLoaded() => _migrator.MigratePendingModules();

    public override void Unload()
    {
        _commands?.UnregisterAll();
        _revealer?.Dispose();
        _scoreboard?.Dispose();
        _sessions?.Stop();
        _sessions?.FlushOnUnload(UnloadFlushBudget);
        _events?.Clear();

        _commandsConfig?.Dispose();
        _ladderConfig?.Dispose();
        _points?.Dispose();
        _config?.Dispose();
    }

    private void LoadConfiguration()
    {
        _config = ConfigFiles.Main(Core, _logger);
        _points = ConfigFiles.Points(Core, _logger);
        _ladderConfig = ConfigFiles.Ladder(Core, _logger);
        _commandsConfig = ConfigFiles.Commands(Core, _logger);
        _detail = new DetailLog(_logger, _config);
    }

    private void OpenDatabase()
    {
        _tables = new TableNames(_config.Current.Database);
        _database = DatabaseConnection.Resolve(Core, _config.Current.Database.Connection, _logger);
        _store = new PlayerStore(_database, _tables);
        _migrator = new SchemaMigrator(Core, _database, _tables, _logger);
    }

    private void BuildServices()
    {
        _registry = new PlayerRegistry();
        _ladder = new RankLadder(() => _ladderConfig.Current.Tiers);
        _events = new RanksEventHub(_logger);

        _sessions = new PlayerSessions(Core, _config, _store, _registry, _ladder, _events, _detail, _logger);
        _awards = new AwardPipeline(Core, _config, _points, _ladder, _registry, _events, _detail, _logger);
        _scoreboard = new ScoreboardService(Core, _config, _ladder, _registry, _logger);
        _revealer = new RankRevealer(Core, _config, _detail, _logger);
        _scoring = new ScoringHandlers(Core, _config, _awards, _registry, _detail, _logger);
        _reset = new PlayerReset(_ladder, _sessions, _scoreboard, _events);
        _menuEntries = new MenuEntryRegistry(_logger);

        // Before the menus, which read the leaderboard through it. Nothing in the API needs them
        // back, so the order is a one-way dependency rather than a cycle to break.
        _api = new RanksApi(
            _config, _points, _registry, _sessions, _awards, _ladder, _scoreboard,
            _store, _migrator, _menuEntries, _tables, _database, _events, _logger);

        _menus = new RankMenus(Core, _ladder, _registry, _commandsConfig, _reset, _menuEntries, _api, _logger);

        _commands = new CommandRegistry(Core, _commandsConfig, _menus, _registry, _reset, _api, _logger);

        // The scoreboard follows points rather than polling, so core never fights another plugin
        // for the clan-tag field.
        _events.RankChanged += e => _scoreboard.Apply(e.SteamId);
        _events.RankChanged += e => _revealer.OnRankChanged(e.SteamId);
        _events.PlayerDataLoaded += snapshot => _scoreboard.Apply(snapshot.SteamId);
        _events.PeriodChanged += _ => OnPeriodChanged();
        _scoring.RoundEnded += OnRoundEnded;
    }

    private void OnRoundEnded()
    {
        _sessions.RollOverIfDue();
        _ = _sessions.SaveAllAsync();
        _ = _sessions.RunMaintenanceAsync(force: false);
        _scoreboard.ApplyAll();
    }

    /// <summary>Redraws every scoreboard and tells the server the season turned over.</summary>
    /// <remarks>
    /// Without the announcement every rank on the server drops at once with nothing to explain it,
    /// which reads as lost data rather than a new season.
    /// </remarks>
    private void OnPeriodChanged()
    {
        _scoreboard.ApplyAll();

        foreach (var player in Core.PlayerManager.GetAllPlayers())
            if (!player.IsFakeClient)
                Chat.Send(Core, player, "period.started");
    }

    /// <remarks>
    /// Unconditional, because core has no registry of who is scoring and deliberately does not
    /// acquire one — so it cannot tell whether a rating module is present (FR-PTS-00).
    /// </remarks>
    private void WarnIfNobodyIsScoring()
    {
        if (_config.Current.Rank.UseCoreRankingLogic) return;

        _logger.LogWarning(
            "Ranks: Rank.UseCoreRankingLogic is false, so core awards no points. " +
            "That is correct only if a rating module is installed to own scoring; otherwise nobody is awarding anything.");
    }

    /// <summary>Runs core's own migrations, keeping the failure for the probe to judge.</summary>
    /// <remarks>
    /// Does not rethrow: a migration that fails against a reachable database leaves an install
    /// that can still read and write, which is the normal shape on a host granting DML but not
    /// DDL. The probe decides whether that is survivable (§4.6).
    /// </remarks>
    private Exception? MigrateSchema()
    {
        try
        {
            _migrator.MigrateCore();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
