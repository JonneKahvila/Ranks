using FluentMigrator;

namespace Ranks.Database.Migrations;

/// <summary>Widens the player name columns to utf8mb4 on MySQL and MariaDB.</summary>
/// <remarks>
/// <c>AsString</c> is emitted as <c>NVARCHAR</c>, which both engines always create as utf8mb3
/// whatever the table default, so a name with emoji or "fancy font" letters fails with
/// "Incorrect string value" and that player is never saved. PostgreSQL and SQLite store any
/// UTF-8 already. A new migration rather than an edit: the originals are recorded as applied.
/// </remarks>
[Migration(202611070000)]
public sealed class M202611070000_PlayerNamesUtf8mb4 : Migration
{
    private const string NameColumn = "name";

    /// <remarks>
    /// Collation named explicitly: the server default varies (MariaDB 11.5+ picks uca1400), and
    /// both tables must agree so the roll-up and lookups never meet a collation mismatch.
    /// </remarks>
    private const string Utf8mb4Name = "VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";

    public override void Up()
    {
        foreach (var table in (ReadOnlySpan<string>)["ranks_players", "ranks_players_history"])
            if (Schema.Table(table).Column(NameColumn).Exists())
                IfDatabase("MySql5", "MariaDB", "MySql")
                    .Alter.Table(table)
                    .AlterColumn(NameColumn).AsCustom(Utf8mb4Name).NotNullable().WithDefaultValue(string.Empty);
    }

    /// <remarks>Empty: narrowing back to utf8mb3 would fail on, or corrupt, the names this exists to store.</remarks>
    public override void Down()
    {
    }
}
