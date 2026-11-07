using System.Reflection;
using Microsoft.Extensions.Logging;
using Ranks.Config;
using Ranks.Contract;
using Ranks.Database;
using Ranks.Menus;
using Ranks.Players;
using Ranks.Ranking;
using Ranks.Scoreboard;
using Ranks.Scoring;
using SwiftlyS2.Shared.Players;

namespace Ranks.Api;

/// <summary>Core's implementation of <c>Ranks.Api.v1</c>.</summary>
/// <remarks>
/// A thin facade: every member delegates to the service that owns the behaviour, so there is no
/// second copy of a rule here. Rebuilt on reconfiguration — the event hub it forwards to is
/// cleared with it, or handlers accumulate across the handshake (FR-API-04).
/// </remarks>
internal sealed class RanksApi : IRanksApi
{
    private readonly ConfigHost<RanksConfig> _config;
    private readonly ConfigHost<PointsConfig> _points;
    private readonly PlayerRegistry _registry;
    private readonly PlayerSessions _sessions;
    private readonly AwardPipeline _awards;
    private readonly RankLadder _ladder;
    private readonly ScoreboardService _scoreboard;
    private readonly PlayerStore _store;
    private readonly SchemaMigrator _migrator;
    private readonly MenuEntryRegistry _menuEntries;
    private readonly TableNames _tables;
    private readonly DatabaseConnection _database;
    private readonly RanksEventHub _events;
    private readonly ILogger _logger;

    public RanksApi(
        ConfigHost<RanksConfig> config,
        ConfigHost<PointsConfig> points,
        PlayerRegistry registry,
        PlayerSessions sessions,
        AwardPipeline awards,
        RankLadder ladder,
        ScoreboardService scoreboard,
        PlayerStore store,
        SchemaMigrator migrator,
        MenuEntryRegistry menuEntries,
        TableNames tables,
        DatabaseConnection database,
        RanksEventHub events,
        ILogger logger)
    {
        _config = config;
        _points = points;
        _registry = registry;
        _sessions = sessions;
        _awards = awards;
        _ladder = ladder;
        _scoreboard = scoreboard;
        _store = store;
        _migrator = migrator;
        _menuEntries = menuEntries;
        _tables = tables;
        _database = database;
        _events = events;
        _logger = logger;
    }

    public bool IsCoreReady { get; private set; }

    /// <remarks>Fires once per load cycle, after config is bound and the schema probe has run —
    /// whether or not the probe passed, because a module needs to know core settled either way.</remarks>
    public void MarkCoreReady()
    {
        if (IsCoreReady) return;

        IsCoreReady = true;
        _events.RaiseCoreReady();
    }

    public event Action CoreReady
    {
        add => _events.CoreReady += value;
        remove => _events.CoreReady -= value;
    }

    public event Action<RanksPlayerSnapshot> PlayerDataLoaded
    {
        add => _events.PlayerDataLoaded += value;
        remove => _events.PlayerDataLoaded -= value;
    }

    public event Action<RanksPointsChangingArgs> PointsChanging
    {
        add => _events.PointsChanging += value;
        remove => _events.PointsChanging -= value;
    }

    public event Action<RanksPointsChangedArgs> PointsChanged
    {
        add => _events.PointsChanged += value;
        remove => _events.PointsChanged -= value;
    }

    public event Action<RanksRankChangedArgs> RankChanged
    {
        add => _events.RankChanged += value;
        remove => _events.RankChanged -= value;
    }

    public event Action<RanksPlayerSnapshot> PlayerDataSaved
    {
        add => _events.PlayerDataSaved += value;
        remove => _events.PlayerDataSaved -= value;
    }

    public event Action<IReadOnlyList<ulong>> PlayersPurged
    {
        add => _events.PlayersPurged += value;
        remove => _events.PlayersPurged -= value;
    }

    public event Action MaintenanceDue
    {
        add => _events.MaintenanceDue += value;
        remove => _events.MaintenanceDue -= value;
    }

    public bool IsLoaded(ulong steamId) => _registry.IsTracked(steamId);

    public bool IsLoaded(IPlayer player) => IsLoaded(player.SteamID);

