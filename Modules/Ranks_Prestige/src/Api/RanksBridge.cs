using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Ranks.Contract;
using Ranks.Prestige.Config;
using Ranks.Prestige.Contract;
using Ranks.Prestige.Menus;
using Ranks.Prestige.Prestiging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;

namespace Ranks.Prestige.Api;

/// <summary>The only file in this plugin that names a <c>Ranks.Contract</c> type.</summary>
/// <remarks>
/// This is the one mistake that takes the <b>whole server</b> down rather than just this module.
/// The CLR resolves every type a method <em>names</em> when it JITs that method, so on a box
/// without Ranks a field typed <see cref="IRanksApi"/> is enough to throw FileNotFoundException —
/// at the <em>caller's</em> frame, where this plugin's own try/catch cannot see it — and SwiftlyS2
/// then reports "Hosting failed to start" for every plugin installed.
///
/// So every member that touches a contract type is <c>[MethodImpl(NoInlining)]</c>, and everywhere
/// else reads <see cref="IsAvailable"/>, a plain bool. <see cref="PrestigeModule"/> lives here for
/// the same reason — the rule is "exactly one file", not "exactly one class".
///
/// Audit rule: grep the <em>field name</em> across the whole plugin, not the type name.
/// </remarks>
internal sealed class RanksBridge
{
    private readonly ISwiftlyCore _core;
    private readonly ILogger _logger;

    // Deliberately not IRanksApi: a field of that type would make every method of this class a
    // JIT hazard rather than only the ones marked NoInlining.
    private object? _api;
    private object? _module;

    private PrestigeLadder _ladder;
    private PrestigeConfig _config;

    public RanksBridge(ISwiftlyCore core, PrestigeLadder ladder, PrestigeConfig config, ILogger logger)
    {
        _core = core;
        _ladder = ladder;
        _config = config;
        _logger = logger;
    }

    // Assigned by AttachMenus during Load, which always runs before the handshake that reads it.
    private PrestigeMenus _menus = null!;
    public bool IsAvailable { get; private set; }

    /// <remarks>
    /// Set after construction because the menus hold this bridge: the cycle has to be closed from
    /// one side, and the bridge is the side that can hand them to core.
    /// </remarks>
    public void AttachMenus(PrestigeMenus menus) => _menus = menus;

    /// <summary>Swaps the ladder in after a config reload, then re-applies to everyone present.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void UpdateLadder(PrestigeLadder ladder, PrestigeConfig config)
    {
        _ladder = ladder;
        _config = config;

        if (_module is PrestigeModule module) module.Refresh(ladder, config);
    }

