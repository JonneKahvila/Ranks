using System.Data;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using HitGroupRow = (short HitGroup, long Hits, long Damage);
using WeaponHitGroupRow = (string Weapon, short HitGroup, long Hits, long Damage);
using WeaponRow = (string Weapon, long Shots, long Hits, long Misses, long Damage, long Kills, long Deaths, long Headshots);

namespace Ranks.WeaponStats.Stats;

/// <summary>Writes accumulated deltas additively. Off-thread, never touches a live session.</summary>
/// <remarks>
/// Core writes <em>absolute</em> values because its in-memory record is authoritative. This module
/// holds only session deltas, so additive is the right shape rather than a shortcut: there is no
/// read on connect, and two servers sharing a database accumulate instead of clobbering.
///
/// A delta always lands in the all-time tables and, when it carries a period, in that period's rows
/// too, in the same transaction (FR-DB-10). The all-time tables therefore keep meaning exactly what
/// they always meant, whether or not core's periods are on.
/// </remarks>
internal sealed class Writer
{
    private readonly ISwiftlyCore _core;
    private readonly ILogger _logger;
    private readonly Lock _queueGate = new();

    /// <remarks>Touched only by queued operations, which never overlap, so it needs no lock of its own.</remarks>
    private readonly List<StatsDelta> _retry = [];

    private Task _queue = Task.CompletedTask;

    private string _connectionName = "default";
    private string _weaponTable = "ranks_weapon_stats";
    private string _hitTable = "ranks_hit_stats";
    private string _weaponHitTable = "ranks_weapon_hit_stats";
    private string _weaponPeriodTable = "ranks_weapon_stats_period";
    private string _hitPeriodTable = "ranks_hit_stats_period";
    private string _weaponHitPeriodTable = "ranks_weapon_hit_stats_period";
    private bool _mysql;

    public Writer(ISwiftlyCore core, ILogger logger)
    {
        _core = core;
        _logger = logger;
    }

    public bool IsReady { get; private set; }

    /// <summary>Binds the names core resolved. Until this runs the writer refuses to write.</summary>
    public void Configure(
        string connectionName,
        string weaponTable,
        string hitTable,
        string weaponHitTable,
        string weaponPeriodTable,
        string hitPeriodTable,
        string weaponHitPeriodTable)
    {
        _connectionName = connectionName;
        _weaponTable = weaponTable;
        _hitTable = hitTable;
        _weaponHitTable = weaponHitTable;
        _weaponPeriodTable = weaponPeriodTable;
        _hitPeriodTable = hitPeriodTable;
        _weaponHitPeriodTable = weaponHitPeriodTable;
        _mysql = _core.Database.GetConnectionInfo(connectionName).Driver?.Trim().ToLowerInvariant() is "mysql" or "mariadb";
        IsReady = true;
    }

    /// <summary>Queues a batch for writing, behind everything queued before it.</summary>
    public Task FlushAsync(List<StatsDelta> batch)
    {
        if (!IsReady || batch.Count == 0) return Task.CompletedTask;

        return Enqueue(() => WritePending(batch));
    }

    /// <summary>Clears what a core reset cleared: one period's rows, or all time while periods are off.</summary>
    /// <remarks>
    /// <paramref name="period"/> is the period core wiped, or null while periods are off. Queued
    /// behind every write handed over before it, so a batch cut before the reset has either landed
    /// already or still sits in the retry buffer, where it is dropped — a write in flight can never
    /// put the old figures back (FR-DB-10).
    /// </remarks>
    public Task ResetAsync(ulong steamId, long? period)
    {
        if (!IsReady) return Task.CompletedTask;

        return Enqueue(() => Reset(steamId, period));
    }

    /// <summary>Deletes every row — all time and every period — for purged players.</summary>
    /// <remarks>
    /// <c>PlayersPurged</c> is the only notice this module will ever get — module tables are keyed
    /// on SteamID64 with nothing joining them back to core, so nothing else sweeps them. A purge
    /// takes past periods too, exactly as it does in core (FR-DB-08).
    /// </remarks>
    public Task PurgeAsync(IReadOnlyList<ulong> steamIds)
    {
        if (!IsReady || steamIds.Count == 0) return Task.CompletedTask;

        var ids = steamIds.ToArray();

        return Enqueue(() => Purge(ids));
    }