    public int GetPoints(ulong steamId) => _registry.BySteamId(steamId)?.Points ?? 0;

    public int GetPoints(IPlayer player) => GetPoints(player.SteamID);

    public void SetPoints(ulong steamId, int points, string reason)
    {
        if (_registry.BySteamId(steamId) is not { } record) return;

        _awards.SetPoints(record, points, reason);
        _scoreboard.Apply(steamId);
    }

    public void SetPoints(IPlayer player, int points, string reason) => SetPoints(player.SteamID, points, reason);

    public void AddPoints(ulong steamId, int amount, string reason, bool showMessage = true)
    {
        if (_registry.BySteamId(steamId) is not { } record) return;

        _awards.AddPoints(record, amount, reason, showMessage);
        _scoreboard.Apply(steamId);
    }

    public void AddPoints(IPlayer player, int amount, string reason, bool showMessage = true) =>
        AddPoints(player.SteamID, amount, reason, showMessage);

    public RanksPlayerSnapshot? GetSnapshot(ulong steamId) =>
        _registry.BySteamId(steamId) is { } record ? _sessions.Snapshot(record) : null;

    public RanksPlayerSnapshot? GetSnapshot(IPlayer player) => GetSnapshot(player.SteamID);

    public string? GetDesiredClanTag(ulong steamId) => _scoreboard.DesiredClanTag(steamId);

    public long GetStat(ulong steamId, string statKey) =>
        _registry.BySteamId(steamId) is { } record ? StatColumns.Read(record, statKey) : 0;

    public async Task<IReadOnlyList<RanksLeaderboardEntry>> GetTopAsync(string statKey = "points", int limit = 10) =>
        await GetTopAsync(statKey, limit, _sessions.ActivePeriod).ConfigureAwait(false);

    public async Task<IReadOnlyList<RanksLeaderboardEntry>> GetTopAsync(string statKey, int limit, long period)
    {
        if (!StatColumns.TryResolve(statKey, out var column))
        {
            _logger.LogWarning("Ranks: GetTopAsync was asked for unknown stat '{StatKey}'.", statKey);
            return [];
        }

        var bounded = Math.Clamp(limit, 1, 100);
        var current = _sessions.ActivePeriod;
        var startPoints = _config.Current.Rank.StartPoints;

        var rows = await Task.Run(() =>
        {
            try
            {
                return _store.Top(column, bounded, period, current, startPoints);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: leaderboard query failed for {StatKey} in period {Period}.", statKey, period);
                return [];
            }
        }).ConfigureAwait(false);

        var entries = new List<RanksLeaderboardEntry>(rows.Count);
        var closed = period != current && period != RanksPeriods.AllTime;

        for (var i = 0; i < rows.Count; i++)
        {
            var (steamId, name, value, points) = rows[i];
            var tier = _ladder.TierFor(StandingFor(steamId, points, closed));

            entries.Add(new RanksLeaderboardEntry(i + 1, steamId, name, value, tier.Name, tier.Tag));
        }

        return entries;
    }

    /// <summary>The points a leaderboard row's tier is resolved from.</summary>
    /// <remarks>
    /// The tier is a property of the player, not of the leaderboard they appear on, so it is always
    /// resolved from points even when the sort is on another column. A resident record wins over
    /// the stored value because it may hold unsaved points — except on a closed period, whose row
    /// already holds the tier the player finished it on.
    /// </remarks>
    private int StandingFor(ulong steamId, int storedPoints, bool closedPeriod) =>
        closedPeriod ? storedPoints : _registry.BySteamId(steamId)?.Points ?? storedPoints;

    public Task<int> GetPositionAsync(ulong steamId, string statKey = "points") =>
        GetPositionAsync(steamId, statKey, _sessions.ActivePeriod);

    public Task<int> GetPositionAsync(ulong steamId, string statKey, long period)
    {
        if (!StatColumns.TryResolve(statKey, out var column)) return Task.FromResult(0);

        var current = _sessions.ActivePeriod;
        var startPoints = _config.Current.Rank.StartPoints;

        return Task.Run(() =>
        {
            try
            {
                return _store.Position(steamId, column, period, current, startPoints);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: position query failed for {SteamId} in period {Period}.", steamId, period);
                return 0;
            }
        });
    }

