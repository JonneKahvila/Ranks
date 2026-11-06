using System.Data;
using Microsoft.Extensions.Logging;

namespace Ranks.Database;

/// <summary>Result of the boot-time readability check.</summary>
internal sealed record SchemaStatus(bool Usable, IReadOnlyList<string> UnusableTables);

/// <summary>Decides whether the schema is usable, independently of whether migrations succeeded.</summary>
/// <remarks>
/// Shared hosting commonly grants DML but not DDL, so a catch-all that disabled the plugin on any
/// migration failure would kill it on those hosts even when every table exists and works. This is
/// the opposite of the response to an unopenable connection, and the difference is what is still
/// usable: a reachable database with a failed migration can still read and write.
/// </remarks>
internal sealed class SchemaProbe
{
    private readonly DatabaseConnection _database;
    private readonly TableNames _tables;
    private readonly ILogger _logger;

    public SchemaProbe(DatabaseConnection database, TableNames tables, ILogger logger)
    {
        _database = database;
        _tables = tables;
        _logger = logger;
    }

    /// <summary>Probes every required table and reports what, if anything, is unreadable.</summary>
    /// <remarks>
    /// Runs unconditionally, even when migrations reported success — that is what turns "an
    /// exception on every player connect" into one line at boot. It opens the connection, so it
    /// must run after the migration runner, which needs an unopened one.
    /// <para>
    /// Each entry names the columns to select as well as the table: a column migration a DML-only
    /// host could not run leaves a readable table that still fails every query naming the column.
    /// </para>
    /// </remarks>
    public SchemaStatus Probe(IReadOnlyCollection<(string BaseName, string Columns)> requiredReads, Exception? schemaError)
    {
        var unusable = new List<string>();

        using var connection = _database.Create();
        connection.Open();

        foreach (var (baseName, columns) in requiredReads)
        {
            var table = _tables.Resolve(baseName);

            if (IsReadable(connection, table, columns, out var failure)) continue;

            unusable.Add(table);
            _logger.LogError(failure, "Ranks: table '{Table}' is not readable.", table);
        }

        if (unusable.Count > 0)
        {
            // Logged before the summary and only here: on the success path a failed migration is a
            // footnote, but when a table is missing it is almost always the whole story, and the
            // summary below can only guess at permissions.
            if (schemaError is not null)
                _logger.LogError(
                    schemaError,
                    "Ranks: migrations failed, which is why the table(s) below are missing.");

            _logger.LogError(
                "Ranks: {Count} table(s) unusable ({Tables}). The plugin will not record anything. " +
                "If migrations failed with a permission error, grant CREATE/ALTER on this database, " +
                "or create the tables by hand and restart.",
                unusable.Count, string.Join(", ", unusable));

            return new SchemaStatus(false, unusable);
        }

        if (schemaError is not null)
            _logger.LogWarning(
                schemaError,
                "Ranks: migrations failed but every required table is readable — continuing. " +
                "This is the expected shape on a host that grants DML but not DDL{Hint}.",
                LooksLikePermissionDenied(schemaError) ? " (the error looks like a denied permission)" : string.Empty);

        return new SchemaStatus(true, []);
    }

    /// <remarks>
    /// Provider-agnostic and tests readability, which is what the plugin actually needs — better
    /// than an information_schema query, which can succeed on a table the account cannot select.
    /// </remarks>
    private static bool IsReadable(IDbConnection connection, string table, string columns, out Exception? failure)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {columns} FROM {table} WHERE 1 = 0";
            command.ExecuteNonQuery();

            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    /// <remarks>
    /// Walks InnerException because FluentMigrator wraps several layers deep. MySQL 1045 is
    /// deliberately absent — it is a bad password and fails at Open(), long before here.
    /// </remarks>
    private static bool LooksLikePermissionDenied(Exception error)
    {
        for (var ex = error; ex is not null; ex = ex.InnerException)
        {
            var text = ex.Message;

            if (text.Contains("42501", StringComparison.Ordinal)) return true;
            if (text.Contains("permission denied", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("command denied", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("access denied", StringComparison.OrdinalIgnoreCase)) return true;

            foreach (var code in (ReadOnlySpan<string>)["1044", "1142", "1143", "1227"])
                if (text.Contains(code, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
