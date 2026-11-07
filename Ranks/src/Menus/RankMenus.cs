using System.Globalization;
using Microsoft.Extensions.Logging;
using Ranks.Api;
using Ranks.Config;
using Ranks.Contract;
using Ranks.Database;
using Ranks.Players;
using Ranks.Ranking;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Players;

namespace Ranks.Menus;

/// <summary>Core's menu tree: one root of categories, everything else a submenu under it.</summary>
/// <remarks>
/// <para>
/// Built through <c>Core.Menu</c>, the 1.5.0 framework; the classic <c>Core.MenusAPI</c> surface is
/// <c>[Obsolete]</c>. A player has at most one open menu across every plugin, so opening one of
/// these closes someone else's — that is normal, not an error to guard against. Menus are released
/// automatically when the plugin unloads, so there is no teardown here.
/// </para>
/// <para>
/// Everything nests with <see cref="SubmenuComponent"/> rather than <c>ButtonComponent</c> +
/// <c>Open()</c>: <c>Open()</c> starts a new root with no back-stack, so the framework's configured
/// navigation would have nothing to go back to. The same rule is what lets a module's menu sit
/// under <c>Statistics</c> and still return here — a menu with no parent of its own is adopted by
/// the entry that opened it.
/// </para>
/// </remarks>
internal sealed class RankMenus
{
    private readonly ISwiftlyCore _core;
    private readonly RankLadder _ladder;
    private readonly PlayerRegistry _registry;
    private readonly ConfigHost<CommandsConfig> _commands;
    private readonly PlayerReset _reset;
    private readonly MenuEntryRegistry _entries;
    private readonly RanksApi _api;
    private readonly ILogger _logger;

    public RankMenus(
        ISwiftlyCore core,
        RankLadder ladder,
        PlayerRegistry registry,
        ConfigHost<CommandsConfig> commands,
        PlayerReset reset,
        MenuEntryRegistry entries,
        RanksApi api,
        ILogger logger)
    {
        _core = core;
        _ladder = ladder;
        _registry = registry;
        _commands = commands;
        _reset = reset;
        _entries = entries;
        _api = api;
        _logger = logger;
    }

    // ---- Entry points --------------------------------------------------------

    /// <summary>The one way in. Every other opener below is a shortcut to a branch of this.</summary>
    public void OpenRoot(IPlayer player)
    {
        if (!Loaded(player)) return;

        // Ids are namespaced because a menu id doubles as its keybind configuration scope, which
        // keeps the server's generated keybind file readable. Each one names what it shows.
        var builder = _core.Menu.CreateMenu("ranks.root")
            .WithTitle(_core.Localizer["menu.root.title"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new SubmenuComponent(_core.Localizer["menu.root.rank"], _ => BuildMyRank(player)))
            .Add(new SubmenuComponent(_core.Localizer["menu.root.stats"], _ => BuildStatistics(player)))
            .Add(new SubmenuComponent(_core.Localizer["menu.root.top"], _ => BuildTop(player)))
            .Add(new SubmenuComponent(_core.Localizer["menu.root.settings"], _ => BuildSettings(player)))
            .Add(new ButtonComponent(_core.Localizer["menu.root.help"], ctx =>
            {
                OnGameThread(() => SendHelp(player));
                ctx.Session.Close();
                return ValueTask.CompletedTask;
            }));

        // Last, and only for someone who can actually run at least one of them.
        if (AdminCommands.Any(key => CanRun(key, player, out _)))
        {
            builder.Add(new ButtonComponent(_core.Localizer["menu.root.admin"], ctx =>
            {
                OnGameThread(() => SendAdminHelp(player));
                ctx.Session.Close();
                return ValueTask.CompletedTask;
            }));
        }

        builder.AddFooter(new DividerComponent()).Build().Open(player);
    }

    public void OpenStatistics(IPlayer player)
    {
        if (Loaded(player)) BuildStatistics(player)?.Open(player);
    }

    public void OpenLadder(IPlayer player) => BuildLadder(player).Open(player);

    /// <summary>Straight to the XP list when it is the only one to choose, otherwise the Leaderboard screen.</summary>
    /// <remarks>
    /// The menu's Leaderboard row always opens the screen, which is where the playtime board lives;
    /// the command keeps the single step it always had on a server without seasons or module boards.
    /// </remarks>
    public void OpenTop(IPlayer player)
    {
        var menu = !_api.ArePeriodsEnabled && _entries.For(RanksMenuSections.Leaderboard).Count == 0
            ? BuildTopList(_api.CurrentPeriod, _core.Localizer["menu.top.title"])
            : BuildTop(player);

        menu.Open(player);
    }

    /// <summary>Opens the playtime board, the target of <c>!ttop</c> and of the Leaderboard screen's Playtime row.</summary>
    public void OpenPlaytimeTop(IPlayer player) => BuildPlaytimeTop().Open(player);

    /// <remarks>Chat, not a menu: there is no record to render and the player needs to know why.</remarks>
    private bool Loaded(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not null) return true;

        Chat.Send(_core, player, "error.not_loaded");
        return false;
    }