    public Task<int> GetTotalRankedAsync()
    {
        var current = _sessions.ActivePeriod;

        return Task.Run(() =>
        {
            try
            {
                return _store.TotalRanked(current, current);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: total-ranked query failed.");
                return 0;
            }
        });
    }

    public void SetKillArbiter(IRanksKillArbiter? arbiter) => _awards.SetKillArbiter(arbiter);

    public bool ArePointsEnabled => _awards.ArePointsEnabled;

    public bool IsPlayCounted => _awards.IsPlayCounted;

    public RanksPointSettings GetPointSettings()
    {
        var config = _config.Current;

        return new RanksPointSettings(
            config.Rank.UseCoreRankingLogic,
            config.Rank.MinPlayers,
            config.Rank.WarmupPoints,
            config.Rank.BotsEnabled,
            config.Rank.FfaMode,
            config.Vip.Multiplier,
            config.Vip.Flags.ToArray(),
            config.Points.DynamicKillPoints,
            config.Points.DynamicKillMin,
            config.Points.DynamicKillMax,
            config.Points.DynamicDeathPoints,
            config.Points.DynamicDeathMin,
            config.Points.DynamicDeathMax,
            config.Rank.StartPoints,
            config.Rank.MinPoints,
            new Dictionary<string, int>(_points.Current.Values, StringComparer.OrdinalIgnoreCase));
    }

    public string ConnectionName => _database.Name;

    public string ResolveTableName(string baseName) => _tables.Resolve(baseName);

    public void RegisterMigrations(Assembly assembly, string pluginId) => _migrator.Register(assembly, pluginId);

    public void RegisterMenuEntry(string moduleId, RanksMenuEntry entry) => _menuEntries.Add(moduleId, entry);

    public void UnregisterMenuEntries(string moduleId) => _menuEntries.RemoveAll(moduleId);

    public void SetDisplayOverride(IPlayer player, RanksDisplayOverride overlay)
    {
        if (_registry.BySlot(player.Slot) is not { } record) return;

        record.DisplayOverride = overlay;
        _scoreboard.Apply(player);
    }

    public void ClearDisplayOverride(IPlayer player)
    {
        if (_registry.BySlot(player.Slot) is not { } record) return;

        record.DisplayOverride = null;
        _scoreboard.Apply(player);
    }

    public RanksDisplayOverride? GetDisplayOverride(IPlayer player) => _registry.BySlot(player.Slot)?.DisplayOverride;

    public Task<bool> SavePlayerAsync(ulong steamId) => _sessions.SavePlayerAsync(steamId);

    public Task<int> SaveAllAsync() => _sessions.SaveAllAsync();

    public IReadOnlyList<RanksTier> GetLadder() =>
        _ladder.Tiers.Select((tier, index) => new RanksTier(index, tier.Name, tier.Tag, tier.Points, tier.Hex)).ToArray();

    public RanksRankIcon GetRankIcon(int points)
    {
        var config = _config.Current;
        var mode = config.Scoreboard.RankMode;

        return new RanksRankIcon(mode.ToString(), mode == RankDisplayMode.Off ? 0 : _scoreboard.IconValue(config, mode, points));
    }

    public async Task<RanksPlayerSnapshot?> LoadSnapshotAsync(ulong steamId)
    {
        // Resident first: the stored row lags whatever the player earned since the last save.
        if (_registry.BySteamId(steamId) is { } resident) return _sessions.Snapshot(resident);

        var current = _sessions.ActivePeriod;
        var startPoints = _config.Current.Rank.StartPoints;

        return await Task.Run(() =>
        {
            try
            {
                if (_store.ReadCurrent(steamId, current, startPoints) is not { } stored) return null;

                stored.RankIndex = _ladder.IndexFor(stored.Points);
                return _sessions.Snapshot(stored);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: snapshot load failed for {SteamId}.", steamId);
                return null;
            }
        }).ConfigureAwait(false);
    }

