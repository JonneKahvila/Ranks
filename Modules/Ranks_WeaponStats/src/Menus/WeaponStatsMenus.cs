using System.Globalization;
using Microsoft.Extensions.Logging;
using Ranks.WeaponStats.Config;
using Ranks.WeaponStats.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Players;

namespace Ranks.WeaponStats.Menus;

/// <summary>This module's two screens, shown under core's <c>Statistics</c> and by its own command.</summary>
/// <remarks>
/// <para>
/// Names no <c>Ranks.Contract</c> type, so it is not a JIT hazard on a server without core and
/// needs none of <c>RanksBridge</c>'s isolation — the registration that does name one lives there.
/// A menu built here is handed to core as a factory result and adopted by the entry that opened
/// it, which is what makes the player's back key return to core's menu.
/// </para>
/// <para>
/// Every figure comes from <see cref="IWeaponStatsApi"/> rather than the writer, because that is
/// the one source that folds in the unflushed session delta — a menu reading the database directly
/// would show a player nothing for the round they are in the middle of.
/// </para>
/// <para>
/// While core's periods are on, both screens open on a choice of this season, all time and the
/// seasons before (FR-DB-10); while they are off, every id, title and figure is what it was before
/// periods existed. CustomHUD keys on the ids: it draws <c>weaponstats.weapons</c> from the rows, so
/// every view keeps that one, but it fetches all-time figures itself for
/// <c>weaponstats.hitgroups</c> and <c>weaponstats.weaponhitgroups</c>, so a period view of those
/// takes an id of its own.
/// </para>
/// </remarks>
internal sealed class WeaponStatsMenus
{
    /// <remarks>Core's <c>RanksPeriods.Initial</c>, restated because this class names no contract type.</remarks>
    private const long BeforeSeasons = 0;

    private readonly ISwiftlyCore _core;
    private readonly IWeaponStatsApi _api;
    private readonly Func<long?> _writePeriod;
    private readonly Func<Task<IReadOnlyList<long>>> _listPeriods;
    private readonly ILogger _logger;

    private volatile WeaponStatsConfig _config;

    /// <param name="writePeriod">Core's current period while its periods are on, else null.</param>
    /// <param name="listPeriods">Every period core holds data for, newest first.</param>
    public WeaponStatsMenus(
        ISwiftlyCore core,
        IWeaponStatsApi api,
        WeaponStatsConfig config,
        Func<long?> writePeriod,
        Func<Task<IReadOnlyList<long>>> listPeriods,
        ILogger logger)
    {
        _core = core;
        _api = api;
        _config = config;
        _writePeriod = writePeriod;
        _listPeriods = listPeriods;
        _logger = logger;
    }

    public void UpdateConfig(WeaponStatsConfig config) => _config = config;

    public void OpenWeapons(IPlayer player) => BuildWeapons(player).Open(player);

    /// <summary>The Weapon stats entry: the weapon list itself, or a choice of seasons while core's periods are on.</summary>
    public IMenu BuildWeapons(IPlayer player)
    {
        var steamId = player.SteamID;

        return _writePeriod() is null
            ? BuildWeaponList(steamId, StatsView.PeriodsOff)
            : BuildSeasons("weaponstats.seasons.weapons", _core.Localizer["menu.weapons.title"], view => BuildWeaponList(steamId, view));
    }

    /// <summary>The Overall Hit Stats entry: the breakdown itself, or a choice of seasons while core's periods are on.</summary>
    public IMenu BuildHitGroups(IPlayer player)
    {
        var steamId = player.SteamID;

        return _writePeriod() is null
            ? BuildHitGroupList(steamId, StatsView.PeriodsOff)
            : BuildSeasons("weaponstats.seasons.hitgroups", _core.Localizer["menu.hitgroups.title"], view => BuildHitGroupList(steamId, view));
    }

