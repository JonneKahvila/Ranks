using System.Globalization;
using Microsoft.Extensions.Logging;
using Ranks.CustomCombos.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Players;

namespace Ranks.CustomCombos.Menus;

/// <summary>The combos screen, shown under core's <c>Statistics</c> and by this module's command.</summary>
/// <remarks>
/// Names no <c>Ranks.Contract</c> type, so it is not a JIT hazard on a server without core — the
/// registration that does name one lives in <c>RanksBridge</c>, and core's periods arrive here as
/// plain delegates.
///
/// Every list keeps the <c>customcombos.list</c> id whichever period it shows: a renderer drawing
/// that page reads the figures off its <see cref="ComboStatRow"/> items, so it is right for any
/// period without knowing which one.
/// </remarks>
internal sealed class ComboMenus
{
    private const string ListId = "customcombos.list";

    /// <remarks>Ranks' <c>RanksPeriods.Initial</c>: the period of everything recorded before periods were switched on.</remarks>
    private const long InitialPeriod = 0;

    private readonly ISwiftlyCore _core;
    private readonly ICustomCombosApi _api;
    private readonly Func<long?> _currentPeriod;
    private readonly Func<Task<IReadOnlyList<long>>> _listPeriods;
    private readonly ILogger _logger;

    public ComboMenus(
        ISwiftlyCore core,
        ICustomCombosApi api,
        Func<long?> currentPeriod,
        Func<Task<IReadOnlyList<long>>> listPeriods,
        ILogger logger)
    {
        _core = core;
        _api = api;
        _currentPeriod = currentPeriod;
        _listPeriods = listPeriods;
        _logger = logger;
    }

    public void Open(IPlayer player) => Build(player).Open(player);

    /// <summary>The current period's combos while core runs periods, else the lifetime list it always was.</summary>
    public IMenu Build(IPlayer player)
    {
        var steamId = player.SteamID;

        return _currentPeriod() is { } current
            ? BuildCurrent(steamId, current)
            : BuildList(steamId, ICustomCombosApi.AllTime, _core.Localizer["menu.combos.title"], _core.Localizer["menu.combos.empty"]);
    }

    /// <summary>The current period's list, under rows opening the lifetime list and the past periods.</summary>
    /// <remarks>The two rows come first, so they stay on the first page however many combos follow.</remarks>
    private IMenu BuildCurrent(ulong steamId, long current) =>
        BuildList(
            steamId,
            current,
            Title(_core.Localizer["menu.combos.current"]),
            _core.Localizer["menu.combos.empty_current"],
            new SubmenuComponent(_core.Localizer["menu.combos.alltime"], _ =>
                BuildList(steamId, ICustomCombosApi.AllTime, Title(_core.Localizer["menu.combos.alltime"]), _core.Localizer["menu.combos.empty"])),
            new SubmenuComponent(_core.Localizer["menu.combos.previous"], _ => BuildPeriodPicker(steamId, current)));

    /// <summary>One period's combos, or the lifetime counts for <see cref="ICustomCombosApi.AllTime"/>, highest first.</summary>
    /// <remarks>
    /// The item type is the row, never <c>IMenuComponent</c>: the component renders each item
    /// through <c>Formatter</c>, whose default is <c>ToString()</c> — so a component as the item
    /// draws its own type name. The loader catches only to log, and rethrows so the component shows
    /// <c>FailedText</c>.
    /// </remarks>
    private IMenu BuildList(ulong steamId, long period, string title, string emptyText, params IMenuComponent[] above)
    {
        var builder = _core.Menu.CreateMenu(ListId)
            .WithTitle(title)
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8);

        foreach (var component in above)
            builder.Add(component);

        return builder
            .Add(new PagedCollectionComponent<ComboStatRow>(async () =>
            {
                try
                {
                    return await _api.GetCombosAsync(steamId, period).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ranks_CustomCombos: failed to build the combo menu for {SteamId} (period {Period}).", steamId, period);
                    throw;
                }
            })
            {
                Formatter = r => _core.Localizer["menu.combos.row", _api.GetComboName(r.ComboKey), r.Count],
                EmptyText = emptyText,
                FailedText = _core.Localizer["menu.combos.error"],
            })
            .AddFooter(new DividerComponent())
            .Build();
    }

    /// <summary>Every period core holds but the current one, newest first, each opening its own list.</summary>
    /// <remarks>
    /// The list is opened from a row handler, so its parent is set by hand: adoption only covers a
    /// menu a <c>SubmenuComponent</c> opened, and without it the back key would close the tree.
    /// </remarks>
    private IMenu BuildPeriodPicker(ulong steamId, long current) =>
        _core.Menu.CreateMenu("customcombos.periods")
            .WithTitle(_core.Localizer["menu.combos.previous"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new PagedCollectionComponent<long>(async () =>
            {
                try
                {
                    var periods = await _listPeriods().ConfigureAwait(false);

                    return periods.Where(period => period != current).ToArray();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ranks_CustomCombos: failed to list past periods for {SteamId}.", steamId);
                    throw;
                }
            })
            {
                Formatter = PeriodLabel,
                OnItemSelected = ctx =>
                {
                    var child = BuildList(steamId, ctx.Item, Title(PeriodLabel(ctx.Item)), EmptyTextFor(ctx.Item));
                    child.Parent = ctx.Session.Menu;
                    child.Open(ctx.Player);
                },
                EmptyText = _core.Localizer["menu.combos.seasons_empty"],
                FailedText = _core.Localizer["menu.combos.error"],
            })
            .AddFooter(new DividerComponent())
            .Build();

    private string Title(string label) => _core.Localizer["menu.combos.title_in", label];

    /// <remarks>
    /// Worded and dated exactly as core labels the same period. A period id is the Unix time of its
    /// UTC start, and the date is invariant ISO because the day a period starts is the same fact in
    /// every locale.
    /// </remarks>
    private string PeriodLabel(long period) =>
        period == InitialPeriod
            ? _core.Localizer["menu.combos.initial"]
            : _core.Localizer["menu.combos.season", DateTimeOffset.FromUnixTimeSeconds(period).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)];

    /// <remarks>
    /// Combos are kept per period only from the moment periods were switched on, so the period
    /// before that is empty here while its combos sit in the lifetime total — said so, rather than
    /// letting the screen read as lost data.
    /// </remarks>
    private string EmptyTextFor(long period) =>
        _core.Localizer[period == InitialPeriod ? "menu.combos.empty_initial" : "menu.combos.empty_season"];
}