    public async Task<RanksPlayerSnapshot?> LoadSnapshotAsync(ulong steamId, long period)
    {
        if (period == _sessions.ActivePeriod) return await LoadSnapshotAsync(steamId).ConfigureAwait(false);
        if (period == RanksPeriods.AllTime) return await LoadAllTimeSnapshotAsync(steamId).ConfigureAwait(false);

        return await Task.Run(() =>
        {
            try
            {
                if (_store.ReadPeriod(steamId, period) is not { } stored) return null;

                stored.RankIndex = _ladder.IndexFor(stored.Points);
                return _sessions.Snapshot(stored);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: snapshot load failed for {SteamId} in period {Period}.", steamId, period);
                return null;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Every closed period summed, plus the period the player is in now.</summary>
    /// <remarks>
    /// The resident snapshot is taken here, on the caller's thread, because the record belongs to
    /// the game thread; the stored row stands in only for a player who is not resident.
    /// </remarks>
    private async Task<RanksPlayerSnapshot?> LoadAllTimeSnapshotAsync(ulong steamId)
    {
        var resident = _registry.BySteamId(steamId) is { } record ? _sessions.Snapshot(record) : null;
        var startPoints = _config.Current.Rank.StartPoints;

        return await Task.Run(() =>
        {
            try
            {
                var latest = resident ?? (_store.ReadStored(steamId) is { } stored ? _sessions.Snapshot(stored) : null);
                if (latest is null) return null;

                return AllTime(latest, _store.ReadHistoryTotals(steamId), startPoints);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: all-time snapshot load failed for {SteamId}.", steamId);
                return null;
            }
        }).ConfigureAwait(false);
    }

    /// <remarks>
    /// Points count each period's gain over <c>Rank.StartPoints</c> and add the start back once, so
    /// a player in a single period reads exactly their points.
    /// </remarks>
    private RanksPlayerSnapshot AllTime(RanksPlayerSnapshot latest, PlayerTotals history, int startPoints)
    {
        var points = (int)Math.Clamp(latest.Points + history.Points - history.Periods * startPoints, int.MinValue, int.MaxValue);
        var index = _ladder.IndexFor(points);
        var tier = _ladder.Tiers[Math.Clamp(index, 0, _ladder.Tiers.Count - 1)];

        return new RanksPlayerSnapshot(
            latest.SteamId, latest.Name, points, tier.Name, tier.Tag, index,
            latest.Kills + history.Kills, latest.Deaths + history.Deaths, latest.Assists + history.Assists,
            latest.RoundWins + history.RoundWins, latest.RoundLosses + history.RoundLosses,
            latest.MatchWins + history.MatchWins, latest.MatchLosses + history.MatchLosses,
            latest.BombsPlanted + history.BombsPlanted, latest.BombsDefused + history.BombsDefused,
            latest.FirstSeen, latest.LastSeen)
        {
            Headshots = latest.Headshots + history.Headshots,
            Hits = latest.Hits + history.Hits,
            Misses = latest.Misses + history.Misses,
            Playtime = latest.Playtime + history.Playtime,
            Period = RanksPeriods.AllTime,
        };
    }

    public long CurrentPeriod => _sessions.ActivePeriod;

    public bool ArePeriodsEnabled => _sessions.PeriodsEnabled;

    public DateTime? CurrentPeriodEndsAt => _sessions.ActivePeriodEndsAt;

    public event Action<RanksPeriodChangedArgs> PeriodChanged
    {
        add => _events.PeriodChanged += value;
        remove => _events.PeriodChanged -= value;
    }

    public event Action<RanksPlayerResetArgs> PlayerReset
    {
        add => _events.PlayerReset += value;
        remove => _events.PlayerReset -= value;
    }

    public async Task<IReadOnlyList<long>> GetPeriodsAsync()
    {
        var current = _sessions.ActivePeriod;

        var stored = await Task.Run(() =>
        {
            try
            {
                return _store.Periods();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: period list query failed.");
                return [];
            }
        }).ConfigureAwait(false);

        return stored.Append(current).Distinct().OrderDescending().ToArray();
    }
}
