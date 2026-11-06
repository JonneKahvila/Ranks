using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Ranks.EloPoints.Match;
using SwiftlyS2.Shared;

namespace Ranks.EloPoints.Rating;

/// <summary>Reads and writes the authoritative rating. Off-thread, never touches a live session.</summary>
/// <remarks>
/// The upsert replaces <c>rating</c> rather than accumulating it, the opposite of
/// Ranks_WeaponStats: that module holds session deltas with no read on connect, while this one
/// reads the current value, computes a new one and writes it.
///
/// The rating table holds each player's current-period row only. A row left on an older period is
/// that period's final rating, and moves to the history table the first time a read or a write for
/// a later period touches its player (FR-DB-10).
///
/// Every database operation runs on one queue, in the order the game thread asked for it. That is
/// what keeps a reset between the settle before it and the settle after it, and what stops a read
/// closing a row while a write for the same player is still in flight.
/// </remarks>
internal sealed class RatingStore
{
    private readonly ISwiftlyCore _core;
    private readonly ILogger _logger;
    private readonly Lock _queueGate = new();

    /// <summary>Last known rating per player and the period it belongs to, so a resync can run without a read.</summary>
    private readonly ConcurrentDictionary<ulong, CachedRating> _cache = new();

    /// <summary>Changes a failed transaction left behind, applied again ahead of the next ones.</summary>
    /// <remarks>Touched only from queued work, and queued work never runs two at once.</remarks>
    private readonly List<RatingChange> _retry = [];

    private Task _queueTail = Task.CompletedTask;
    private string _connectionName = "default";
    private string _table = "ranks_elopoints";
    private string _history = "ranks_elopoints_history";
    private bool _mysql;

    public RatingStore(ISwiftlyCore core, ILogger logger)
    {
        _core = core;
        _logger = logger;
    }

    public bool IsReady { get; private set; }

    /// <summary>Binds the names core resolved. Until this runs the store refuses to touch the database.</summary>
    public void Configure(string connectionName, string table, string history)
    {
        _connectionName = connectionName;
        _table = table;
        _history = history;
        _mysql = _core.Database.GetConnectionInfo(connectionName).Driver?.Trim().ToLowerInvariant() is "mysql" or "mariadb";
        IsReady = true;
    }

    /// <summary>The rating last read or settled for this player in <paramref name="period"/>, when there is one.</summary>
    public bool TryGetCached(ulong steamId, long period, out double rating)
    {
        var known = _cache.TryGetValue(steamId, out var cached) && cached.Period == period ? cached.Rating : null;
        rating = known ?? 0;

        return known.HasValue;
    }

    /// <summary>Drops every cached rating: after a rollover they all belong to the period that closed.</summary>
    public void ForgetAll() => _cache.Clear();

    /// <summary>Reads a player's rating for <paramref name="period"/>, or null when they hold none there yet.</summary>
    /// <remarks>
    /// Off the game thread: a blocking database read at a round boundary is a stall. A read still
    /// in flight when a round ends leaves the session unresolved, and an unresolved player is left
    /// out of the team average rather than being averaged in at a guessed value.
    /// </remarks>
    public Task<double?> ReadAsync(ulong steamId, long period)
    {
        if (!IsReady) return Task.FromResult<double?>(null);

        if (_cache.TryGetValue(steamId, out var cached) && cached.Period == period) return Task.FromResult(cached.Rating);

        return Enqueue(() => ReadOrClose(steamId, period));
    }

    /// <summary>Queues settled ratings for writing, each into the period it was settled in.</summary>
    /// <remarks>
    /// A failed batch stays queued ahead of the next one rather than merging back into the live
    /// sessions, which belong to the game thread.
    /// </remarks>
    public Task CommitAsync(List<RatingWrite> batch)
    {
        if (!IsReady || batch.Count == 0) return Task.CompletedTask;

        foreach (var write in batch) _cache[write.SteamId] = new CachedRating(write.Period, write.Rating);

        var changes = batch.Select(RatingChange.Settled).ToArray();

        return Enqueue(() => Flush(changes));
    }

    /// <summary>Wipes a player's rating for <paramref name="period"/>, behind every write already queued.</summary>
    /// <remarks>
    /// Run at once instead, the delete could land before a write still in flight from the last settle,
    /// which would put the pre-reset rating straight back. The cache answers "none" from here on, so a
    /// read cannot find the old row while the delete waits its turn.
    /// </remarks>
    public Task ResetAsync(ulong steamId, long period)
    {
        if (!IsReady) return Task.CompletedTask;

        _cache[steamId] = new CachedRating(period, null);

        return Enqueue(() => Flush([RatingChange.Reset(steamId, period)]));
    }

