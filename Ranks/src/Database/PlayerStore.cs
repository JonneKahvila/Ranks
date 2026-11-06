using System.Data;
using Ranks.Contract;
using Ranks.Players;

namespace Ranks.Database;

/// <summary>Maps the stat keys the API exposes onto core's fixed columns.</summary>
/// <remarks>
/// A whitelist, not an escaping routine: <c>statKey</c> arrives from module code and is
/// concatenated into an ORDER BY, which no parameter can carry. Anything not listed here is
/// refused rather than sanitised.
/// </remarks>
internal static class StatColumns
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["points"] = "points",
        ["core.points"] = "points",
        ["core.kills"] = "kills",
        ["core.deaths"] = "deaths",
        ["core.assists"] = "assists",
        ["core.headshots"] = "headshots",
        ["core.hits"] = "hits",
        ["core.misses"] = "misses",
        ["core.round_wins"] = "round_wins",
        ["core.round_losses"] = "round_losses",
        ["core.match_wins"] = "match_wins",
        ["core.match_losses"] = "match_losses",
        ["core.bombs_planted"] = "bombs_planted",
        ["core.bombs_defused"] = "bombs_defused",
    };

    public static bool TryResolve(string statKey, out string column) => Map.TryGetValue(statKey, out column!);

    /// <summary>Reads a counter straight off a resident record.</summary>
    /// <remarks>
    /// Stat keys keep the <c>core.</c> prefix that point-event keys dropped, so <c>kill</c> is not
    /// an error here and returns 0 forever, which is the documented behaviour (FR-API-03).
    /// </remarks>
    public static long Read(PlayerRecord record, string statKey) => statKey.ToLowerInvariant() switch
    {
        "points" or "core.points" => record.Points,
        "core.kills" => record.Kills,
        "core.deaths" => record.Deaths,
        "core.assists" => record.Assists,
        "core.headshots" => record.Headshots,
        "core.hits" => record.Hits,
        "core.misses" => record.Misses,
        "core.round_wins" => record.RoundWins,
        "core.round_losses" => record.RoundLosses,
        "core.match_wins" => record.MatchWins,
        "core.match_losses" => record.MatchLosses,
        "core.bombs_planted" => record.BombsPlanted,
        "core.bombs_defused" => record.BombsDefused,
        _ => 0,
    };
}

/// <summary>Reads and writes core's two tables. The single writer for <c>ranks_players</c>.</summary>
internal sealed class PlayerStore
{
    private readonly DatabaseConnection _database;
    private readonly TableNames _tables;
    private readonly bool _mysql;

    public PlayerStore(DatabaseConnection database, TableNames tables)
    {
        _database = database;
        _tables = tables;
        _mysql = database.Info.Driver?.Trim().ToLowerInvariant() is "mysql" or "mariadb";
    }

    public string PlayersTable => _tables.Resolve("ranks_players");

    public string SettingsTable => _tables.Resolve("ranks_settings");

    public string HistoryTable => _tables.Resolve("ranks_players_history");

    /// <summary>Loads one player for tracking in <paramref name="period"/>, closing a stale stored period first.</summary>
    /// <remarks>
    /// Returns null only when the player has no row yet; a <em>failure</em> throws, because a
    /// failed load must leave the player untracked and retry rather than substitute an empty
    /// record whose next absolute save would wipe the real row (FR-DB-07).
    /// <para>
    /// A row still holding an older period is moved to history and reset here, in one transaction,
    /// rather than left for the next save: until both halves land, a past period's leaderboard would
    /// count that row twice. A row holding a <em>newer</em> period comes back untouched — another
    /// server sharing the database rolled over first, and the caller catches up rather than this
    /// archiving that server's period backwards.
    /// </para>
    /// </remarks>
    public PlayerRecord? Load(ulong steamId, long period, int startPoints)
    {
        using var connection = _database.Create();
        connection.Open();

        if (ReadPlayerRow(connection, steamId) is not { } stored) return null;
        if (stored.Period >= period) return stored;

        CloseStalePeriod(connection, steamId, stored.Period, period, startPoints);
        stored.Period = period;
        stored.ClearProgress(startPoints, 0);

        return stored;
    }

