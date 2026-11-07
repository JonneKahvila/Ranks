using FluentMigrator;

namespace Ranks.Database.Migrations;

/// <summary>Adds seconds played to the player row and to closed periods, indexed for the playtime board.</summary>
/// <remarks>
/// <c>BIGINT</c> seconds, the shape LVL Ranks and K4-LevelRanks both store, so an imported total
/// lands unconverted. The default backfills existing rows with 0: time played before the column
/// existed was never measured. History is not indexed — a past season's board filters on
/// <c>period</c>, which its existing index already leads with.
/// </remarks>
[Migration(202611072300)]
public sealed class M202611072300_Playtime : Migration
{
    private const string Column = "playtime";
    private const string Players = "ranks_players";
    private const string PlayersPlaytimeIndex = "ix_ranks_players_period_playtime";

    public override void Up()
    {
        foreach (var table in (ReadOnlySpan<string>)[Players, "ranks_players_history"])
            if (!Schema.Table(table).Column(Column).Exists())
                Alter.Table(table).AddColumn(Column).AsInt64().NotNullable().WithDefaultValue(0);

        if (!Schema.Table(Players).Index(PlayersPlaytimeIndex).Exists())
            Create.Index(PlayersPlaytimeIndex).OnTable(Players)
                .OnColumn("period").Ascending()
                .OnColumn(Column).Ascending();
    }

    /// <remarks>Drops the index only — the columns hold player data (FR-DB-05 req. 6).</remarks>
    public override void Down()
    {
        if (Schema.Table(Players).Index(PlayersPlaytimeIndex).Exists())
            Delete.Index(PlayersPlaytimeIndex).OnTable(Players);
    }
}
