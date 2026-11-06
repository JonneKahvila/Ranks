using System.Data;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;

namespace Ranks.CustomCombos.Stats;

/// <summary>Writes combo counts additively, off-thread.</summary>
/// <remarks>
/// Additive rather than absolute for the same reason as <c>Ranks_WeaponStats</c>: this module
/// holds only the counts since the last flush, so two servers sharing a database accumulate
/// instead of clobbering, and nothing has to be read back on connect.
///
/// Every change runs through one queue, one at a time and in call order. A reset or purge queued
/// on the game thread therefore lands after every flush cut before it, so a write already under
/// way cannot bring back what it removed (FR-DB-10).
/// </remarks>
internal sealed class Writer
{
    private readonly ISwiftlyCore _core;
    private readonly ILogger _logger;
    private readonly Func<bool> _detailedLogging;
    private readonly Lock _queueGate = new();

    /// <remarks>Touched only by queued operations, which never overlap, so it needs no lock of its own.</remarks>
    private readonly List<ComboDelta> _retry = [];

    private Task _queue = Task.CompletedTask;
    private string _connectionName = "default";
    private string _table = "ranks_combos";
    private string _periodTable = "ranks_combos_period";
    private bool _mysql;

    public Writer(ISwiftlyCore core, ILogger logger, Func<bool> detailedLogging)
    {
        _core = core;
        _logger = logger;
        _detailedLogging = detailedLogging;
    }

    public bool IsReady { get; private set; }

    /// <summary>Binds the names core resolved. Until this runs the writer refuses to write.</summary>
    public void Configure(string connectionName, string table, string periodTable)
    {
        _connectionName = connectionName;
        _table = table;
        _periodTable = periodTable;
        _mysql = _core.Database.GetConnectionInfo(connectionName).Driver?.Trim().ToLowerInvariant() is "mysql" or "mariadb";
        IsReady = true;
    }

    public Task FlushAsync(List<ComboDelta> batch)
    {
        if (!IsReady || batch.Count == 0) return Task.CompletedTask;

        return Enqueue(() => WritePending(batch));
    }

    /// <summary>Clears a reset player's counts: all of them while <paramref name="period"/> is null, else that period's.</summary>
    /// <remarks>
    /// With periods on, the period's counts also come off the lifetime totals, so the past periods
    /// core keeps still add up to them (FR-DB-10).
    /// </remarks>
    public Task ResetAsync(ulong steamId, long? period)
    {
        if (!IsReady) return Task.CompletedTask;

        return Enqueue(() => Reset(steamId, period));
    }

    /// <summary>Deletes purged players' rows from both tables.</summary>
    /// <remarks>
    /// <c>PlayersPurged</c> is the only notice this module will ever get — its tables are keyed on
    /// SteamID64 with nothing joining them back to core, so nothing else sweeps them (FR-DB-08).
    /// </remarks>
    public Task PurgeAsync(IReadOnlyList<ulong> steamIds)
    {
        if (!IsReady || steamIds.Count == 0) return Task.CompletedTask;

        var ids = steamIds.ToHashSet();

        return Enqueue(() => Purge(ids));
    }

    /// <summary>Deletes rows, in both tables, for players core no longer knows about.</summary>
    /// <remarks>
    /// PlayersPurged is a live event, so a purge that happens while this module is unloaded is
    /// never swept and those rows grow without bound. Safe to depend on core's table here:
    /// FR-DB-04 fixes ranks_players as a closed, known schema, which makes it a contract rather
    /// than an implementation detail.
    /// </remarks>
    public Task ReconcileOrphansAsync(string playersTable)
    {
        if (!IsReady) return Task.CompletedTask;

        return Enqueue(() => ReconcileOrphans(playersTable));
    }

    /// <summary>Every combo a player has landed, highest first.</summary>
    public Task<List<(string ComboKey, long Count, DateTime LastAt)>> ReadAsync(ulong steamId) =>
        ReadRowsAsync(steamId, null);

    /// <summary>Every combo a player landed in one period, highest first.</summary>
    public Task<List<(string ComboKey, long Count, DateTime LastAt)>> ReadAsync(ulong steamId, long period) =>
        ReadRowsAsync(steamId, period);

