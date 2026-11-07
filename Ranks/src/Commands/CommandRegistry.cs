using Microsoft.Extensions.Logging;
using Ranks.Api;
using Ranks.Config;
using Ranks.Database;
using Ranks.Menus;
using Ranks.Players;
using Ranks.Ranking;
using Ranks.Scoreboard;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace Ranks.Commands;

/// <summary>Registers and handles core's commands.</summary>
/// <remarks>
/// Registered at runtime rather than by <c>[Command]</c>: attribute arguments are compile-time
/// constants, and FR-CMD-01 makes the name, permission and whether it registers at all
/// operator-editable. An attribute also cannot express "an empty Command string means do not
/// register".
/// </remarks>
internal sealed class CommandRegistry
{
    private readonly ISwiftlyCore _core;
    private readonly ConfigHost<CommandsConfig> _commands;
    private readonly RankMenus _menus;
    private readonly PlayerRegistry _registry;
    private readonly PlayerReset _reset;
    private readonly RanksApi _api;
    private readonly ILogger _logger;

    private readonly List<Guid> _registered = [];

    public CommandRegistry(
        ISwiftlyCore core,
        ConfigHost<CommandsConfig> commands,
        RankMenus menus,
        PlayerRegistry registry,
        PlayerReset reset,
        RanksApi api,
        ILogger logger)
    {
        _core = core;
        _commands = commands;
        _menus = menus;
        _registry = registry;
        _reset = reset;
        _api = api;
        _logger = logger;
    }

    public void RegisterAll()
    {
        foreach (var (key, definition) in _commands.Current.All)
        {
            if (string.IsNullOrWhiteSpace(definition.Command)) continue;

            var handler = HandlerFor(key);
            if (handler is null) continue;

            try
            {
                // registerRaw stays false: the framework owns the sw_ console prefix and the
                // !rank / /rank chat triggers alike. Registering raw takes the bare console name
                // for ourselves, which collides with anything else that wants it and bypasses the
                // convention every other SwiftlyS2 plugin follows.
                _registered.Add(_core.Command.RegisterCommand(
                    definition.Command, handler, registerRaw: false,
                    definition.Permission, _core.Localizer[$"command.{key}.help"]));

                foreach (var alias in definition.Aliases)
                    _core.Command.RegisterCommandAlias(definition.Command, alias, registerRaw: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: could not register command '{Command}'.", definition.Command);
            }
        }
    }

    /// <remarks>
    /// Plugins auto-unregister on unload, so this exists for a config reload that renames a
    /// command — the only way to drop the old name without a full restart.
    /// </remarks>
    public void UnregisterAll()
    {
        foreach (var id in _registered)
        {
            try
            {
                _core.Command.UnregisterCommand(id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ranks: could not unregister a command.");
            }
        }

        _registered.Clear();
    }

    private ICommandService.CommandListener? HandlerFor(string key) => key switch
    {
        // Each player command is a shortcut into one branch of the tree the root menu shows, bar
        // !mytime, which answers in chat as K4-LevelRanks did; preferences and help have no
        // command at all, by design (FR-CMD-02).
        CommandsConfig.Rank => ctx => ForPlayer(ctx, _menus.OpenRoot),
        CommandsConfig.Ranks => ctx => ForPlayer(ctx, _menus.OpenLadder),
        CommandsConfig.Stats => ctx => ForPlayer(ctx, _menus.OpenStatistics),
        CommandsConfig.Top => ctx => ForPlayer(ctx, _menus.OpenTop),
        CommandsConfig.MyTime => ctx => ForPlayer(ctx, ShowPlaytime),
        CommandsConfig.TopTime => ctx => ForPlayer(ctx, ShowPlaytimeTop),
        CommandsConfig.ResetMyRank => ctx => ForPlayer(ctx, ResetSelf),
        CommandsConfig.SetPoints => ctx => AdjustPoints(ctx, AdjustMode.Set),
        CommandsConfig.GivePoints => ctx => AdjustPoints(ctx, AdjustMode.Add),
        CommandsConfig.RemovePoints => ctx => AdjustPoints(ctx, AdjustMode.Subtract),
        CommandsConfig.ResetRank => ResetTarget,
        _ => null,
    };

    /// <remarks>
    /// Sender is null from the server console, so IsSentByPlayer has to be checked first rather
    /// than relying on a null check that the compiler cannot see through.
    /// </remarks>
    private void ForPlayer(ICommandContext ctx, Action<IPlayer> action)
    {
        if (!ctx.IsSentByPlayer || ctx.Sender is not { } player)
        {
            ctx.Reply(_core.Localizer["error.players_only"]);
            return;
        }

        action(player);
    }

    /// <summary>The player's playtime in chat, as K4-LevelRanks' <c>!mytime</c> answered.</summary>
    /// <remarks>The current period's figure, like every default view; all time is under Statistics and on the playtime board.</remarks>
    private void ShowPlaytime(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not { } record)
        {
            Chat.Send(_core, player, "error.not_loaded");
            return;
        }

        var key = _api.ArePeriodsEnabled ? "playtime.mine_season" : "playtime.mine";
        Chat.Send(_core, player, key, PlaytimeFormatter.Format(_core, record.LivePlaytime));
    }

    /// <summary>Opens the playtime board and reports the player's place on it in chat, as K4-LevelRanks' <c>!ttop</c> did.</summary>
    private void ShowPlaytimeTop(IPlayer player)
    {
        _menus.OpenPlaytimeTop(player);

        var key = _api.ArePeriodsEnabled ? "playtime.position_season" : "playtime.position";
        _ = AnnouncePlaytimePositionAsync(player.SteamID, key);
    }

    /// <remarks>
    /// Silent while the player holds no stored figures — a record not yet saved has nothing to rank —
    /// as K4-LevelRanks was. Both counts are the current period's, so they describe one board. The
    /// API calls catch their own failures and answer 0, so this task never faults.
    /// </remarks>
    private async Task AnnouncePlaytimePositionAsync(ulong steamId, string key)
    {
        var position = await _api.GetPositionAsync(steamId, StatColumns.Playtime).ConfigureAwait(false);
        var total = await _api.GetTotalRankedAsync().ConfigureAwait(false);

        if (position <= 0 || total <= 0) return;

        _core.Scheduler.NextTick(() => SendIfConnected(steamId, key, position, total));
    }

    /// <summary>Chat to a player found again by SteamID, since they may have left while the query ran.</summary>
    /// <remarks>Game thread only: resolving a player is a native read.</remarks>
    private void SendIfConnected(ulong steamId, string key, params object[] args)
    {
        if (_core.PlayerManager.GetPlayerFromSteamId(steamId, allowUnauthorized: false) is { } player)
            Chat.Send(_core, player, key, args);
    }


    /// <remarks>
    /// Two-step by design: the first call arms, the second within the window commits. A menu
    /// confirmation would be nicer, but this command is reachable from console where no menu is.
    /// </remarks>
    private void ResetSelf(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not { } record)
        {
            Chat.Send(_core, player, "error.not_loaded");
            return;
        }

        var now = DateTime.UtcNow;

        if (_pendingResets.TryGetValue(record.SteamId, out var armedAt) && now - armedAt < ResetConfirmWindow)
        {
            _pendingResets.Remove(record.SteamId);
            _reset.Apply(record);
            Chat.Send(_core, player, "reset.done");
            return;
        }

        _pendingResets[record.SteamId] = now;
        Chat.Send(_core, player, "reset.confirm", (int)ResetConfirmWindow.TotalSeconds);
    }