    /// <summary>Deletes every row, current and past, for purged players.</summary>
    /// <remarks>
    /// <c>PlayersPurged</c> is the only notice this module will ever get — both tables are keyed on
    /// SteamID64 with nothing joining them back to core, so nothing else sweeps them.
    /// </remarks>
    public Task PurgeAsync(IReadOnlyList<ulong> steamIds)
    {
        if (!IsReady || steamIds.Count == 0) return Task.CompletedTask;

        var ids = steamIds.ToArray();
        foreach (var id in ids) _cache.TryRemove(id, out _);

        return Enqueue(() => Sweep(ids));
    }

    /// <summary>Runs <paramref name="work"/> on the thread pool once everything queued before it has finished.</summary>
    private Task<T> Enqueue<T>(Func<T> work)
    {
        lock (_queueGate)
        {
            var queued = _queueTail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _queueTail = queued;

            return queued;
        }
    }

    private Task Enqueue(Action work) => Enqueue(() =>
    {
        work();
        return true;
    });

    /// <summary>Returns the stored rating, or closes a row left on an older period and returns null.</summary>
    /// <remarks>
    /// An older row is that period's final rating, so it moves to history and the player starts this
    /// period unrated. A row on a newer period comes back as it is: another server sharing the
    /// database crossed the boundary first, and this one is about to follow.
    /// </remarks>
    private double? ReadOrClose(ulong steamId, long period)
    {
        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            if (ReadRow(connection, null, steamId) is not { } row) return null;

            if (row.Period < period)
            {
                using var transaction = connection.BeginTransaction();
                Archive(connection, transaction, steamId, row.Period);
                transaction.Commit();

                return null;
            }

            Remember(steamId, period, row.Rating);

            return row.Rating;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks_EloPoints: could not read the stored rating for {SteamId}.", steamId);
            return null;
        }
    }

    /// <summary>Caches a rating read from the table, unless something newer for that period got there first.</summary>
    /// <remarks>
    /// A settle or a reset on the game thread may land while the read is in flight, and either one
    /// knows more than the row this read saw.
    /// </remarks>
    private void Remember(ulong steamId, long period, double rating)
    {
        var read = new CachedRating(period, rating);
        _cache.AddOrUpdate(steamId, read, (_, existing) => existing.Period == period ? existing : read);
    }

    /// <summary>Applies whatever a failed transaction left behind, then <paramref name="changes"/>, in one transaction.</summary>
    /// <remarks>
    /// Never reordered: a reset queued between two settles has to stay between them, or one of them
    /// restores the rating it wiped. A data-loss path, so the failure is never gated behind
    /// DetailedLogging.
    /// </remarks>
    private void Flush(IEnumerable<RatingChange> changes)
    {
        _retry.AddRange(changes);

        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            foreach (var change in _retry) Apply(connection, transaction, change);

            transaction.Commit();
            _retry.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks_EloPoints: writing {Count} rating change(s) failed; they are buffered for retry.", _retry.Count);
        }
    }

    private void Apply(IDbConnection connection, IDbTransaction transaction, RatingChange change)
    {
        if (change.Write is { } write) Write(connection, transaction, write);
        else Wipe(connection, transaction, change.SteamId, change.Period);
    }

    /// <summary>Writes one settled rating into the period it was settled in.</summary>
    /// <remarks>
    /// FR-DB-10. A row on an older period is closed first. A rating whose period has already closed
    /// for the player — a retry that crossed a rollover — goes to that period's history row instead,
    /// so it neither overwrites the current period nor re-opens a closed one.
    /// </remarks>
    private void Write(IDbConnection connection, IDbTransaction transaction, RatingWrite write)
    {
        var held = ReadRow(connection, transaction, write.SteamId)?.Period;

        if (held is { } stale && stale < write.Period) Archive(connection, transaction, write.SteamId, stale);

        var closed = held is { } stored ? stored > write.Period : HistoryHolds(connection, transaction, write);

        if (closed) Upsert(connection, transaction, _history, "steam_id, period", write);
        else Upsert(connection, transaction, _table, "steam_id", write);
    }

    /// <summary>Upserts one rating: <c>rating</c> is replaced, the two counters accumulate.</summary>
    /// <remarks>
    /// VALUES() on MySQL rather than 8.0.20's "AS new" row alias, which MariaDB does not support at
    /// all; elsewhere the counters are qualified with the table name, or the reference is ambiguous
    /// against EXCLUDED. The update leaves <c>period</c> alone: the row it can hit already holds this
    /// write's period, because <see cref="Write"/> closed or bypassed any other.
    /// </remarks>
    private void Upsert(IDbConnection connection, IDbTransaction transaction, string table, string key, RatingWrite write)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {table} (steam_id, period, rating, matches, abandons, updated_at)
               VALUES (@steam_id, @period, @rating, 1, @abandons, @updated_at)
               ON DUPLICATE KEY UPDATE
                   rating = VALUES(rating),
                   matches = matches + 1,
                   abandons = abandons + VALUES(abandons),
                   updated_at = VALUES(updated_at)
               """
            : $"""
               INSERT INTO {table} (steam_id, period, rating, matches, abandons, updated_at)
               VALUES (@steam_id, @period, @rating, 1, @abandons, @updated_at)
               ON CONFLICT ({key}) DO UPDATE SET
                   rating = EXCLUDED.rating,
                   matches = {table}.matches + 1,
                   abandons = {table}.abandons + EXCLUDED.abandons,
                   updated_at = EXCLUDED.updated_at
               """;

        Add(command, "@steam_id", (long)write.SteamId);
        Add(command, "@period", write.Period);
        Add(command, "@rating", (decimal)write.Rating);
        Add(command, "@abandons", write.Abandoned ? 1 : 0);
        Add(command, "@updated_at", DateTime.UtcNow);

        command.ExecuteNonQuery();
    }

    /// <summary>Moves a player's row off a closed period: copied to history, then deleted.</summary>
    /// <remarks>
    /// Both statements are guarded on the stale period, so a second server closing the same row at
    /// once copies nothing and deletes nothing. A history row already there for that period is that
    /// same row, closed first by another server sharing the database, so the copy skips it.
    /// </remarks>
    private void Archive(IDbConnection connection, IDbTransaction transaction, ulong steamId, long stale)
    {
        const string Columns = "steam_id, period, rating, matches, abandons, updated_at";

        using (var copy = connection.CreateCommand())
        {
            copy.Transaction = transaction;
            copy.CommandText = _mysql
                ? $"INSERT IGNORE INTO {_history} ({Columns}) SELECT {Columns} FROM {_table} " +
                  "WHERE steam_id = @steam_id AND period = @stale"
                : $"INSERT INTO {_history} ({Columns}) SELECT {Columns} FROM {_table} " +
                  "WHERE steam_id = @steam_id AND period = @stale ON CONFLICT (steam_id, period) DO NOTHING";
            Add(copy, "@steam_id", (long)steamId);
            Add(copy, "@stale", stale);
            copy.ExecuteNonQuery();
        }

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = $"DELETE FROM {_table} WHERE steam_id = @steam_id AND period = @stale";
        Add(delete, "@steam_id", (long)steamId);
        Add(delete, "@stale", stale);
        delete.ExecuteNonQuery();
    }

    /// <summary>Deletes a player's rating for one period, from whichever table holds it.</summary>
    /// <remarks>
    /// The period was current when the player was reset. History only comes into it when that period
    /// closed while this delete was waiting on a retry, and a read had already moved the row there.
    /// </remarks>
    private void Wipe(IDbConnection connection, IDbTransaction transaction, ulong steamId, long period)
    {
        Delete(connection, transaction, _table, steamId, period);
        Delete(connection, transaction, _history, steamId, period);
    }

    /// <summary>Deletes purged players from both tables, and drops anything of theirs still waiting to be written.</summary>
    private void Sweep(ulong[] ids)
    {
        var purged = ids.ToHashSet();
        _retry.RemoveAll(change => purged.Contains(change.SteamId));

        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            foreach (var steamId in ids)
            {
                Delete(connection, transaction, _table, steamId, null);
                Delete(connection, transaction, _history, steamId, null);
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks_EloPoints: failed to sweep {Count} purged player(s).", ids.Length);
        }
    }

    /// <summary>The player's row in the rating table, whatever period it holds, or null when there is none.</summary>
    private (long Period, double Rating)? ReadRow(IDbConnection connection, IDbTransaction? transaction, ulong steamId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT period, rating FROM {_table} WHERE steam_id = @steam_id";
        Add(command, "@steam_id", (long)steamId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    /// <summary>True when the write's period has already closed for its player and sits in history.</summary>
    private bool HistoryHolds(IDbConnection connection, IDbTransaction transaction, RatingWrite write)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {_history} WHERE steam_id = @steam_id AND period = @period";
        Add(command, "@steam_id", (long)write.SteamId);
        Add(command, "@period", write.Period);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <remarks>Without <paramref name="period"/> every row the player holds in <paramref name="table"/> goes.</remarks>
    private static void Delete(IDbConnection connection, IDbTransaction transaction, string table, ulong steamId, long? period)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = period is null
            ? $"DELETE FROM {table} WHERE steam_id = @steam_id"
            : $"DELETE FROM {table} WHERE steam_id = @steam_id AND period = @period";
        Add(command, "@steam_id", (long)steamId);

        if (period is { } only) Add(command, "@period", only);

        command.ExecuteNonQuery();
    }

    private static void Add(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>A rating and the period it belongs to. A null rating means the player is known to hold none there.</summary>
    private readonly record struct CachedRating(long Period, double? Rating);

    /// <summary>One queued change: a settled rating, or — with no <see cref="Write"/> — a reset of the player's period.</summary>
    private readonly record struct RatingChange(ulong SteamId, long Period, RatingWrite? Write)
    {
        public static RatingChange Settled(RatingWrite write) => new(write.SteamId, write.Period, write);

        public static RatingChange Reset(ulong steamId, long period) => new(steamId, period, null);
    }
}