    /// <summary>Deletes rows, all time and per period, for players core no longer knows about.</summary>
    /// <remarks>
    /// PlayersPurged is a live event, so a purge that happens while this module is unloaded is
    /// never swept and those rows grow without bound. Safe to depend on core's table here:
    /// FR-DB-04 fixes ranks_players as a closed, known schema, which makes it a contract rather
    /// than an implementation detail. Every player core keeps has a row there, past periods or not.
    /// </remarks>
    public Task ReconcileOrphansAsync(string playersTable)
    {
        if (!IsReady) return Task.CompletedTask;

        return Enqueue(() => ReconcileOrphans(playersTable));
    }

    /// <summary>Every stored weapon row for a player, all time when <paramref name="period"/> is null. Off-thread.</summary>
    /// <remarks>
    /// Returns what was last flushed, so the caller must add the live session delta — see
    /// <c>Collector.PeekFor</c>. Unfiltered and unordered.
    /// </remarks>
    public Task<List<WeaponRow>> ReadWeaponsAsync(ulong steamId, long? period)
    {
        if (!IsReady) return Task.FromResult(new List<WeaponRow>());

        return Task.Run(() =>
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            return SelectWeapons(connection, transaction: null, steamId, period);
        });
    }

    /// <summary>Every stored hitgroup row for a player, all time when <paramref name="period"/> is null. Off-thread.</summary>
    public Task<List<HitGroupRow>> ReadHitGroupsAsync(ulong steamId, long? period)
    {
        if (!IsReady) return Task.FromResult(new List<HitGroupRow>());

        return Task.Run(() =>
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            return SelectHitGroups(connection, transaction: null, steamId, period);
        });
    }

    /// <summary>Every stored per-weapon hitgroup row for a player, all time when <paramref name="period"/> is null. Off-thread.</summary>
    public Task<List<WeaponHitGroupRow>> ReadWeaponHitGroupsAsync(ulong steamId, long? period)
    {
        if (!IsReady) return Task.FromResult(new List<WeaponHitGroupRow>());

        return Task.Run(() =>
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            return SelectWeaponHitGroups(connection, transaction: null, steamId, period);
        });
    }

    /// <summary>Runs <paramref name="operation"/> off-thread once everything queued before it has finished.</summary>
    /// <remarks>
    /// One at a time and in call order, which is what lets a reset trust every batch cut before it to
    /// have landed or to sit in the retry buffer. A continuation chain rather than a semaphore taken
    /// inside <c>Task.Run</c>: two tasks started in order can still reach a semaphore out of order.
    /// </remarks>
    private Task Enqueue(Action operation)
    {
        lock (_queueGate)
            return _queue = _queue.ContinueWith(_ => operation(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Writes the batch together with whatever earlier writes left in the retry buffer.</summary>
    /// <remarks>
    /// A failed batch goes back into the retry buffer, off-thread, rather than into the live session
    /// objects, which belong to the game thread. Logged ungated: it is the data-loss path.
    /// </remarks>
    private void WritePending(List<StatsDelta> batch)
    {
        _retry.AddRange(batch);
        List<StatsDelta> pending = [.. _retry];
        _retry.Clear();

        try
        {
            Write(pending);
        }
        catch (Exception ex)
        {
            _retry.AddRange(pending);
            _logger.LogError(ex, "Ranks_WeaponStats: flush of {Count} player batch(es) failed; they are buffered for retry.", pending.Count);
        }
    }

    private void Write(List<StatsDelta> batch)
    {
        using var connection = _core.Database.GetConnection(_connectionName);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var delta in batch)
        {
            WriteDelta(connection, transaction, delta, period: null);

            if (delta.Period is { } period)
                WriteDelta(connection, transaction, delta, period);
        }

        transaction.Commit();
    }

    /// <summary>Adds one delta onto the all-time rows, or onto one period's rows when <paramref name="period"/> is set.</summary>
    private void WriteDelta(IDbConnection connection, IDbTransaction transaction, StatsDelta delta, long? period)
    {
        foreach (var (weapon, counters) in delta.Weapons)
            WriteWeapon(connection, transaction, delta.SteamId, period, weapon, counters);

        foreach (var (hitGroup, hits, damage) in delta.HitGroups)
            WriteHitGroup(connection, transaction, delta.SteamId, period, hitGroup, hits, damage);

        foreach (var (weapon, hitGroup, hits, damage) in delta.WeaponHitGroups)
            WriteWeaponHitGroup(connection, transaction, delta.SteamId, period, weapon, hitGroup, hits, damage);
    }

    private void WriteWeapon(IDbConnection connection, IDbTransaction transaction, ulong steamId, long? period, string weapon, WeaponCounters counters)
    {
        var table = period is null ? _weaponTable : _weaponPeriodTable;
        var (keys, markers) = Key(period, "steam_id", "weapon");
        var columns = $"{keys}, shots, hits, misses, damage, kills, deaths, headshots";
        var values = $"{markers}, @shots, @hits, @misses, @damage, @kills, @deaths, @headshots";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // VALUES() rather than MySQL 8.0.20's "AS new" row alias: MariaDB does not support the
        // alias at all, and VALUES() works on both.
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {table} ({columns}) VALUES ({values})
               ON DUPLICATE KEY UPDATE
                   shots = shots + VALUES(shots), hits = hits + VALUES(hits),
                   misses = misses + VALUES(misses),
                   damage = damage + VALUES(damage), kills = kills + VALUES(kills),
                   deaths = deaths + VALUES(deaths), headshots = headshots + VALUES(headshots)
               """
            // Qualified with the table name, or the reference is ambiguous against EXCLUDED.
            : $"""
               INSERT INTO {table} ({columns}) VALUES ({values})
               ON CONFLICT ({keys}) DO UPDATE SET
                   shots = {table}.shots + EXCLUDED.shots,
                   hits = {table}.hits + EXCLUDED.hits,
                   misses = {table}.misses + EXCLUDED.misses,
                   damage = {table}.damage + EXCLUDED.damage,
                   kills = {table}.kills + EXCLUDED.kills,
                   deaths = {table}.deaths + EXCLUDED.deaths,
                   headshots = {table}.headshots + EXCLUDED.headshots
               """;

        AddOwner(command, steamId, period);
        Add(command, "@weapon", weapon);
        Add(command, "@shots", counters.Shots);
        Add(command, "@hits", counters.Hits);
        Add(command, "@misses", counters.Misses);
        Add(command, "@damage", counters.Damage);
        Add(command, "@kills", counters.Kills);
        Add(command, "@deaths", counters.Deaths);
        Add(command, "@headshots", counters.Headshots);

        command.ExecuteNonQuery();
    }

    private void WriteHitGroup(IDbConnection connection, IDbTransaction transaction, ulong steamId, long? period, short hitGroup, long hits, long damage)
    {
        var table = period is null ? _hitTable : _hitPeriodTable;
        var (keys, markers) = Key(period, "steam_id", "hitgroup");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {table} ({keys}, hits, damage)
               VALUES ({markers}, @hits, @damage)
               ON DUPLICATE KEY UPDATE hits = hits + VALUES(hits), damage = damage + VALUES(damage)
               """
            : $"""
               INSERT INTO {table} ({keys}, hits, damage)
               VALUES ({markers}, @hits, @damage)
               ON CONFLICT ({keys}) DO UPDATE SET
                   hits = {table}.hits + EXCLUDED.hits,
                   damage = {table}.damage + EXCLUDED.damage
               """;

        AddOwner(command, steamId, period);
        Add(command, "@hitgroup", hitGroup);
        Add(command, "@hits", hits);
        Add(command, "@damage", damage);

        command.ExecuteNonQuery();
    }

    private void WriteWeaponHitGroup(IDbConnection connection, IDbTransaction transaction, ulong steamId, long? period, string weapon, short hitGroup, long hits, long damage)
    {
        var table = period is null ? _weaponHitTable : _weaponHitPeriodTable;
        var (keys, markers) = Key(period, "steam_id", "weapon", "hitgroup");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {table} ({keys}, hits, damage)
               VALUES ({markers}, @hits, @damage)
               ON DUPLICATE KEY UPDATE hits = hits + VALUES(hits), damage = damage + VALUES(damage)
               """
            : $"""
               INSERT INTO {table} ({keys}, hits, damage)
               VALUES ({markers}, @hits, @damage)
               ON CONFLICT ({keys}) DO UPDATE SET
                   hits = {table}.hits + EXCLUDED.hits,
                   damage = {table}.damage + EXCLUDED.damage
               """;

        AddOwner(command, steamId, period);
        Add(command, "@weapon", weapon);
        Add(command, "@hitgroup", hitGroup);
        Add(command, "@hits", hits);
        Add(command, "@damage", damage);

        command.ExecuteNonQuery();
    }

    /// <remarks>
    /// Drops the player's retry entries for what is wiped before touching the database: all of them
    /// while periods are off, the wiped period's while they are on. A closed period's entries stay,
    /// because core keeps past periods through a reset. Not retried on failure — a later attempt
    /// would also take away whatever the player has earned since.
    /// </remarks>
    private void Reset(ulong steamId, long? period)
    {
        _retry.RemoveAll(delta => delta.SteamId == steamId && (period is null || delta.Period == period));

        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            if (period is { } wiped) ClearPeriod(connection, transaction, steamId, wiped);
            else ClearAllTime(connection, transaction, steamId);

            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks_WeaponStats: could not reset the weapon statistics of {SteamId}; they are unchanged.", steamId);
        }
    }

    /// <summary>Deletes every all-time row the player holds: a reset while periods are off, when all time is all there is.</summary>
    private void ClearAllTime(IDbConnection connection, IDbTransaction transaction, ulong steamId)
    {
        foreach (var table in (ReadOnlySpan<string>)[_weaponTable, _hitTable, _weaponHitTable])
            Execute(connection, transaction, $"DELETE FROM {table} WHERE steam_id = @steam_id", steamId, period: null);
    }

    /// <summary>Takes one period's rows off the player's all-time totals, then deletes them.</summary>
    /// <remarks>
    /// Clamped at zero rather than trusted, because nothing guarantees all time still holds the
    /// period: a reset made while periods were off clears all time and leaves the period rows alone.
    /// </remarks>
    private void ClearPeriod(IDbConnection connection, IDbTransaction transaction, ulong steamId, long period)
    {
        foreach (var row in SelectWeapons(connection, transaction, steamId, period))
            SubtractWeapon(connection, transaction, steamId, row);

        foreach (var row in SelectHitGroups(connection, transaction, steamId, period))
            SubtractHitGroup(connection, transaction, steamId, row);

        foreach (var row in SelectWeaponHitGroups(connection, transaction, steamId, period))
            SubtractWeaponHitGroup(connection, transaction, steamId, row);

        DeleteEmptiedRows(connection, transaction, steamId);

        foreach (var table in (ReadOnlySpan<string>)[_weaponPeriodTable, _hitPeriodTable, _weaponHitPeriodTable])
            Execute(connection, transaction, $"DELETE FROM {table} WHERE {OwnedBy(period)}", steamId, period);
    }

    private void SubtractWeapon(IDbConnection connection, IDbTransaction transaction, ulong steamId, WeaponRow row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {_weaponTable} SET
                shots = {Clamped("shots")},
                hits = {Clamped("hits")},
                misses = {Clamped("misses")},
                damage = {Clamped("damage")},
                kills = {Clamped("kills")},
                deaths = {Clamped("deaths")},
                headshots = {Clamped("headshots")}
            WHERE steam_id = @steam_id AND weapon = @weapon
            """;

        AddOwner(command, steamId, period: null);
        Add(command, "@weapon", row.Weapon);
        Add(command, "@shots", row.Shots);
        Add(command, "@hits", row.Hits);
        Add(command, "@misses", row.Misses);
        Add(command, "@damage", row.Damage);
        Add(command, "@kills", row.Kills);
        Add(command, "@deaths", row.Deaths);
        Add(command, "@headshots", row.Headshots);

        command.ExecuteNonQuery();
    }

    private void SubtractHitGroup(IDbConnection connection, IDbTransaction transaction, ulong steamId, HitGroupRow row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {_hitTable} SET
                hits = {Clamped("hits")},
                damage = {Clamped("damage")}
            WHERE steam_id = @steam_id AND hitgroup = @hitgroup
            """;

        AddOwner(command, steamId, period: null);
        Add(command, "@hitgroup", row.HitGroup);
        Add(command, "@hits", row.Hits);
        Add(command, "@damage", row.Damage);

        command.ExecuteNonQuery();
    }

    private void SubtractWeaponHitGroup(IDbConnection connection, IDbTransaction transaction, ulong steamId, WeaponHitGroupRow row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {_weaponHitTable} SET
                hits = {Clamped("hits")},
                damage = {Clamped("damage")}
            WHERE steam_id = @steam_id AND weapon = @weapon AND hitgroup = @hitgroup
            """;

        AddOwner(command, steamId, period: null);
        Add(command, "@weapon", row.Weapon);
        Add(command, "@hitgroup", row.HitGroup);
        Add(command, "@hits", row.Hits);
        Add(command, "@damage", row.Damage);

        command.ExecuteNonQuery();
    }

    /// <summary>Deletes the all-time rows a subtraction left at zero, so no screen lists a weapon or hitgroup at nothing.</summary>
    /// <remarks>Only a subtraction can produce one: a delta with nothing in it is never written.</remarks>
    private void DeleteEmptiedRows(IDbConnection connection, IDbTransaction transaction, ulong steamId)
    {
        foreach (var statement in (ReadOnlySpan<string>)
                 [
                     $"DELETE FROM {_weaponTable} WHERE steam_id = @steam_id AND shots = 0 AND hits = 0 AND misses = 0 AND damage = 0 AND kills = 0 AND deaths = 0 AND headshots = 0",
                     $"DELETE FROM {_hitTable} WHERE steam_id = @steam_id AND hits = 0 AND damage = 0",
                     $"DELETE FROM {_weaponHitTable} WHERE steam_id = @steam_id AND hits = 0 AND damage = 0",
                 ])
            Execute(connection, transaction, statement, steamId, period: null);
    }

    private void Purge(ulong[] ids)
    {
        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            foreach (var table in EveryTable())
                foreach (var steamId in ids)
                    Execute(connection, transaction, $"DELETE FROM {table} WHERE steam_id = @steam_id", steamId, period: null);

            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks_WeaponStats: failed to sweep {Count} purged player(s).", ids.Length);
        }
    }

    private void ReconcileOrphans(string playersTable)
    {
        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            var removed = 0;

            foreach (var table in EveryTable())
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM {table} WHERE steam_id NOT IN (SELECT steam_id FROM {playersTable})";
                removed += command.ExecuteNonQuery();
            }

            if (removed > 0)
                _logger.LogInformation("Ranks_WeaponStats: removed {Count} orphaned row(s) left by a purge this module missed.", removed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks_WeaponStats: orphan reconcile failed.");
        }
    }

    private List<WeaponRow> SelectWeapons(IDbConnection connection, IDbTransaction? transaction, ulong steamId, long? period)
    {
        var rows = new List<WeaponRow>();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT weapon, shots, hits, misses, damage, kills, deaths, headshots FROM {(period is null ? _weaponTable : _weaponPeriodTable)} WHERE {OwnedBy(period)}";
        AddOwner(command, steamId, period);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                      reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)));

        return rows;
    }

    private List<HitGroupRow> SelectHitGroups(IDbConnection connection, IDbTransaction? transaction, ulong steamId, long? period)
    {
        var rows = new List<HitGroupRow>();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT hitgroup, hits, damage FROM {(period is null ? _hitTable : _hitPeriodTable)} WHERE {OwnedBy(period)}";
        AddOwner(command, steamId, period);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt16(0), reader.GetInt64(1), reader.GetInt64(2)));

        return rows;
    }

    private List<WeaponHitGroupRow> SelectWeaponHitGroups(IDbConnection connection, IDbTransaction? transaction, ulong steamId, long? period)
    {
        var rows = new List<WeaponHitGroupRow>();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT weapon, hitgroup, hits, damage FROM {(period is null ? _weaponHitTable : _weaponHitPeriodTable)} WHERE {OwnedBy(period)}";
        AddOwner(command, steamId, period);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt16(1), reader.GetInt64(2), reader.GetInt64(3)));

        return rows;
    }

    /// <summary>All six tables: the three all-time ones, then their per-period copies.</summary>
    private string[] EveryTable() =>
        [_weaponTable, _hitTable, _weaponHitTable, _weaponPeriodTable, _hitPeriodTable, _weaponHitPeriodTable];

    /// <summary>A table's key columns and their markers: the all-time key, plus <c>period</c> on a per-period table.</summary>
    private static (string Columns, string Markers) Key(long? period, params string[] allTimeKey)
    {
        string[] key = period is null ? allTimeKey : [.. allTimeKey, "period"];

        return (string.Join(", ", key), string.Join(", ", key.Select(column => "@" + column)));
    }

    /// <summary>One player's rows: every one in an all-time table, one period's in a per-period table.</summary>
    private static string OwnedBy(long? period) =>
        period is null ? "steam_id = @steam_id" : "steam_id = @steam_id AND period = @period";

    /// <summary>A column less the parameter of the same name, stopping at zero.</summary>
    /// <remarks>A CASE rather than GREATEST, which SQLite does not have.</remarks>
    private static string Clamped(string column) => $"CASE WHEN {column} > @{column} THEN {column} - @{column} ELSE 0 END";

    /// <summary>Runs one statement over one player's rows.</summary>
    private static void Execute(IDbConnection connection, IDbTransaction? transaction, string statement, ulong steamId, long? period)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = statement;
        AddOwner(command, steamId, period);

        command.ExecuteNonQuery();
    }

    /// <summary>Binds the player, and the period only when there is one — a marker the statement does not use is never bound.</summary>
    private static void AddOwner(IDbCommand command, ulong steamId, long? period)
    {
        Add(command, "@steam_id", (long)steamId);

        if (period is { } stamp) Add(command, "@period", stamp);
    }

    private static void Add(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