    /// <remarks>Reads the lifetime table when <paramref name="period"/> is null.</remarks>
    private Task<List<(string ComboKey, long Count, DateTime LastAt)>> ReadRowsAsync(ulong steamId, long? period) =>
        Task.Run(() =>
        {
            var rows = new List<(string, long, DateTime)>();

            if (!IsReady) return rows;

            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = period is null
                ? $"SELECT combo_key, count, last_at FROM {_table} WHERE steam_id = @steam_id ORDER BY count DESC"
                : $"SELECT combo_key, count, last_at FROM {_periodTable} WHERE steam_id = @steam_id AND period = @period ORDER BY count DESC";
            Add(command, "@steam_id", (long)steamId);

            if (period is { } value) Add(command, "@period", value);

            using var reader = command.ExecuteReader();

            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2)));

            return rows;
        });

    /// <summary>Runs <paramref name="operation"/> off-thread once everything queued before it has finished.</summary>
    /// <remarks>
    /// Continues past a failed predecessor: every operation catches and logs its own failure, so
    /// the queue itself never stalls.
    /// </remarks>
    private Task Enqueue(Action operation)
    {
        lock (_queueGate)
        {
            _queue = _queue.ContinueWith(
                _ => operation(),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);

            return _queue;
        }
    }

    /// <remarks>
    /// A failed batch goes back to the retry buffer whole, because its transaction rolled every row
    /// back. Logged ungated: it is the data-loss path.
    /// </remarks>
    private void WritePending(List<ComboDelta> batch)
    {
        _retry.AddRange(batch);

        List<ComboDelta> pending = [.. _retry];
        _retry.Clear();

        try
        {
            Write(pending);
        }
        catch (Exception ex)
        {
            _retry.AddRange(pending);
            _logger.LogError(ex, "Ranks_CustomCombos: flush of {Count} row(s) failed; they are buffered for retry.", pending.Count);
        }
    }

    /// <summary>Adds every delta to the lifetime table, and to its period's row when it carries one, in one transaction.</summary>
    private void Write(List<ComboDelta> batch)
    {
        using var connection = _core.Database.GetConnection(_connectionName);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var delta in batch)
        {
            WriteOne(connection, transaction, delta);

            if (delta.Period is { } period)
                WritePeriod(connection, transaction, delta, period);
        }

        transaction.Commit();
    }

    private void WriteOne(IDbConnection connection, IDbTransaction transaction, ComboDelta delta)
    {
        const string Columns = "steam_id, combo_key, count, last_at";
        const string Values = "@steam_id, @combo_key, @count, @last_at";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // VALUES() rather than MySQL 8.0.20's "AS new" row alias: MariaDB does not support the
        // alias at all, and VALUES() works on both.
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {_table} ({Columns}) VALUES ({Values})
               ON DUPLICATE KEY UPDATE count = count + VALUES(count), last_at = VALUES(last_at)
               """
            // Qualified with the table name, or the reference is ambiguous against EXCLUDED.
            : $"""
               INSERT INTO {_table} ({Columns}) VALUES ({Values})
               ON CONFLICT (steam_id, combo_key) DO UPDATE SET
                   count = {_table}.count + EXCLUDED.count,
                   last_at = EXCLUDED.last_at
               """;

        Add(command, "@steam_id", (long)delta.SteamId);
        Add(command, "@combo_key", delta.ComboKey);
        Add(command, "@count", delta.Count);
        Add(command, "@last_at", DateTime.UtcNow);

        command.ExecuteNonQuery();
    }

    /// <summary><see cref="WriteOne"/>'s upsert against the period table, keyed on the period as well.</summary>
    private void WritePeriod(IDbConnection connection, IDbTransaction transaction, ComboDelta delta, long period)
    {
        const string Columns = "steam_id, combo_key, period, count, last_at";
        const string Values = "@steam_id, @combo_key, @period, @count, @last_at";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {_periodTable} ({Columns}) VALUES ({Values})
               ON DUPLICATE KEY UPDATE count = count + VALUES(count), last_at = VALUES(last_at)
               """
            : $"""
               INSERT INTO {_periodTable} ({Columns}) VALUES ({Values})
               ON CONFLICT (steam_id, combo_key, period) DO UPDATE SET
                   count = {_periodTable}.count + EXCLUDED.count,
                   last_at = EXCLUDED.last_at
               """;

        Add(command, "@steam_id", (long)delta.SteamId);
        Add(command, "@combo_key", delta.ComboKey);
        Add(command, "@period", period);
        Add(command, "@count", delta.Count);
        Add(command, "@last_at", DateTime.UtcNow);

        command.ExecuteNonQuery();
    }

    /// <remarks>
    /// Retry entries the reset covers are dropped first, or the next flush would write them straight
    /// back; every flush cut before the reset has already run, so all of them predate it. A failure
    /// is logged ungated: the player keeps the counts the reset was meant to clear.
    /// </remarks>
    private void Reset(ulong steamId, long? period)
    {
        _retry.RemoveAll(delta => delta.SteamId == steamId && (period is null || delta.Period == period));

        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            if (period is { } current)
                ResetPeriod(connection, transaction, steamId, current);
            else
                Execute(connection, transaction, $"DELETE FROM {_table} WHERE steam_id = @steam_id", steamId);

            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks_CustomCombos: could not clear the combo counts of reset player {SteamId}.", steamId);
        }
    }

    /// <summary>Takes one period's counts off the lifetime totals, then deletes that period's rows.</summary>
    /// <remarks>
    /// Clamped at zero: a reset made while periods were off cleared the lifetime rows but not the
    /// period rows, so a lifetime count can be the smaller. A lifetime row brought to zero is deleted,
    /// because the player has then not landed that combo outside the period just cleared.
    /// </remarks>
    private void ResetPeriod(IDbConnection connection, IDbTransaction transaction, ulong steamId, long period)
    {
        foreach (var (comboKey, count) in ReadPeriodCounts(connection, transaction, steamId, period))
            Subtract(connection, transaction, steamId, comboKey, count);

        Execute(connection, transaction, $"DELETE FROM {_table} WHERE steam_id = @steam_id AND count <= 0", steamId);
        Execute(connection, transaction, $"DELETE FROM {_periodTable} WHERE steam_id = @steam_id AND period = @period", steamId, period);
    }

    /// <remarks>Read to the end before anything else runs: MySqlConnector allows one open reader per connection.</remarks>
    private List<(string ComboKey, long Count)> ReadPeriodCounts(IDbConnection connection, IDbTransaction transaction, ulong steamId, long period)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT combo_key, count FROM {_periodTable} WHERE steam_id = @steam_id AND period = @period";
        Add(command, "@steam_id", (long)steamId);
        Add(command, "@period", period);

        var rows = new List<(string, long)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt64(1)));

        return rows;
    }

    private void Subtract(IDbConnection connection, IDbTransaction transaction, ulong steamId, string comboKey, long count)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {_table} SET count = CASE WHEN count > @count THEN count - @count ELSE 0 END WHERE steam_id = @steam_id AND combo_key = @combo_key";
        Add(command, "@count", count);
        Add(command, "@steam_id", (long)steamId);
        Add(command, "@combo_key", comboKey);

        command.ExecuteNonQuery();
    }

    /// <remarks>
    /// Retry entries for these players go first, or a retried write would recreate their rows. A
    /// failure is logged ungated.
    /// </remarks>
    private void Purge(HashSet<ulong> steamIds)
    {
        _retry.RemoveAll(delta => steamIds.Contains(delta.SteamId));

        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            foreach (var table in (ReadOnlySpan<string>)[_table, _periodTable])
            {
                var sql = $"DELETE FROM {table} WHERE steam_id = @steam_id";

                foreach (var steamId in steamIds)
                    Execute(connection, transaction, sql, steamId);
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks_CustomCombos: failed to sweep {Count} purged player(s).", steamIds.Count);
        }
    }

    /// <remarks>What it removed is expected maintenance, so the count is gated; a failure is not.</remarks>
    private void ReconcileOrphans(string playersTable)
    {
        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            var removed = 0;

            foreach (var table in (ReadOnlySpan<string>)[_table, _periodTable])
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM {table} WHERE steam_id NOT IN (SELECT steam_id FROM {playersTable})";
                removed += command.ExecuteNonQuery();
            }

            if (removed > 0 && _detailedLogging())
                _logger.LogInformation("Ranks_CustomCombos: removed {Count} orphaned row(s) left by a purge this module missed.", removed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks_CustomCombos: orphan reconcile failed.");
        }
    }

    /// <summary>Runs one statement keyed on <c>@steam_id</c>, and on <c>@period</c> when one is given.</summary>
    private static void Execute(IDbConnection connection, IDbTransaction transaction, string sql, ulong steamId, long? period = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, "@steam_id", (long)steamId);

        if (period is { } value) Add(command, "@period", value);

        command.ExecuteNonQuery();
    }

    private static void Add(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
