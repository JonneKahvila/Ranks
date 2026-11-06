using System.Data;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Ranks.EloPoints.Match;
using Ranks.EloPoints.Contract;
using SwiftlyS2.Shared;

namespace Ranks.EloPoints.Api;

/// <summary>This module's published read API.</summary>
/// <remarks>
/// Names its contract without JIT isolation, unlike <see cref="RanksBridge"/>: a provider
/// hard-requires the assembly it publishes from, exactly as core does with Ranks.Contract.
///
/// No session merge is needed here, unlike WeaponStats — a rating is written once, at match end,
/// so the stored value is never behind. What a match in progress would contribute is deliberately
/// not exposed: it has no result until it ends.
///
/// Core's current period comes through the bridge, which answers null while core is unavailable;
/// every query then answers empty, as it does before core is ready.
/// </remarks>
internal sealed class EloPointsApi : IEloPointsApi
{
    private const string Columns = "steam_id, rating, matches, abandons, updated_at";

    private readonly ISwiftlyCore _core;
    private readonly MatchTracker _tracker;
    private readonly RanksBridge _bridge;
    private readonly ILogger _logger;

    private string _connectionName = "default";
    private string _table = "ranks_elopoints";
    private string _history = "ranks_elopoints_history";
    private bool _ready;

    public EloPointsApi(ISwiftlyCore core, MatchTracker tracker, RanksBridge bridge, ILogger logger)
    {
        _core = core;
        _tracker = tracker;
        _bridge = bridge;
        _logger = logger;
    }

    /// <summary>Binds the names core resolved. Until this runs every query answers empty.</summary>
    public void Configure(string connectionName, string table, string history)
    {
        _connectionName = connectionName;
        _table = table;
        _history = history;
        _ready = true;
    }

    public Task<EloStanding?> GetStandingAsync(ulong steamId) =>
        _bridge.CurrentPeriod() is { } period ? GetStandingAsync(steamId, period) : Task.FromResult<EloStanding?>(null);

    /// <remarks>
    /// A closed period prefers its history row, and falls back to a rating row still holding that
    /// period — one whose player has not been touched since the rollover, so it has not moved yet.
    /// </remarks>
    public Task<EloStanding?> GetStandingAsync(ulong steamId, long period)
    {
        if (!_ready || period == IEloPointsApi.AllTime || _bridge.CurrentPeriod() is not { } current)
            return Task.FromResult<EloStanding?>(null);

        var closed = period != current;

        return Task.Run<EloStanding?>(() =>
        {
            try
            {
                using var connection = _core.Database.GetConnection(_connectionName);
                connection.Open();

                var archived = closed ? ReadStanding(connection, _history, steamId, period) : null;

                return archived ?? ReadStanding(connection, _table, steamId, period);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_EloPoints: could not read the standing for {SteamId}.", steamId);
                return null;
            }
        });
    }

    public Task<IReadOnlyList<EloStanding>> GetTopAsync(int limit = 10) =>
        _bridge.CurrentPeriod() is { } period ? GetTopAsync(limit, period) : Task.FromResult<IReadOnlyList<EloStanding>>([]);

    public Task<IReadOnlyList<EloStanding>> GetTopAsync(int limit, long period)
    {
        if (!_ready || period == IEloPointsApi.AllTime || _bridge.CurrentPeriod() is not { } current)
            return Task.FromResult<IReadOnlyList<EloStanding>>([]);

        var clamped = Math.Clamp(limit, 1, 100);
        var sql = TopSql(closed: period != current);

        return Task.Run<IReadOnlyList<EloStanding>>(() =>
        {
            try
            {
                using var connection = _core.Database.GetConnection(_connectionName);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = sql;
                Add(command, "@period", period);
                Add(command, "@limit", clamped);

                var rows = new List<EloStanding>(clamped);

                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add(ReadRanked(reader));

                return rows;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks_EloPoints: could not read the rating leaderboard.");
                return [];
            }
        });
    }

    public bool IsInRatedMatch(ulong steamId) => _tracker.HasOpenSession(steamId);

    /// <remarks>
    /// The current period reads the rating table alone, so a player who has not finished a match
    /// since the rollover is not on it. A closed one also reads rating rows still on that period,
    /// which move to history only when their player is next touched (FR-DB-10).
    /// </remarks>
    private string TopSql(bool closed) => closed
        ? $"""
           SELECT {Columns} FROM (
               SELECT {Columns} FROM {_history} WHERE period = @period
               UNION ALL
               SELECT {Columns} FROM {_table} WHERE period = @period
           ) r
           ORDER BY rating DESC
           LIMIT @limit
           """
        : $"SELECT {Columns} FROM {_table} WHERE period = @period ORDER BY rating DESC LIMIT @limit";

    private static EloStanding? ReadStanding(IDbConnection connection, string table, ulong steamId, long period)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT rating, matches, abandons, updated_at FROM {table} WHERE steam_id = @steam_id AND period = @period";
        Add(command, "@steam_id", (long)steamId);
        Add(command, "@period", period);

        using var reader = command.ExecuteReader();

        return reader.Read() ? Read(steamId, reader) : null;
    }

    private static EloStanding Read(ulong steamId, IDataReader reader) => new(
        steamId,
        Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture),
        reader.GetInt32(1),
        reader.GetInt32(2),
        reader.GetDateTime(3));

    private static EloStanding ReadRanked(IDataReader reader) => new(
        (ulong)reader.GetInt64(0),
        Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
        reader.GetInt32(2),
        reader.GetInt32(3),
        reader.GetDateTime(4));

    private static void Add(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