    /// <summary>The player's current standing, or null when core is absent or has no record yet.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public PrestigeStanding? StandingFor(IPlayer player)
    {
        if (_api is not IRanksApi api || !api.IsLoaded(player)) return null;

        return _ladder.Resolve(api.GetPoints(player));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public (int Current, int Required)? ProgressToNextFor(IPlayer player)
    {
        if (_api is not IRanksApi api || !api.IsLoaded(player)) return null;

        return _ladder.ProgressToNext(api.GetPoints(player));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public PrestigeProgress? ProgressFor(ulong steamId)
    {
        if (_api is not IRanksApi api || !api.IsLoaded(steamId)) return null;

        var points = api.GetPoints(steamId);

        return new PrestigeProgress(_ladder.Resolve(points).Level, points, _ladder.ProgressToNext(points)?.Required);
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
                        "Ranks_Prestige: '{Key}' is published but did not resolve to this module's IRanksApi. " +
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
            _logger.LogWarning(ex, "Ranks_Prestige: could not acquire the Ranks API.");
            _api = null;
            IsAvailable = false;

            return false;
        }
    }

    /// <remarks>Idempotent — the handshake re-runs on every plugin load and unload.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Attach(IRanksApi api)
    {
        _module ??= new PrestigeModule(api, _core, _ladder, _config, _menus, _logger);

        ((PrestigeModule)_module).Register();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unwire()
    {
        // Overlays are core-side state keyed to a live session, so leaving them behind would show
        // every player a deduction with nothing left running to maintain or explain it.
        if (_module is PrestigeModule module)
        {
            module.ClearEveryone();
            module.Unregister();
        }

        _module = null;
        _api = null;
        IsAvailable = false;
    }
}

/// <summary>This module's hook into core's lifecycle.</summary>
/// <remarks>
/// Everything happens through <c>SetDisplayOverride</c> (FR-API-07). The module never writes the
/// clan tag, the rank icon or the score itself — core resolves all three from the overlay, which
/// is what keeps a prestige prefix from fighting core for the tag field on a repeating timer.
/// </remarks>
internal sealed class PrestigeModule : RanksModuleBase
{
    private readonly ILogger _logger;
    private readonly PrestigeMenus _menus;

    private PrestigeLadder _ladder;
    private PrestigeConfig _config;

    public PrestigeModule(
        IRanksApi api, ISwiftlyCore core, PrestigeLadder ladder, PrestigeConfig config, PrestigeMenus menus, ILogger logger)
        : base(api, core)
    {
        _ladder = ladder;
        _config = config;
        _menus = menus;
        _logger = logger;
    }

    public override string ModuleId => "Ranks_Prestige";

    /// <remarks>
    /// The label is this module's own translation — core ships no key for a screen it does not
    /// know exists. The overlay pass covers a hot reload: core does not replay PlayerDataLoaded
    /// for players who were already resident, so without it they carry no prefix until their
    /// points next move.
    /// </remarks>
    protected override void OnCoreReady()
    {
        RegisterMenuEntry(RanksMenuSections.Statistics, Core.Localizer["menu.prestige.title"], _menus.Build, order: 40);
        ApplyEveryone();
    }

    public void ApplyEveryone()
    {
        foreach (var player in Core.PlayerManager.GetAllPlayers())
            if (!player.IsFakeClient)
                ApplyTo(player);
    }

    public void Refresh(PrestigeLadder ladder, PrestigeConfig config)
    {
        _ladder = ladder;
        _config = config;

        ApplyEveryone();
    }

    public void ClearEveryone()
    {
        foreach (var player in Core.PlayerManager.GetAllPlayers())
            if (!player.IsFakeClient)
                SafeClear(player);
    }

    protected override void OnPlayerDataLoaded(RanksPlayerSnapshot player)
    {
        if (Core.PlayerManager.GetPlayerFromSteamId(player.SteamId, allowUnauthorized: false) is { } p) ApplyTo(p);
    }

    /// <remarks>
    /// The overlay is recomputed per award rather than per round: it is derived from the point
    /// total, so anything that moves points moves the deduction. Core applies the write itself and
    /// skips it when nothing changed, so a steady-state kill costs one comparison.
    /// </remarks>
    protected override void OnPointsChanged(RanksPointsChangedArgs e)
    {
        if (Core.PlayerManager.GetPlayerFromSteamId(e.SteamId, allowUnauthorized: false) is { } p) ApplyTo(p);
    }

    /// <remarks>
    /// Core moves every player's points when a season starts and one player's on a reset, raising
    /// <c>PointsChanged</c> for neither because neither is an award. Without these the overlay keeps
    /// showing the old prestige until the player's next point (FR-DB-10).
    /// </remarks>
    protected override void OnPeriodChanged(RanksPeriodChangedArgs e) => ApplyEveryone();

    protected override void OnPlayerReset(RanksPlayerResetArgs e)
    {
        if (Core.PlayerManager.GetPlayerFromSteamId(e.SteamId, allowUnauthorized: false) is { } p) ApplyTo(p);
    }

    private void ApplyTo(IPlayer player)
    {
        try
        {
            if (!_config.Enabled || !Api.IsLoaded(player))
            {
                SafeClear(player);
                return;
            }

            var standing = _ladder.Resolve(Api.GetPoints(player));

            if (standing.Level == 0)
            {
                SafeClear(player);
                return;
            }

            Api.SetDisplayOverride(player, new RanksDisplayOverride(
                Points: standing.DisplayPoints,
                RankMode: standing.RankMode.Length > 0 ? standing.RankMode : null,
                TagPrefix: FormatPrefix(standing.Level)));

            if (_config.DetailedLogging)
            {
                _logger.LogInformation(
                    "Ranks_Prestige: {SteamId} is prestige {Level}, showing {Display}.",
                    player.SteamID, standing.Level, standing.DisplayPoints);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks_Prestige: could not apply the overlay for {SteamId}.", player.SteamID);
        }
    }

    private string? FormatPrefix(int level)
    {
        if (_config.TagPrefix.Length == 0) return null;

        // Operator-authored format string: a stray brace is a config typo, not a crash.
        try
        {
            return string.Format(_config.TagPrefix, level);
        }
        catch (FormatException)
        {
            _logger.LogWarning(
                "Ranks_Prestige: TagPrefix '{Prefix}' is not a valid format string; writing no prefix.",
                _config.TagPrefix);

            return null;
        }
    }

    private void SafeClear(IPlayer player)
    {
        try
        {
            Api.ClearDisplayOverride(player);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks_Prestige: could not clear the overlay for {SteamId}.", player.SteamID);
        }
    }
}