    // ---- My rank -------------------------------------------------------------

    private IMenu? BuildMyRank(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not { } record) return null;

        var tier = _ladder.TierFor(record.Points);
        var toNext = _ladder.PointsToNext(record.Points);
        var progress = (float)_ladder.ProgressInBand(record.Points);

        return _core.Menu.CreateMenu("ranks.myrank")
            .WithTitle(_core.Localizer["menu.rank.title"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new TextComponent(_core.Localizer["menu.rank.current", tier.Name]))
            .Add(new TextComponent(_core.Localizer["menu.rank.XP", record.Points]))
            .Add(new ProgressBarComponent(
                toNext is { } remaining
                    ? _core.Localizer["menu.rank.next", _ladder.NextTier(record.Points)!.Name, remaining]
                    : _core.Localizer["menu.rank.top_band"],
                _ => progress,
                multiLine: false))
            .Add(new SubmenuComponent(_core.Localizer["menu.rank.ladder"], _ => BuildLadder(player)))
            .AddFooter(new DividerComponent())
            .Build();
    }

    private IMenu BuildLadder(IPlayer player)
    {
        var index = _registry.BySlot(player.Slot)?.RankIndex ?? -1;

        var builder = _core.Menu.CreateMenu("ranks.ladder")
            .WithTitle(_core.Localizer["menu.ladder.title"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8);

        var tiers = _ladder.Tiers;

        for (var i = 0; i < tiers.Count; i++)
        {
            var key = i == index ? "menu.ladder.row_current" : "menu.ladder.row";
            builder.Add(new TextComponent(_core.Localizer[key, tiers[i].Name, tiers[i].Points]));
        }

        return builder.AddFooter(new DividerComponent()).Build();
    }

    // ---- Statistics ----------------------------------------------------------

    /// <summary>Core's own counters, plus one row per module registered under this section.</summary>
    /// <remarks>With periods enabled the main row is this period's, so all time and past periods get rows of their own.</remarks>
    private IMenu? BuildStatistics(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is null) return null;

        var steamId = player.SteamID;
        var periods = _api.ArePeriodsEnabled;

        var builder = _core.Menu.CreateMenu("ranks.stats")
            .WithTitle(_core.Localizer["menu.stats.title"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new SubmenuComponent(_core.Localizer[periods ? "menu.stats.current" : "menu.stats.main"], _ => BuildMainStats(player)));

        if (periods)
        {
            builder
                .Add(new SubmenuComponent(_core.Localizer["menu.stats.alltime"], _ =>
                    BuildSnapshotStats("ranks.stats.period", _core.Localizer["menu.stats.alltime"], steamId, RanksPeriods.AllTime)))
                .Add(new SubmenuComponent(_core.Localizer["menu.stats.previous"], _ =>
                    BuildPeriodPicker((period, label) => BuildSnapshotStats("ranks.stats.period", label, steamId, period))));
        }

        var entries = _entries.For(RanksMenuSections.Statistics);

        foreach (var (moduleId, entry) in entries)
            builder.Add(new SubmenuComponent(entry.Label, _ => _entries.Build(moduleId, entry, player)));

        // Says why the list is short, rather than leaving the player to read it as a broken menu.
        if (entries.Count == 0)
            builder.Add(new TextComponent(_core.Localizer["menu.stats.modules_empty"]));

        return builder.AddFooter(new DividerComponent()).Build();
    }

    /// <remarks>
    /// Keeps the <c>ranks.stats.main</c> id for the period the player is in now: a renderer drawing
    /// that page reads the player's live snapshot, which is exactly this period's figures.
    /// </remarks>
    private IMenu? BuildMainStats(IPlayer player)
    {
        if (_api.GetSnapshot(player) is not { } snapshot) return null;

        var builder = _core.Menu.CreateMenu("ranks.stats.main")
            .WithTitle(_core.Localizer[_api.ArePeriodsEnabled ? "menu.stats.current" : "menu.stats.main_title"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(10);

        foreach (var line in StatLines(snapshot))
            builder.Add(new TextComponent(line));

        return builder.AddFooter(new DividerComponent()).Build();
    }

    /// <summary>The counter lines, shared so a player's own menu and a leaderboard pick agree.</summary>
    /// <remarks>One page of ten: both menus that list these size their page to fit them, so a new line moves that number too.</remarks>
    private List<string> StatLines(RanksPlayerSnapshot record)
    {
        var kd = record.Deaths == 0 ? record.Kills : Math.Round(record.Kills / (double)record.Deaths, 2);
        var headshotRate = record.Kills == 0 ? 0 : Math.Round(record.Headshots * 100.0 / record.Kills, 1);

        // Hits + Misses, never a stored shot count: one shotgun blast is a single trigger pull
        // that can land nine hits, so these two columns are the only pair that adds up.
        var attempts = record.Hits + record.Misses;
        var accuracy = attempts == 0 ? 0 : Math.Round(record.Hits * 100.0 / attempts, 1);

        return
        [
            _core.Localizer["menu.stats.kills", record.Kills],
            _core.Localizer["menu.stats.deaths", record.Deaths],
            _core.Localizer["menu.stats.kd", kd],
            _core.Localizer["menu.stats.assists", record.Assists],
            _core.Localizer["menu.stats.headshots", record.Headshots, headshotRate],
            _core.Localizer["menu.stats.accuracy", accuracy, record.Hits, record.Misses],
            _core.Localizer["menu.stats.rounds", record.RoundWins, record.RoundLosses],
            _core.Localizer["menu.stats.matches", record.MatchWins, record.MatchLosses],
            _core.Localizer["menu.stats.bombs", record.BombsPlanted, record.BombsDefused],
            _core.Localizer["menu.stats.playtime", PlaytimeFormatter.Format(_core, record.Playtime)],
        ];
    }

    // ---- Leaderboard ---------------------------------------------------------

    /// <summary>The Leaderboard row: core's XP and playtime boards, then every module's.</summary>
    private IMenu BuildTop(IPlayer player)
    {
        var entries = _entries.For(RanksMenuSections.Leaderboard);
        var periods = _api.ArePeriodsEnabled;

        var builder = _core.Menu.CreateMenu("ranks.top.sections")
            .WithTitle(_core.Localizer["menu.top.title"]);

        if (periods) AddSeasonLine(builder);

        builder
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8);

        if (periods)
        {
            builder
                .Add(new SubmenuComponent(_core.Localizer["menu.top.current"], _ =>
                    BuildTopList(_api.CurrentPeriod, _core.Localizer["menu.top.current"])))
                .Add(new SubmenuComponent(_core.Localizer["menu.top.alltime"], _ =>
                    BuildTopList(RanksPeriods.AllTime, _core.Localizer["menu.top.alltime"])))
                .Add(new SubmenuComponent(_core.Localizer["menu.top.previous"], _ => BuildPeriodPicker(BuildTopList)));
        }
        else
        {
            builder.Add(new SubmenuComponent(_core.Localizer["menu.top.players"], _ =>
                BuildTopList(_api.CurrentPeriod, _core.Localizer["menu.top.title"])));
        }

        builder.Add(new SubmenuComponent(_core.Localizer["menu.top.playtime"], _ => BuildPlaytimeTop()));

        foreach (var (moduleId, entry) in entries)
            builder.Add(new SubmenuComponent(entry.Label, _ => _entries.Build(moduleId, entry, player)));

        return builder.AddFooter(new DividerComponent()).Build();
    }

    /// <summary>The playtime board: the list itself while periods are off, otherwise this season, all time and previous seasons.</summary>
    private IMenu BuildPlaytimeTop()
    {
        var title = _core.Localizer["menu.toptime.title"];

        if (!_api.ArePeriodsEnabled) return BuildPlaytimeList(_api.CurrentPeriod, title);

        var builder = _core.Menu.CreateMenu("ranks.top.playtime.sections").WithTitle(title);

        AddSeasonLine(builder);

        return builder
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new SubmenuComponent(_core.Localizer["menu.top.current"], _ =>
                BuildPlaytimeList(_api.CurrentPeriod, _core.Localizer["menu.top.current"])))
            .Add(new SubmenuComponent(_core.Localizer["menu.top.alltime"], _ =>
                BuildPlaytimeList(RanksPeriods.AllTime, _core.Localizer["menu.top.alltime"])))
            .Add(new SubmenuComponent(_core.Localizer["menu.top.previous"], _ => BuildPeriodPicker(BuildPlaytimeList)))
            .AddFooter(new DividerComponent())
            .Build();
    }

    /// <summary>One period's XP leaderboard, or all time.</summary>
    /// <remarks>
    /// Every variant keeps the <c>ranks.top</c> id: a renderer drawing that page reads the figures
    /// off the rows, so it is right for any period without knowing which one it shows.
    /// </remarks>
    private IMenu BuildTopList(long period, string title) =>
        BuildBoardList("ranks.top", "points", period, title,
            e => _core.Localizer["menu.top.row", e.Position, e.Name, e.Value]);

    /// <summary>One period's playtime leaderboard, or all time.</summary>
    /// <remarks>An id of its own: a renderer that recognises <c>ranks.top</c> reads every row's figure as points, and these are seconds.</remarks>
    private IMenu BuildPlaytimeList(long period, string title) =>
        BuildBoardList("ranks.top.playtime", StatColumns.Playtime, period, title,
            e => _core.Localizer["menu.toptime.row", e.Position, e.Name, PlaytimeFormatter.Format(_core, e.Value)]);

    /// <summary>A leaderboard over one stat key. Paging is the component's job, so nothing here hand-rolls it.</summary>
    /// <remarks>
    /// The item type is the row, never <c>IMenuComponent</c>: the component renders each item
    /// through <c>Formatter</c>, whose default is <c>ToString()</c> — so a component as the item
    /// draws its own type name.
    /// </remarks>
    private IMenu BuildBoardList(string id, string statKey, long period, string title, Func<RanksLeaderboardEntry, string> row) =>
        _core.Menu.CreateMenu(id)
            .WithTitle(title)
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(10)
            .Add(new PagedCollectionComponent<RanksLeaderboardEntry>(async () =>
            {
                try
                {
                    return await _api.GetTopAsync(statKey, 50, period).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Rethrown so the component shows FailedText; caught only to get it logged.
                    _logger.LogError(ex, "Ranks: failed to build the leaderboard menu.");
                    throw;
                }
            })
            {
                Formatter = row,
                ItemHint = _ => _core.Localizer["menu.top.hint"],
                OnItemSelected = ctx =>
                {
                    var child = BuildPlayerStats(ctx.Item.SteamId, ctx.Item.Name, period);

                    // Set explicitly: adoption only happens for a menu a SubmenuComponent opens,
                    // and this one is opened from a row handler, so without it the back key would
                    // close the tree instead of returning to the leaderboard.
                    child.Parent = ctx.Session.Menu;
                    child.Open(ctx.Player);
                },
                EmptyText = _core.Localizer["menu.top.empty"],
                FailedText = _core.Localizer["menu.top.error"],
            })
            .AddFooter(new DividerComponent())
            .Build();

    /// <summary>Another player's counters, opened from a leaderboard row, for the period that board shows.</summary>
    /// <remarks>
    /// <c>ranks.top.player</c> is kept for the current period only: a renderer drawing that page
    /// loads the player's current snapshot itself, which would put this period's numbers over a
    /// past board's pick.
    /// </remarks>
    private IMenu BuildPlayerStats(ulong steamId, string name, long period) =>
        BuildSnapshotStats(
            period == _api.CurrentPeriod ? "ranks.top.player" : "ranks.top.player.period",
            _core.Localizer["menu.playerstats.title", name],
            steamId,
            period);

    /// <summary>A player's counters for one period, or all time.</summary>
    /// <remarks>
    /// Paged rather than plain text because the figures have to be fetched: a listed player is
    /// usually offline, and a past period is never resident. <c>PagedCollectionComponent</c> is what
    /// owns that load, its thread and its failure text.
    ///
    /// Module sections are deliberately absent — a module's menu factory takes the viewing
    /// <c>IPlayer</c>, so it can only ever render the viewer's own numbers (FR-CMD-04).
    /// </remarks>
    private IMenu BuildSnapshotStats(string id, string title, ulong steamId, long period) =>
        _core.Menu.CreateMenu(id)
            .WithTitle(title)
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(10)
            .Add(new PagedCollectionComponent<string>(async () =>
            {
                try
                {
                    var snapshot = await _api.LoadSnapshotAsync(steamId, period).ConfigureAwait(false);

                    return snapshot is null ? [] : StatLines(snapshot);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ranks: failed to load statistics for {SteamId} in period {Period}.", steamId, period);
                    throw;
                }
            })
            {
                EmptyText = _core.Localizer["menu.playerstats.empty"],
                FailedText = _core.Localizer["menu.top.error"],
            })
            .AddFooter(new DividerComponent())
            .Build();

    // ---- Periods -------------------------------------------------------------

    /// <summary>Every period but the current one, newest first, each opening <paramref name="build"/> for itself.</summary>
    private IMenu BuildPeriodPicker(Func<long, string, IMenu> build) =>
        _core.Menu.CreateMenu("ranks.periods")
            .WithTitle(_core.Localizer["menu.period.title"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new PagedCollectionComponent<PeriodRow>(async () =>
            {
                try
                {
                    var current = _api.CurrentPeriod;
                    var periods = await _api.GetPeriodsAsync().ConfigureAwait(false);

                    return LabelPastPeriods(periods, current);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ranks: failed to list past periods.");
                    throw;
                }
            })
            {
                Formatter = row => row.Label,
                OnItemSelected = ctx =>
                {
                    var child = build(ctx.Item.Period, ctx.Item.Label);
                    child.Parent = ctx.Session.Menu;
                    child.Open(ctx.Player);
                },
                EmptyText = _core.Localizer["menu.period.empty"],
                FailedText = _core.Localizer["menu.top.error"],
            })
            .AddFooter(new DividerComponent())
            .Build();

    /// <summary>A past period as the picker lists it, labelled once so the row and the view it opens agree.</summary>
    private sealed record PeriodRow(long Period, string Label);

    /// <remarks>
    /// A period's end is the start of the one after it, so labelling needs the whole list: only a
    /// period that ran exactly one calendar month is named after it, whatever the schedule is now.
    /// </remarks>
    private PeriodRow[] LabelPastPeriods(IReadOnlyList<long> periods, long current)
    {
        var starts = periods.Append(current).Distinct().Order().ToArray();

        return periods
            .Where(period => period != current)
            .Select(period => new PeriodRow(period, PeriodLabel(period, NextStart(starts, period))))
            .ToArray();
    }

    private static long? NextStart(long[] ascendingStarts, long period) =>
        ascendingStarts.Where(start => start > period).Select(start => (long?)start).FirstOrDefault();

    /// <remarks>Dates in invariant ISO form: the day a period starts is the same fact in every locale.</remarks>
    private string PeriodLabel(long period, long? nextStart) =>
        period == RanksPeriods.Initial
            ? _core.Localizer["menu.period.initial"]
            : nextStart is { } end && PeriodClock.IsCalendarMonth(period, end)
                ? MonthLabel(period)
                : _core.Localizer["menu.period.row", RanksPeriods.StartOf(period).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)];

    /// <summary>Names the current season under the title.</summary>
    /// <remarks>
    /// In the header because a header line never takes focus: as a body row the selection landed on
    /// it first, and pressing select there did nothing, which reads as a broken button. Left out once
    /// the season has ended, until the rollover check catches up with it.
    /// </remarks>
    private void AddSeasonLine(IMenuBuilder builder)
    {
        if (_api.CurrentPeriodEndsAt is { } endsAt && endsAt > DateTime.UtcNow)
            builder.AddHeader(new TextComponent(CurrentPeriodLine(_api.CurrentPeriod, endsAt)));
    }

    /// <remarks>
    /// The last full day rather than the boundary: every boundary falls at 00:00 UTC, so the
    /// boundary's own date reads as one more day of season than there is.
    /// </remarks>
    private string CurrentPeriodLine(long current, DateTime endsAt)
    {
        var end = new DateTimeOffset(endsAt).ToUnixTimeSeconds();

        return PeriodClock.IsCalendarMonth(current, end)
            ? _core.Localizer["menu.period.current_month", MonthLabel(current)]
            : _core.Localizer["menu.period.last_day", endsAt.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)];
    }

