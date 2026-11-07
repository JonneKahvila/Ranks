using System.Data;

namespace Ranks.Database;

/// <summary>What <see cref="DatabaseTables.RenameIfOnlyOld"/> found.</summary>
internal enum TableRename
{
    None,
    Renamed,
    BothExist,
}

/// <summary>The little DDL core runs outside FluentMigrator, dispatched on the configured driver.</summary>
internal static class DatabaseTables
{
    /// <summary>Every table in the connection's database, or its current schema, compared case-insensitively.</summary>
    public static HashSet<string> List(IDbConnection connection, string driver)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ListQuery(driver) is not { } sql) return names;

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0))
                names.Add(reader.GetString(0));

        return names;
    }

    /// <summary>Renames <paramref name="from"/> to <paramref name="to"/> when only the first exists.</summary>
    /// <remarks>
    /// A failed rename is checked again before it is reported: servers sharing a database often
    /// restart together, and the one that loses the race finds the work already done.
    /// </remarks>
    public static TableRename RenameIfOnlyOld(IDbConnection connection, string driver, string from, string to)
    {
        var tables = List(connection, driver);
        if (!tables.Contains(from)) return TableRename.None;
        if (tables.Contains(to)) return TableRename.BothExist;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE {Quote(driver, from)} RENAME TO {Quote(driver, to)}";
            command.ExecuteNonQuery();
        }
        catch
        {
            var after = List(connection, driver);
            if (after.Contains(to) && !after.Contains(from)) return TableRename.None;

            throw;
        }

        return TableRename.Renamed;
    }

    private static string? ListQuery(string driver) => driver switch
    {
        "mysql" or "mariadb" => "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()",
        "postgresql" or "postgres" or "pgsql" => "SELECT tablename FROM pg_tables WHERE schemaname = current_schema()",
        "sqlite" => "SELECT name FROM sqlite_master WHERE type = 'table'",
        _ => null,
    };

    /// <remarks>Quoted as FluentMigrator created them: PostgreSQL folds an unquoted name to lowercase.</remarks>
    private static string Quote(string driver, string identifier) =>
        driver is "mysql" or "mariadb"
            ? $"`{identifier.Replace("`", "``")}`"
            : $"\"{identifier.Replace("\"", "\"\"")}\"";
}