    /// <summary>The player's figures for <paramref name="period"/> as tracking would see them, without writing.</summary>
    /// <remarks>A row still on an older period reads as the fresh record its next load will make of it.</remarks>
    public PlayerRecord? ReadCurrent(ulong steamId, long period, int startPoints)
    {
        using var connection = _database.Create();
        connection.Open();

        if (ReadPlayerRow(connection, steamId) is not { } stored) return null;
        if (stored.Period >= period) return stored;

        stored.Period = period;
        stored.ClearProgress(startPoints, 0);

        return stored;
    }

    /// <summary>The player's row exactly as stored, whatever period it holds.</summary>
    public PlayerRecord? ReadStored(ulong steamId)
    {
        using var connection = _database.Create();
        connection.Open();

        return ReadPlayerRow(connection, steamId);
    }

    /// <summary>A closed period's final figures, or null when the player holds none there.</summary>
    /// <remarks>Falls back to the player row while it still holds that period and has not been moved yet.</remarks>
    public PlayerRecord? ReadPeriod(ulong steamId, long period)
    {
        using var connection = _database.Create();
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT h.name, h.points, h.kills, h.deaths, h.assists,
                       h.round_wins, h.round_losses, h.match_wins, h.match_losses,
                       h.bombs_planted, h.bombs_defused, h.last_seen,
                       h.headshots, h.hits, h.misses, p.first_seen
                  FROM {HistoryTable} h
                  LEFT JOIN {PlayersTable} p ON p.steam_id = h.steam_id
                 WHERE h.steam_id = @steam_id AND h.period = @period
                """;
            AddParameter(command, "@steam_id", (long)steamId);
            AddParameter(command, "@period", period);

            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                var lastSeen = reader.GetDateTime(11);

                return new PlayerRecord
                {
                    SteamId = steamId,
                    Period = period,
                    Name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    Points = reader.GetInt32(1),
                    Kills = reader.GetInt32(2),
                    Deaths = reader.GetInt32(3),
                    Assists = reader.GetInt32(4),
                    RoundWins = reader.GetInt32(5),
                    RoundLosses = reader.GetInt32(6),
                    MatchWins = reader.GetInt32(7),
                    MatchLosses = reader.GetInt32(8),
                    BombsPlanted = reader.GetInt32(9),
                    BombsDefused = reader.GetInt32(10),
                    LastSeen = lastSeen,
                    Headshots = reader.GetInt32(12),
                    Hits = reader.GetInt32(13),
                    Misses = reader.GetInt32(14),
                    FirstSeen = reader.IsDBNull(15) ? lastSeen : reader.GetDateTime(15),
                };
            }
        }

        return ReadPlayerRow(connection, steamId) is { } stored && stored.Period == period ? stored : null;
    }

    /// <summary>Every closed period the player holds, summed.</summary>
    /// <remarks>
    /// Never includes the period the player row holds: a period reaches history only once that row
    /// has moved past it, so this plus the row is the whole of a player's play, counted once.
    /// </remarks>
    public PlayerTotals ReadHistoryTotals(ulong steamId)
    {
        using var connection = _database.Create();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*), COALESCE(SUM(points), 0), COALESCE(SUM(kills), 0), COALESCE(SUM(deaths), 0),
                   COALESCE(SUM(assists), 0), COALESCE(SUM(round_wins), 0), COALESCE(SUM(round_losses), 0),
                   COALESCE(SUM(match_wins), 0), COALESCE(SUM(match_losses), 0),
                   COALESCE(SUM(bombs_planted), 0), COALESCE(SUM(bombs_defused), 0),
                   COALESCE(SUM(headshots), 0), COALESCE(SUM(hits), 0), COALESCE(SUM(misses), 0)
              FROM {HistoryTable}
             WHERE steam_id = @steam_id
            """;
        AddParameter(command, "@steam_id", (long)steamId);

        using var reader = command.ExecuteReader();
        reader.Read();

        long At(int ordinal) => Convert.ToInt64(reader.GetValue(ordinal));

        return new PlayerTotals(
            At(0), At(1), At(2), At(3), At(4), At(5), At(6), At(7), At(8), At(9), At(10), At(11), At(12), At(13));
    }