    /// <remarks>Month names come from the translation file (<c>month.1</c> … <c>month.12</c>), not the server's culture.</remarks>
    private string MonthLabel(long period)
    {
        var start = RanksPeriods.StartOf(period);
        return _core.Localizer["menu.period.month", _core.Localizer[$"month.{start.Month}"], start.Year];
    }

    // ---- Settings ------------------------------------------------------------

    private IMenu? BuildSettings(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not { } record) return null;

        var builder = _core.Menu.CreateMenu("ranks.settings")
            .WithTitle(_core.Localizer["menu.settings.title"])
            .AddHeader(new DividerComponent())
            .WithItemsPerPage(8)
            .Add(new ToggleComponent(
                _core.Localizer["menu.settings.XP_messages"],
                record.ShowPointMessages,
                ctx =>
                {
                    record.ShowPointMessages = ctx.NewValue;
                    record.Dirty = true;
                }));

        foreach (var (moduleId, entry) in _entries.For(RanksMenuSections.Settings))
            builder.Add(new SubmenuComponent(entry.Label, _ => _entries.Build(moduleId, entry, player)));

        // Last, and behind its own screen: the only destructive row in the tree.
        builder.Add(new SubmenuComponent(_core.Localizer["menu.settings.reset"], _ => BuildResetConfirm(player)));