    /// <summary>This season, all time and the seasons before, each opening <paramref name="build"/> for itself.</summary>
    /// <remarks>This season is read when its row is chosen, so a screen left open across a boundary opens the new one.</remarks>
    private IMenu BuildSeasons(string menuId, string title, Func<StatsView, IMenu> build) =>
        _core.Menu.CreateMenu(menuId)
            .WithTitle(title)
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new SubmenuComponent(_core.Localizer["menu.period.current"], _ => build(ThisSeason())))
            .Add(new SubmenuComponent(_core.Localizer["menu.period.alltime"], _ =>
                build(new StatsView(IWeaponStatsApi.AllTime, _core.Localizer["menu.period.alltime"]))))
            .Add(new SubmenuComponent(_core.Localizer["menu.period.previous"], _ => BuildPeriodPicker(build)))
            .AddFooter(new DividerComponent())
            .Build();

    /// <summary>The period writes go to now, or all time under the old titles once core's periods are switched off.</summary>
    private StatsView ThisSeason() =>
        _writePeriod() is { } current ? new StatsView(current, _core.Localizer["menu.period.current"]) : StatsView.PeriodsOff;

    /// <summary>Every season but the current one, newest first, each opening <paramref name="build"/> for itself.</summary>
    /// <remarks>
    /// Core's list rather than one of this module's own, because a season is core's (FR-DB-10). A
    /// season this module holds nothing for still opens and says so — "Before seasons" always does,
    /// since nothing was kept per season before seasons existed; those figures are under all time.
    /// </remarks>
    private IMenu BuildPeriodPicker(Func<StatsView, IMenu> build) =>
        _core.Menu.CreateMenu("weaponstats.periods")
            .WithTitle(_core.Localizer["menu.period.title"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new PagedCollectionComponent<PeriodRow>(async () =>
            {
                try
                {
                    var current = _writePeriod();
                    var periods = await _listPeriods().ConfigureAwait(false);

                    return LabelPastPeriods(periods, current);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ranks_WeaponStats: failed to list past seasons.");
                    throw;
                }
            })
            {
                Formatter = row => row.Label,
                OnItemSelected = ctx =>
                {
                    var child = build(new StatsView(ctx.Item.Period, ctx.Item.Label));
                    child.Parent = ctx.Session.Menu;
                    child.Open(ctx.Player);
                },
                EmptyText = _core.Localizer["menu.period.empty"],
                FailedText = _core.Localizer["menu.error"],
            })
            .AddFooter(new DividerComponent())
            .Build();

    /// <summary>Per-weapon kills, accuracy and headshot rate for one view, best first.</summary>
    /// <remarks>
    /// The item type is the row, never <c>IMenuComponent</c>: the component renders each item
    /// through <c>Formatter</c>, whose default is <c>ToString()</c> — so a component as the item
    /// draws its own type name. Every view keeps the <c>weaponstats.weapons</c> id, because a
    /// renderer drawing that page takes the figures off these rows.
    /// </remarks>
    private IMenu BuildWeaponList(ulong steamId, StatsView view)
    {
        var minShots = _config.Display.MinShotsToList;

        return _core.Menu.CreateMenu("weaponstats.weapons")
            .WithTitle(view.Label is null ? _core.Localizer["menu.weapons.title"] : _core.Localizer["menu.weapons.title_period", view.Label])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new PagedCollectionComponent<WeaponStatRow>(async () =>
            {
                try
                {
                    var rows = await _api.GetWeaponsAsync(steamId, view.Period).ConfigureAwait(false);

                    // The operator's noise floor still applies: a menu page of weapons fired once
                    // each buries the ones the player actually uses. There is no row cap, though —
                    // paging is what the old TopWeapons limit existed to stand in for.
                    return rows
                        .Where(r => r.Shots >= minShots)
                        .OrderByDescending(r => r.Kills)
                        .ThenByDescending(r => r.Damage)
                        .ToList();
                }
                catch (Exception ex)
                {
                    // Rethrown so the component shows FailedText; caught only to get it logged.
                    _logger.LogError(ex, "Ranks_WeaponStats: failed to build the weapon menu for {SteamId}.", steamId);
                    throw;
                }
            })
            {
                Formatter = r => _core.Localizer[
                    "menu.weapons.row",
                    Display(r.Weapon),
                    r.Kills,
                    Percent(r.Headshots, r.Kills),
                    Percent(r.Hits, r.Hits + r.Misses)],
                ItemHint = _ => _core.Localizer["menu.weapons.hint"],
                OnItemSelected = ctx =>
                {
                    var child = BuildWeaponHitGroups(ctx.Player, ctx.Item.Weapon, view);

                    // Set explicitly: adoption only happens for a menu a SubmenuComponent opens,
                    // and this one is opened from a row handler, so without it the back key would
                    // close the tree instead of returning to the weapon list.
                    child.Parent = ctx.Session.Menu;
                    child.Open(ctx.Player);
                },
                EmptyText = _core.Localizer[view.IsAllTime ? "menu.weapons.empty" : "menu.weapons.empty_period"],
                FailedText = _core.Localizer["menu.error"],
            })
            .AddFooter(new DividerComponent())
            .Build();
    }

    /// <summary>Where every landed hit went, across all weapons, for one view.</summary>
    /// <remarks><c>weaponstats.hitgroups</c> is all time's alone: a renderer drawing that page fetches all-time figures itself.</remarks>
    private IMenu BuildHitGroupList(ulong steamId, StatsView view) =>
        BuildHitGroupMenu(
            view.IsAllTime ? "weaponstats.hitgroups" : "weaponstats.hitgroups.period",
            view.Label is null ? _core.Localizer["menu.hitgroups.title"] : _core.Localizer["menu.hitgroups.title_period", view.Label],
            steamId,
            weapon: null,
            view);

    /// <summary>The same breakdown for one weapon, opened from a row of the weapon list.</summary>
    /// <remarks><c>weaponstats.weaponhitgroups</c> is all time's alone, for the same reason as <c>weaponstats.hitgroups</c>.</remarks>
    private IMenu BuildWeaponHitGroups(IPlayer player, string weapon, StatsView view) =>
        BuildHitGroupMenu(
            // Per-weapon id so a server owner can rebind or restyle the drill-down on its own;
            // the weapon is not in the id, or every gun would be a separate config scope.
            view.IsAllTime ? "weaponstats.weaponhitgroups" : "weaponstats.weaponhitgroups.period",
            view.Label is null
                ? _core.Localizer["menu.weaponhits.title", Display(weapon)]
                : _core.Localizer["menu.weaponhits.title_period", Display(weapon), view.Label],
            player.SteamID,
            weapon,
            view);

    private IMenu BuildHitGroupMenu(string menuId, string title, ulong steamId, string? weapon, StatsView view) =>
        _core.Menu.CreateMenu(menuId)
            .WithTitle(title)
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new PagedCollectionComponent<HitRow>(async () =>
            {
                try
                {
                    // One weapon's rows come from the per-weapon table rather than by filtering
                    // the overall one, which carries no weapon to filter on.
                    var rows = weapon is null
                        ? (await _api.GetHitGroupsAsync(steamId, view.Period).ConfigureAwait(false))
                            .Select(r => (r.HitGroup, r.Hits, r.Damage))
                        : (await _api.GetWeaponHitGroupsAsync(steamId, view.Period).ConfigureAwait(false))
                            .Where(r => string.Equals(r.Weapon, weapon, StringComparison.OrdinalIgnoreCase))
                            .Select(r => (r.HitGroup, r.Hits, r.Damage));

                    var listed = rows.ToList();

                    // Share is projected here rather than formatted later: the formatter sees one
                    // row at a time and cannot know the total.
                    var total = listed.Sum(r => r.Hits);

                    return listed
                        .OrderByDescending(r => r.Hits)
                        .Select(r => new HitRow(HitGroupName(r.HitGroup), r.Hits, Percent(r.Hits, total), r.Damage))
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ranks_WeaponStats: failed to build the hitgroup menu for {SteamId}.", steamId);
                    throw;
                }
            })
            {
                Formatter = r => _core.Localizer["menu.hitgroups.row", r.Name, r.Hits, r.Share, r.Damage],
                EmptyText = _core.Localizer[view.IsAllTime ? "menu.hitgroups.empty" : "menu.hitgroups.empty_period"],
                FailedText = _core.Localizer["menu.error"],
            })
            .AddFooter(new DividerComponent())
            .Build();

    private readonly record struct HitRow(string Name, long Hits, double Share, long Damage);

    /// <summary>Which figures a screen shows — one period's, or all time — and the season label its title carries.</summary>
    /// <remarks>A null <see cref="Label"/> is the only view while periods are off, titled as before they existed.</remarks>
    private readonly record struct StatsView(long Period, string? Label)
    {
        public static StatsView PeriodsOff => new(IWeaponStatsApi.AllTime, null);

        public bool IsAllTime => Period == IWeaponStatsApi.AllTime;
    }

    /// <summary>A past season as the picker lists it, labelled once so the row and the view it opens agree.</summary>
    private sealed record PeriodRow(long Period, string Label);

    /// <remarks>
    /// Labelled as core labels the same seasons: a season's end is the start of the one after it, so
    /// only one that ran exactly one calendar month is named after it.
    /// </remarks>
    private PeriodRow[] LabelPastPeriods(IReadOnlyList<long> periods, long? current)
    {
        var starts = (current is { } live ? periods.Append(live) : periods).Distinct().Order().ToArray();

        return periods
            .Where(period => period != current)
            .Select(period => new PeriodRow(period, PeriodLabel(period, starts.FirstOrDefault(start => start > period))))
            .ToArray();
    }

    /// <remarks>
    /// Core's wording for core's ids: an id is the Unix time of the season's UTC start, printed as an
    /// invariant ISO date because the day a season starts is the same fact in every locale.
    /// <paramref name="nextStart"/> is 0 when no later season exists.
    /// </remarks>
    private string PeriodLabel(long period, long nextStart) =>
        period == BeforeSeasons
            ? _core.Localizer["menu.period.initial"]
            : IsCalendarMonth(period, nextStart)
                ? MonthLabel(period)
                : _core.Localizer["menu.period.row", DateTimeOffset.FromUnixTimeSeconds(period).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)];

    /// <remarks>Month names come from the translation file (<c>month.1</c> … <c>month.12</c>), not the server's culture.</remarks>
    private string MonthLabel(long period)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(period).UtcDateTime;
        return _core.Localizer["menu.period.month", _core.Localizer[$"month.{start.Month}"], start.Year];
    }

    /// <remarks>The same test core applies (FR-DB-10), repeated here because core's helper is internal to it.</remarks>
    private static bool IsCalendarMonth(long start, long end)
    {
        var first = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime;

        return first is { Day: 1, TimeOfDay.Ticks: 0 }
            && new DateTimeOffset(first.AddMonths(1)).ToUnixTimeSeconds() == end;
    }

    private static double Percent(long part, long whole) => whole == 0 ? 0 : Math.Round(part * 100.0 / whole, 1);

    /// <remarks>
    /// Strips the stored designer-name prefix and upper-cases for display only. The stored form
    /// stays <c>weapon_ak47</c>, which is what the entity system, the dumps and weapon_fire all
    /// agree on — so it is also what the per-weapon lookup matches on.
    /// </remarks>
    private static string Display(string weapon) =>
        (weapon.StartsWith("weapon_", StringComparison.Ordinal) ? weapon[7..] : weapon).ToUpperInvariant();

    /// <remarks>
    /// Mirrors <c>HitGroup_t</c>, which is not a dense range — 9 is <c>UNUSED</c> and never
    /// appears. An unrecognised value is shown as its number rather than dropped, so a future
    /// engine addition surfaces as an odd row instead of silently vanishing from the totals.
    /// </remarks>
    private string HitGroupName(short hitGroup) => hitGroup switch
    {
        0 => _core.Localizer["hitgroup.generic"],
        1 => _core.Localizer["hitgroup.head"],
        2 => _core.Localizer["hitgroup.chest"],
        3 => _core.Localizer["hitgroup.stomach"],
        4 => _core.Localizer["hitgroup.left_arm"],
        5 => _core.Localizer["hitgroup.right_arm"],
        6 => _core.Localizer["hitgroup.left_leg"],
        7 => _core.Localizer["hitgroup.right_leg"],
        8 => _core.Localizer["hitgroup.neck"],
        10 => _core.Localizer["hitgroup.gear"],
        11 => _core.Localizer["hitgroup.special"],
        _ => _core.Localizer["hitgroup.unknown", hitGroup],
    };
}