    /// <summary>The newest period any player row holds, or <c>RanksPeriods.Initial</c> on an empty table.</summary>
    /// <remarks>Where a server with periods disabled resumes, so switching them off never starts one.</remarks>
    public long LatestPeriod()
    {
        using var connection = _database.Create();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MAX(period) FROM {PlayersTable}";

        var result = command.ExecuteScalar();

        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    /// <summary>Every period either table holds, unordered and distinct.</summary>
    public List<long> Periods()
    {
        using var connection = _database.Create();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT period FROM {PlayersTable} GROUP BY period
            UNION
            SELECT period FROM {HistoryTable} GROUP BY period
            """;

        var periods = new List<long>();

        using var reader = command.ExecuteReader();
        while (reader.Read()) periods.Add(Convert.ToInt64(reader.GetValue(0)));

        return periods;
    }

    /// <summary>Reads the player row with its settings, both tables in one round trip.</summary>
    private PlayerRecord? ReadPlayerRow(IDbConnection connection, ulong steamId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.steam_id, p.name, p.points, p.kills, p.deaths, p.assists,
                   p.round_wins, p.round_losses, p.match_wins, p.match_losses,
                   p.bombs_planted, p.bombs_defused, p.first_seen, p.last_seen,
                   s.show_point_messages, p.headshots, p.hits, p.misses, p.period
              FROM {PlayersTable} p
              LEFT JOIN {SettingsTable} s ON s.steam_id = p.steam_id
             WHERE p.steam_id = @steam_id
            """;
        AddParameter(command, "@steam_id", (long)steamId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return new PlayerRecord
        {
            SteamId = steamId,
            Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Points = reader.GetInt32(2),
            Kills = reader.GetInt32(3),
            Deaths = reader.GetInt32(4),
            Assists = reader.GetInt32(5),
            RoundWins = reader.GetInt32(6),
            RoundLosses = reader.GetInt32(7),
            MatchWins = reader.GetInt32(8),
            MatchLosses = reader.GetInt32(9),
            BombsPlanted = reader.GetInt32(10),
            BombsDefused = reader.GetInt32(11),
            FirstSeen = reader.GetDateTime(12),
            LastSeen = reader.GetDateTime(13),
            ShowPointMessages = reader.IsDBNull(14) || Convert.ToBoolean(reader.GetValue(14)),
            Headshots = reader.GetInt32(15),
            Hits = reader.GetInt32(16),
            Misses = reader.GetInt32(17),
            Period = Convert.ToInt64(reader.GetValue(18)),
        };
    }

    /// <summary>Moves a stale row's figures to history and resets the row into <paramref name="period"/>.</summary>
    /// <remarks>
    /// Both statements are guarded on the stale period, so a second server closing the same row at
    /// once inserts nothing and updates nothing. The insert keeps an existing history row because
    /// that one came from a resident rollover, whose figures are at least as recent as this row's.
    /// </remarks>
    private void CloseStalePeriod(IDbConnection connection, ulong steamId, long stale, long period, int startPoints)
    {
        const string Columns = "steam_id, period, name, points, kills, deaths, assists, round_wins, round_losses, " +
                               "match_wins, match_losses, bombs_planted, bombs_defused, headshots, hits, misses, last_seen";

        using var transaction = connection.BeginTransaction();

        using (var archive = connection.CreateCommand())
        {
            archive.Transaction = transaction;
            archive.CommandText = _mysql
                ? $"INSERT IGNORE INTO {HistoryTable} ({Columns}) SELECT {Columns} FROM {PlayersTable} " +
                  "WHERE steam_id = @steam_id AND period = @stale"
                : $"INSERT INTO {HistoryTable} ({Columns}) SELECT {Columns} FROM {PlayersTable} " +
                  "WHERE steam_id = @steam_id AND period = @stale ON CONFLICT (steam_id, period) DO NOTHING";
            AddParameter(archive, "@steam_id", (long)steamId);
            AddParameter(archive, "@stale", stale);
            archive.ExecuteNonQuery();
        }

        using (var reset = connection.CreateCommand())
        {
            reset.Transaction = transaction;
            reset.CommandText = $"""
                UPDATE {PlayersTable}
                   SET points = @points, kills = 0, deaths = 0, assists = 0, round_wins = 0, round_losses = 0,
                       match_wins = 0, match_losses = 0, bombs_planted = 0, bombs_defused = 0,
                       headshots = 0, hits = 0, misses = 0, period = @period
                 WHERE steam_id = @steam_id AND period = @stale
                """;
            AddParameter(reset, "@points", startPoints);
            AddParameter(reset, "@period", period);
            AddParameter(reset, "@steam_id", (long)steamId);
            AddParameter(reset, "@stale", stale);
            reset.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Writes a batch of records absolutely, isolating each behind a savepoint.</summary>
    /// <remarks>
    /// One bad row — an unencodable name is the realistic case — skips that player rather than
    /// aborting the batch, or one corrupt record stops the whole server from saving. Only committed
    /// SteamIDs come back, so the caller marks exactly those clean and the rest retry with their
    /// then-latest values.
    /// </remarks>
    public SaveOutcome Save(IReadOnlyList<PlayerSnapshotRow> rows)
    {
        if (rows.Count == 0) return new SaveOutcome([], []);

        var committed = new List<ulong>(rows.Count);
        var failed = new List<(ulong SteamId, Exception Error)>();

        using var connection = _database.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var savepoint = $"ranks_sp_{i}";

            try
            {
                Execute(connection, transaction, $"SAVEPOINT {savepoint}");

                foreach (var archive in row.Archives ?? [])
                    WriteHistory(connection, transaction, archive);

                WritePlayer(connection, transaction, row);
                WriteSettings(connection, transaction, row);
                Execute(connection, transaction, $"RELEASE SAVEPOINT {savepoint}");

                committed.Add(row.SteamId);
            }
            catch (Exception ex)
            {
                Execute(connection, transaction, $"ROLLBACK TO SAVEPOINT {savepoint}");
                failed.Add((row.SteamId, ex));
            }
        }

        transaction.Commit();

        return new SaveOutcome(committed, failed);
    }

    /// <summary>Top rows by one column within a period, or all time, carrying points so the caller can resolve a rank tier.</summary>
    /// <remarks>
    /// Points come back even when the sort is on another column: an offline player has no resident
    /// record, so without them every row of a kills leaderboard would render as the lowest tier.
    /// </remarks>
    public List<(ulong SteamId, string Name, long Value, int Points)> Top(
        string column, int limit, long period, long currentPeriod, int startPoints)
    {
        using var connection = _database.Create();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT steam_id, name, stat_value, points FROM ({Ranking(column, period, currentPeriod)}) r
             ORDER BY stat_value DESC, steam_id ASC
             LIMIT @limit
            """;
        AddRankingParameters(command, column, period, startPoints);
        AddParameter(command, "@limit", limit);

        var rows = new List<(ulong, string, long, int)>(limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(((ulong)reader.GetInt64(0),
                      reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                      Convert.ToInt64(reader.GetValue(2)),
                      reader.GetInt32(3)));

        return rows;
    }

    /// <summary>The player's position within a period, or all time; 0 when they hold no figures there.</summary>
    /// <remarks>Counts strictly-better rows, so equal values share a position rather than ordering arbitrarily.</remarks>
    public int Position(ulong steamId, string column, long period, long currentPeriod, int startPoints)
    {
        using var connection = _database.Create();
        connection.Open();

        var ranking = Ranking(column, period, currentPeriod);

        object? own;

        using (var value = connection.CreateCommand())
        {
            value.CommandText = $"SELECT stat_value FROM ({ranking}) r WHERE steam_id = @steam_id";
            AddRankingParameters(value, column, period, startPoints);
            AddParameter(value, "@steam_id", (long)steamId);

            own = value.ExecuteScalar();
        }

        if (own is null or DBNull) return 0;

        using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) + 1 FROM ({ranking}) r WHERE stat_value > @own";
        AddRankingParameters(count, column, period, startPoints);
        AddParameter(count, "@own", Convert.ToInt64(own));

        return Convert.ToInt32(count.ExecuteScalar() ?? 0);
    }

    /// <summary>How many players hold figures in a period, or at all.</summary>
    public int TotalRanked(long period, long currentPeriod)
    {
        using var connection = _database.Create();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = period == RanksPeriods.AllTime
            ? $"SELECT COUNT(*) FROM {PlayersTable}"
            : $"SELECT COUNT(*) FROM ({Ranking("points", period, currentPeriod)}) r";

        if (period != RanksPeriods.AllTime) AddParameter(command, "@period", period);

        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    /// <summary>One row per player — <c>steam_id, name, stat_value, points</c> — for a period or all time.</summary>
    /// <remarks>
    /// <paramref name="column"/> must already be a whitelisted <see cref="StatColumns"/> value; it is
    /// concatenated, as no parameter can carry an identifier.
    /// <para>
    /// The current period never reaches history, so it reads the player table alone; a closed one
    /// also reads player rows that still hold it, which move only when their owner next loads.
    /// All time sums both tables. Its <c>points</c> subtracts <c>@start</c> from every period, then
    /// adds it back once — the total a player would hold had no period ever reset them.
    /// </para>
    /// </remarks>
    private string Ranking(string column, long period, long currentPeriod)
    {
        if (period == currentPeriod)
            return $"SELECT steam_id, name, {column} AS stat_value, points FROM {PlayersTable} WHERE period = @period";

        if (period != RanksPeriods.AllTime)
            return $"""
                SELECT steam_id, name, {column} AS stat_value, points FROM {HistoryTable} WHERE period = @period
                UNION ALL
                SELECT steam_id, name, {column} AS stat_value, points FROM {PlayersTable} WHERE period = @period
                """;

        var isPoints = column == "points";
        var part = isPoints ? "points - @start" : column;
        var total = isPoints ? "SUM(stat_part) + @start" : "SUM(stat_part)";

        return $"""
            SELECT a.steam_id, p.name, a.stat_value, p.points
              FROM (SELECT steam_id, {total} AS stat_value
                      FROM (SELECT steam_id, {part} AS stat_part FROM {HistoryTable}
                            UNION ALL
                            SELECT steam_id, {part} AS stat_part FROM {PlayersTable}) u
                     GROUP BY steam_id) a
              JOIN {PlayersTable} p ON p.steam_id = a.steam_id
            """;
    }

    /// <remarks>Adds only what <see cref="Ranking"/> references: a parameter the SQL never names is not portable.</remarks>
    private static void AddRankingParameters(IDbCommand command, string column, long period, int startPoints)
    {
        if (period != RanksPeriods.AllTime) AddParameter(command, "@period", period);
        else if (column == "points") AddParameter(command, "@start", startPoints);
    }

    /// <summary>Deletes players unseen for longer than the threshold, cascading to settings and history.</summary>
    /// <remarks>
    /// Returns the purged SteamIDs because nothing else ever will: module tables are keyed on
    /// SteamID64 with nothing joining them back to core, so without this list their rows orphan
    /// permanently (FR-DB-08).
    /// </remarks>
    public List<ulong> Purge(int purgeDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-purgeDays);
        var purged = new List<ulong>();

        using var connection = _database.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT steam_id FROM {PlayersTable} WHERE last_seen < @cutoff";
            AddParameter(select, "@cutoff", cutoff);

            using var reader = select.ExecuteReader();
            while (reader.Read()) purged.Add((ulong)reader.GetInt64(0));
        }

        if (purged.Count == 0)
        {
            transaction.Rollback();
            return purged;
        }

        // Settings first, while the players rows it selects from still exist.
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {SettingsTable} WHERE steam_id IN " +
                                 $"(SELECT steam_id FROM {PlayersTable} WHERE last_seen < @cutoff)";
            AddParameter(delete, "@cutoff", cutoff);
            delete.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {HistoryTable} WHERE steam_id IN " +
                                 $"(SELECT steam_id FROM {PlayersTable} WHERE last_seen < @cutoff)";
            AddParameter(delete, "@cutoff", cutoff);
            delete.ExecuteNonQuery();
        }

        // Filtered directly rather than by subquery: MySQL refuses "DELETE FROM t WHERE x IN
        // (SELECT ... FROM t)" with "You can't specify target table for update in FROM clause".
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {PlayersTable} WHERE last_seen < @cutoff";
            AddParameter(delete, "@cutoff", cutoff);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();

        return purged;
    }

    private void WritePlayer(IDbConnection connection, IDbTransaction transaction, PlayerSnapshotRow row)
    {
        const string Columns = "steam_id, name, points, kills, deaths, assists, round_wins, round_losses, " +
                               "match_wins, match_losses, bombs_planted, bombs_defused, first_seen, last_seen, " +
                               "headshots, hits, misses, period";
        const string Values = "@steam_id, @name, @points, @kills, @deaths, @assists, @round_wins, @round_losses, " +
                              "@match_wins, @match_losses, @bombs_planted, @bombs_defused, @first_seen, @last_seen, " +
                              "@headshots, @hits, @misses, @period";

        // first_seen is deliberately absent from both update lists: it is set once, and an absolute
        // writer that restated it would move it forward on every save.
        const string Assignments = "name = @name, points = @points, kills = @kills, deaths = @deaths, " +
                                   "assists = @assists, round_wins = @round_wins, round_losses = @round_losses, " +
                                   "match_wins = @match_wins, match_losses = @match_losses, " +
                                   "bombs_planted = @bombs_planted, bombs_defused = @bombs_defused, " +
                                   "headshots = @headshots, hits = @hits, misses = @misses, " +
                                   "last_seen = @last_seen, period = @period";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"INSERT INTO {PlayersTable} ({Columns}) VALUES ({Values}) ON DUPLICATE KEY UPDATE {Assignments}"
            : $"INSERT INTO {PlayersTable} ({Columns}) VALUES ({Values}) ON CONFLICT (steam_id) DO UPDATE SET {Assignments}";

        AddParameter(command, "@steam_id", (long)row.SteamId);
        AddParameter(command, "@name", row.Name);
        AddParameter(command, "@points", row.Points);
        AddParameter(command, "@kills", row.Kills);
        AddParameter(command, "@deaths", row.Deaths);
        AddParameter(command, "@assists", row.Assists);
        AddParameter(command, "@round_wins", row.RoundWins);
        AddParameter(command, "@round_losses", row.RoundLosses);
        AddParameter(command, "@match_wins", row.MatchWins);
        AddParameter(command, "@match_losses", row.MatchLosses);
        AddParameter(command, "@bombs_planted", row.BombsPlanted);
        AddParameter(command, "@bombs_defused", row.BombsDefused);
        AddParameter(command, "@first_seen", row.FirstSeen);
        AddParameter(command, "@last_seen", row.LastSeen);
        AddParameter(command, "@headshots", row.Headshots);
        AddParameter(command, "@hits", row.Hits);
        AddParameter(command, "@misses", row.Misses);
        AddParameter(command, "@period", row.Period);

        command.ExecuteNonQuery();
    }

    /// <summary>Writes a closed period's final figures, replacing any earlier copy.</summary>
    /// <remarks>
    /// Replaces rather than keeps: this comes from the resident record, which is the newest
    /// account of that period there is — an existing row can only be an older one.
    /// </remarks>
    private void WriteHistory(IDbConnection connection, IDbTransaction transaction, PlayerHistoryRow row)
    {
        const string Columns = "steam_id, period, name, points, kills, deaths, assists, round_wins, round_losses, " +
                               "match_wins, match_losses, bombs_planted, bombs_defused, headshots, hits, misses, last_seen";
        const string Values = "@steam_id, @period, @name, @points, @kills, @deaths, @assists, @round_wins, @round_losses, " +
                              "@match_wins, @match_losses, @bombs_planted, @bombs_defused, @headshots, @hits, @misses, @last_seen";
        const string Assignments = "name = @name, points = @points, kills = @kills, deaths = @deaths, " +
                                   "assists = @assists, round_wins = @round_wins, round_losses = @round_losses, " +
                                   "match_wins = @match_wins, match_losses = @match_losses, " +
                                   "bombs_planted = @bombs_planted, bombs_defused = @bombs_defused, " +
                                   "headshots = @headshots, hits = @hits, misses = @misses, last_seen = @last_seen";

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"INSERT INTO {HistoryTable} ({Columns}) VALUES ({Values}) ON DUPLICATE KEY UPDATE {Assignments}"
            : $"INSERT INTO {HistoryTable} ({Columns}) VALUES ({Values}) ON CONFLICT (steam_id, period) DO UPDATE SET {Assignments}";

        AddParameter(command, "@steam_id", (long)row.SteamId);
        AddParameter(command, "@period", row.Period);
        AddParameter(command, "@name", row.Name);
        AddParameter(command, "@points", row.Points);
        AddParameter(command, "@kills", row.Kills);
        AddParameter(command, "@deaths", row.Deaths);
        AddParameter(command, "@assists", row.Assists);
        AddParameter(command, "@round_wins", row.RoundWins);
        AddParameter(command, "@round_losses", row.RoundLosses);
        AddParameter(command, "@match_wins", row.MatchWins);
        AddParameter(command, "@match_losses", row.MatchLosses);
        AddParameter(command, "@bombs_planted", row.BombsPlanted);
        AddParameter(command, "@bombs_defused", row.BombsDefused);
        AddParameter(command, "@headshots", row.Headshots);
        AddParameter(command, "@hits", row.Hits);
        AddParameter(command, "@misses", row.Misses);
        AddParameter(command, "@last_seen", row.LastSeen);

        command.ExecuteNonQuery();
    }

    private void WriteSettings(IDbConnection connection, IDbTransaction transaction, PlayerSnapshotRow row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"INSERT INTO {SettingsTable} (steam_id, show_point_messages) VALUES (@steam_id, @show) " +
              "ON DUPLICATE KEY UPDATE show_point_messages = @show"
            : $"INSERT INTO {SettingsTable} (steam_id, show_point_messages) VALUES (@steam_id, @show) " +
              "ON CONFLICT (steam_id) DO UPDATE SET show_point_messages = @show";

        AddParameter(command, "@steam_id", (long)row.SteamId);
        AddParameter(command, "@show", row.ShowPointMessages);

        command.ExecuteNonQuery();
    }

    private static void Execute(IDbConnection connection, IDbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <remarks>
    /// Generic DbParameter rather than a provider type, so one code path serves all three engines.
    /// '@' is accepted as the marker by MySqlConnector, Npgsql and System.Data.SQLite alike.
    /// </remarks>
    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>What one save pass committed and what it skipped.</summary>
internal sealed record SaveOutcome(
    IReadOnlyList<ulong> Committed,
    IReadOnlyList<(ulong SteamId, Exception Error)> Failed);

/// <summary>An immutable copy of a record, taken on the game thread and written off it.</summary>
/// <remarks>
/// Snapshotting is what makes the writer race-free: the record keeps being mutated by gameplay
/// while the write is in flight, and an absolute write of a half-mutated row would persist a state
/// that never existed.
/// <para>
/// <see cref="Archives"/> compares by reference, deliberately: the record replaces its array on
/// every rollover, so "the same instance" is exactly "no period closed during this write".
/// </para>
/// </remarks>
internal readonly record struct PlayerSnapshotRow(
    ulong SteamId,
    string Name,
    int Points,
    int Kills,
    int Deaths,
    int Assists,
    int RoundWins,
    int RoundLosses,
    int MatchWins,
    int MatchLosses,
    int BombsPlanted,
    int BombsDefused,
    DateTime FirstSeen,
    DateTime LastSeen,
    bool ShowPointMessages,
    int Headshots,
    int Hits,
    int Misses,
    long Period,
    PlayerHistoryRow[]? Archives)
{
    public static PlayerSnapshotRow From(PlayerRecord record) => new(
        record.SteamId, record.Name, record.Points, record.Kills, record.Deaths, record.Assists,
        record.RoundWins, record.RoundLosses, record.MatchWins, record.MatchLosses,
        record.BombsPlanted, record.BombsDefused, record.FirstSeen, record.LastSeen,
        record.ShowPointMessages, record.Headshots, record.Hits, record.Misses,
        record.Period, record.PendingArchives);
}

/// <summary>One closed period's final figures for one player, as <c>ranks_players_history</c> holds them.</summary>
internal readonly record struct PlayerHistoryRow(
    ulong SteamId,
    long Period,
    string Name,
    int Points,
    int Kills,
    int Deaths,
    int Assists,
    int RoundWins,
    int RoundLosses,
    int MatchWins,
    int MatchLosses,
    int BombsPlanted,
    int BombsDefused,
    int Headshots,
    int Hits,
    int Misses,
    DateTime LastSeen)
{
    public static PlayerHistoryRow From(PlayerRecord record) => new(
        record.SteamId, record.Period, record.Name, record.Points, record.Kills, record.Deaths, record.Assists,
        record.RoundWins, record.RoundLosses, record.MatchWins, record.MatchLosses,
        record.BombsPlanted, record.BombsDefused, record.Headshots, record.Hits, record.Misses, record.LastSeen);
}

/// <summary>Sums over a player's closed periods, with how many periods were summed.</summary>
internal readonly record struct PlayerTotals(
    long Periods,
    long Points,
    long Kills,
    long Deaths,
    long Assists,
    long RoundWins,
    long RoundLosses,
    long MatchWins,
    long MatchLosses,
    long BombsPlanted,
    long BombsDefused,
    long Headshots,
    long Hits,
    long Misses);
