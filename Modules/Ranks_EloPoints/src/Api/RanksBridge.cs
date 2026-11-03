using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Ranks.Contract;
using Ranks.EloPoints.Match;
using Ranks.EloPoints.Rating;
using SwiftlyS2.Shared;

namespace Ranks.EloPoints.Api;

/// <summary>The only file in this plugin that names a <c>Ranks.Contract</c> type.</summary>
/// <remarks>
/// This is the one mistake that takes the <b>whole server</b> down rather than just this module.
/// The CLR resolves every type a method <em>names</em> when it JITs that method, so on a box
/// without Ranks a field typed <see cref="IRanksApi"/> is enough to throw FileNotFoundException —
/// at the <em>caller's</em> frame, where this plugin's own try/catch cannot see it — and SwiftlyS2
/// then reports "Hosting failed to start" for every plugin installed.
///
/// So every member that touches a contract type is <c>[MethodImpl(NoInlining)]</c>, and everywhere
/// else reads <see cref="IsAvailable"/> or one of the flattened records below. Returning a
/// <c>RanksPlayerSnapshot</c> or <c>RanksPointSettings</c> would put a contract type in a
/// signature outside this file, which is the same hazard one indirection further out.
///
/// Audit rule: grep the <em>field name</em> across the whole plugin, not the type name.
/// <c>_api.PlayersPurged -= handler</c> in Unload is the classic survivor.
/// </remarks>
internal sealed class RanksBridge
{
    private readonly ISwiftlyCore _core;
    private readonly RatingStore _store;
    private readonly ILogger _logger;

    // Deliberately not IRanksApi: a field of that type would make every method of this class a
    // JIT hazard rather than only the ones marked NoInlining.
    private object? _api;
    private object? _module;

    public RanksBridge(ISwiftlyCore core, RatingStore store, ILogger logger)
    {
        _core = core;
        _store = store;
        _logger = logger;
    }

    public bool IsAvailable { get; private set; }

    /// <summary>Raised once core reports ready, so the plugin can run its one-shot checks.</summary>
    public event Action? CoreReady;

    /// <summary>Set by the module so the tracker can be reached from core's lifecycle events.</summary>
    public MatchTracker? Tracker { get; set; }

    /// <summary>Set by the plugin so the published API can be given core's resolved table name.</summary>
    public EloPointsApi? PublishedApi { get; set; }

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
                        "Ranks_EloPoints: '{Key}' is published but did not resolve to this module's IRanksApi. " +
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
            _logger.LogWarning(ex, "Ranks_EloPoints: could not acquire the Ranks API.");
            _api = null;
            IsAvailable = false;

            return false;
        }
    }

    /// <remarks>Idempotent — the handshake re-runs on every plugin load and unload.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Attach(IRanksApi api)
    {
        _module ??= new EloPointsModule(api, _core, this, _store, () => CoreReady?.Invoke());

        ((EloPointsModule)_module).Register();
        ((EloPointsModule)_module).HandOverMigrations(typeof(RanksBridge).Assembly);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unwire()
    {
        if (_module is EloPointsModule module) module.Unregister();

        _module = null;
        _api = null;
        IsAvailable = false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int? GetPoints(ulong steamId) => _api is IRanksApi api ? api.GetPoints(steamId) : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SetPoints(ulong steamId, int points, string reason) => (_api as IRanksApi)?.SetPoints(steamId, points, reason);

    /// <remarks>
    /// Explicit rather than waiting for core's periodic pass: the record is evicted once saved and
    /// the next pass may be minutes away (FR-MOD-05 rule 5).
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SavePlayer(ulong steamId) => _ = (_api as IRanksApi)?.SavePlayerAsync(steamId);

    /// <summary>Core's counters as a flattened record, or null when core is absent or the player is untracked.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public PerfCounters? GetCounters(ulong steamId)
    {
        if (_api is not IRanksApi api || api.GetSnapshot(steamId) is not { } snapshot) return null;

        return new PerfCounters(
            snapshot.Kills,
            snapshot.Deaths,
            snapshot.Assists,
            snapshot.RoundWins,
            snapshot.BombsPlanted,
            snapshot.BombsDefused);
    }

    /// <summary>A fresh snapshot per call, so it tracks live config reloads rather than caching at load.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public CoreSettings? GetSettings()
    {
        if (_api is not IRanksApi api) return null;

        var settings = api.GetPointSettings();

        return new CoreSettings(
            settings.UseCoreRankingLogic,
            settings.StartPoints,
            settings.MinPoints,
            settings.MinPlayers,
            settings.WarmupPoints,
            settings.BotsEnabled);
    }

    /// <summary>The core table name, resolved with the operator's affixes.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public string? ResolveTable(string baseName) => _api is IRanksApi api ? api.ResolveTableName(baseName) : null;

    /// <summary>Core's play gate, null when core is absent or not ready.</summary>
    /// <remarks>Throws MissingMethodException against a Ranks older than the member; the tracker catches it.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool? IsPlayCounted() => _api is IRanksApi api ? api.IsPlayCounted : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public string? ConnectionName() => _api is IRanksApi api ? api.ConnectionName : null;
}

/// <summary>This module's hook into core's lifecycle.</summary>
/// <remarks>
/// Lives beside the bridge rather than in its own file because it also names contract types, and
/// the isolation rule above is "exactly one file", not "exactly one class".
/// </remarks>
internal sealed class EloPointsModule : RanksModuleBase
{
    private readonly RanksBridge _bridge;
    private readonly RatingStore _store;
    private readonly Action _onCoreReady;

    public EloPointsModule(IRanksApi api, ISwiftlyCore core, RanksBridge bridge, RatingStore store, Action onCoreReady)
        : base(api, core)
    {
        _bridge = bridge;
        _store = store;
        _onCoreReady = onCoreReady;
    }

    public override string ModuleId => "Ranks_EloPoints";

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
        _store.Configure(ConnectionName, Table("ranks_elopoints"));
        _bridge.PublishedApi?.Configure(ConnectionName, Table("ranks_elopoints"));
        _onCoreReady();
    }

    /// <remarks>
    /// Two jobs in one handler: capture the match-start counters for a mid-match joiner, and push
    /// a rating that was settled while the player was already gone back into core.
    /// </remarks>
    protected override void OnPlayerDataLoaded(RanksPlayerSnapshot player)
    {
        var counters = new PerfCounters(
            player.Kills,
            player.Deaths,
            player.Assists,
            player.RoundWins,
            player.BombsPlanted,
            player.BombsDefused);

        _bridge.Tracker?.OnPlayerDataLoaded(player.SteamId, counters, player.Points);

        if (_store.TryGetCached(player.SteamId, out var rating) && (int)Math.Round(rating) != player.Points)
            Api.SetPoints(player.SteamId, (int)Math.Round(rating), "elopoints.resync");
    }

    /// <remarks>
    /// Core has already removed its own rows by the time this fires, so the module must not expect
    /// to look the player up.
    /// </remarks>
    protected override void OnPlayersPurged(IReadOnlyList<ulong> steamIds) => _ = _store.PurgeAsync(steamIds);
}