    private static readonly TimeSpan ResetConfirmWindow = TimeSpan.FromSeconds(15);

    private readonly Dictionary<ulong, DateTime> _pendingResets = [];

    /// <remarks>
    /// <c>Args</c> excludes the command name. The amount is the last argument and the target is
    /// everything before it, so an unquoted name containing a space still resolves.
    /// </remarks>
    private void AdjustPoints(ICommandContext ctx, AdjustMode mode)
    {
        if (ctx.Args.Length < 2)
        {
            ctx.Reply(_core.Localizer["admin.usage", ctx.CommandName]);
            return;
        }

        var amountArg = ctx.Args[^1];

        if (!int.TryParse(amountArg, out var amount))
        {
            ctx.Reply(_core.Localizer["admin.bad_amount", amountArg]);
            return;
        }

        var reason = $"admin.{mode.ToString().ToLowerInvariant()}";

        foreach (var record in TargetRecords(ctx, ctx.Args[..^1]))
        {
            if (mode == AdjustMode.Set) _api.SetPoints(record.SteamId, amount, reason);
            else _api.AddPoints(record.SteamId, mode == AdjustMode.Add ? amount : -amount, reason, showMessage: false);

            ctx.Reply(_core.Localizer["admin.adjusted", record.Name, record.Points]);
        }
    }

    private void ResetTarget(ICommandContext ctx)
    {
        if (ctx.Args.Length == 0)
        {
            ctx.Reply(_core.Localizer["admin.usage_target", ctx.CommandName]);
            return;
        }

        foreach (var record in TargetRecords(ctx, ctx.Args))
        {
            _reset.Apply(record);
            ctx.Reply(_core.Localizer["admin.reset", record.Name]);
        }
    }

    /// <summary>The loaded records the target arguments name, after replying for anything unusable.</summary>
    private List<PlayerRecord> TargetRecords(ICommandContext ctx, string[] targetArgs)
    {
        var pattern = string.Join(' ', targetArgs);
        var match = TargetResolver.Resolve(_core, ctx.Sender, pattern);

        if (match.Outcome != TargetResolver.Outcome.Found)
        {
            var key = match.Outcome == TargetResolver.Outcome.Ambiguous ? "admin.ambiguous_target" : "admin.no_target";
            ctx.Reply(_core.Localizer[key, pattern]);
            return [];
        }

        var records = new List<PlayerRecord>(match.Players.Count);

        foreach (var player in match.Players)
        {
            if (_registry.BySlot(player.Slot) is { } record) records.Add(record);
            else ctx.Reply(_core.Localizer["admin.not_loaded", player.Name]);
        }

        return records;
    }

    private enum AdjustMode
    {
        Set,
        Add,
        Subtract,
    }
}
