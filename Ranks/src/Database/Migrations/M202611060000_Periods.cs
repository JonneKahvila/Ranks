using FluentMigrator;

namespace Ranks.Database.Migrations;

/// <summary>Stamps the player row with its period and adds the table closed periods move to (FR-DB-10).</summary>
/// <remarks>
/// The row keeps <c>steam_id</c> as its only key: <c>ranks_players</c> is a fixed contract that
/// modules and the importer read, and SQLite cannot alter a primary key in place. History gets a
/// table of its own with the composite key declared inline, the only portable form.
///
/// The column defaults to 0, <c>RanksPeriods.Initial</c>, so every existing row lands in the period
/// that predates the feature rather than in one it never belonged to.
/// </remarks>
[Migration(202611060000)]
public sealed class M202611060000_Periods : Migration
{
    private const string Players = "ranks_players";
    private const string History = "ranks_players_history";
    private const string PlayersPeriodIndex = "ix_ranks_players_period_points";
    private const string HistoryPeriodIndex = "ix_ranks_players_history_period_points";

    public override void Up()
    {
        if (!Schema.Table(Players).Column("period").Exists())
            Alter.Table(Players).AddColumn("period").AsInt64().NotNullable().WithDefaultValue(0);

        if (!Schema.Table(Players).Index(PlayersPeriodIndex).Exists())
            Create.Index(PlayersPeriodIndex).OnTable(Players)
                .OnColumn("period").Ascending()
                .OnColumn("points").Ascending();

        if (!Schema.Table(History).Exists())
            Create.Table(History)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("period").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("name").AsString(128).NotNullable().WithDefaultValue(string.Empty)
                .WithColumn("points").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("kills").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("deaths").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("assists").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("round_wins").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("round_losses").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("match_wins").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("match_losses").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("bombs_planted").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("bombs_defused").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("headshots").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("hits").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("misses").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("last_seen").AsDateTime().NotNullable();

        if (!Schema.Table(History).Index(HistoryPeriodIndex).Exists())
            Create.Index(HistoryPeriodIndex).OnTable(History)
                .OnColumn("period").Ascending()
                .OnColumn("points").Ascending();
    }

    /// <remarks>Drops the indexes only — the column and the table hold player data (FR-DB-05 req. 6).</remarks>
    public override void Down()
    {
        if (Schema.Table(History).Index(HistoryPeriodIndex).Exists())
            Delete.Index(HistoryPeriodIndex).OnTable(History);

        if (Schema.Table(Players).Index(PlayersPeriodIndex).Exists())
            Delete.Index(PlayersPeriodIndex).OnTable(Players);
    }
}