        return builder.AddFooter(new DividerComponent()).Build();
    }

    private IMenu BuildResetConfirm(IPlayer player) =>
        _core.Menu.CreateMenu("ranks.settings.reset")
            .WithTitle(_core.Localizer["menu.settings.reset_title"])
            .AddHeader(new DividerComponent())
            .Add(new TextComponent(_core.Localizer["menu.settings.reset_warning"]))
            .Add(new ButtonComponent(_core.Localizer["menu.settings.reset_confirm"], ctx =>
            {
                OnGameThread(() =>
                {
                    if (_registry.BySlot(player.Slot) is { } record)
                    {
                        _reset.Apply(record);
                        Chat.Send(_core, player, "reset.done");
                    }
                });
                // Closed rather than left open: every screen behind this one still shows the old
                // numbers, and staying on a confirm button invites a second press.
                ctx.Session.Close();
                return ValueTask.CompletedTask;
            }))
            .AddFooter(new DividerComponent())
            .Build();

    // ---- Help ----------------------------------------------------------------

    /// <summary>The commands this server actually has, one line each.</summary>
    /// <remarks>
    /// Chat, not a menu: a menu row is a single centred line that clips well before one of these
    /// ends. Closing the menu is what makes the chat readable.
    ///
    /// Names come from <c>commands.jsonc</c> rather than the translation, so a renamed or removed
    /// command is described correctly instead of advertising one that does not exist.
    /// </remarks>
    private void SendHelp(IPlayer player)
    {
        Chat.Send(_core, player, "help.header");

        // Listed in reading order — the root menu first, then the shortcuts into it. Each key is
        // spelled out rather than interpolated so the translation checker can see it is reachable.
        Line(CommandsConfig.Rank, name => _core.Localizer["help.rank", name]);
        Line(CommandsConfig.Stats, name => _core.Localizer["help.stats", name]);
        Line(CommandsConfig.MyTime, name => _core.Localizer["help.mytime", name]);
        Line(CommandsConfig.Ranks, name => _core.Localizer["help.ranks", name]);
        Line(CommandsConfig.Top, name => _core.Localizer["help.top", name]);
        Line(CommandsConfig.TopTime, name => _core.Localizer["help.ttop", name]);
        Line(CommandsConfig.ResetMyRank, name => _core.Localizer["help.resetmyrank", name]);

        void Line(string configKey, Func<string, string> format)
        {
            if (!CanRun(configKey, player, out var name)) return;

            Chat.SendRaw(_core, player, format(name));
        }
    }

    /// <summary>The admin half, behind the same per-command permission the command itself carries.</summary>
    private void SendAdminHelp(IPlayer player)
    {
        Chat.Send(_core, player, "help.admin_header");

        Line(CommandsConfig.SetPoints, name => _core.Localizer["help.setpoints", name]);
        Line(CommandsConfig.GivePoints, name => _core.Localizer["help.givepoints", name]);
        Line(CommandsConfig.RemovePoints, name => _core.Localizer["help.removepoints", name]);
        Line(CommandsConfig.ResetRank, name => _core.Localizer["help.resetrank", name]);

        void Line(string configKey, Func<string, string> format)
        {
            if (!CanRun(configKey, player, out var name)) return;

            Chat.SendRaw(_core, player, format(name));
        }
    }

    private static readonly string[] AdminCommands =
    [
        CommandsConfig.SetPoints,
        CommandsConfig.GivePoints,
        CommandsConfig.RemovePoints,
        CommandsConfig.ResetRank,
    ];

    /// <summary>Whether this server has the command at all, and whether this player may run it.</summary>
    /// <remarks>
    /// The same two tests registration makes — an empty name means the operator removed the
    /// command, an empty permission means everyone — so help never advertises a command that
    /// would be refused or does not exist.
    /// </remarks>
    private bool CanRun(string configKey, IPlayer player, out string name)
    {
        name = string.Empty;

        if (!_commands.Current.All.TryGetValue(configKey, out var definition)) return false;
        if (string.IsNullOrWhiteSpace(definition.Command)) return false;

        if (!string.IsNullOrWhiteSpace(definition.Permission)
            && !_core.Permission.PlayerHasPermission(player.SteamID, definition.Permission))
            return false;

        name = definition.Command;
        return true;
    }

    /// <remarks>
    /// A menu handler is not guaranteed to run on the game thread, and chat writes and scoreboard
    /// updates are both documented thread-unsafe — calling them from a handler directly is the
    /// crash class that took a production server down through the classic menu API.
    /// </remarks>
    private void OnGameThread(Action action)
    {
        if (_core.IsGameThread) action();
        else _core.Scheduler.NextTick(action);
    }
}
