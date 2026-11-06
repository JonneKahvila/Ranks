using FluentMigrator;

namespace Ranks.EloPoints.Migrations;

/// <summary>Stamps each rating with its period and adds the table closed periods move to (FR-DB-10).</summary>
/// <remarks>
/// The rating row keeps <c>steam_id</c> as its only key and holds the current period, as
/// <c>ranks_players</c> does in core; SQLite cannot alter a primary key in place, so history gets a
/// table of its own with the composite key declared inline, the only portable form.
///
/// The column defaults to 0, <c>RanksPeriods.Initial</c> — core's current period on any install that
/// never enabled periods — so every existing rating stays current and nothing changes until they are.
/// </remarks>
[Migration(202611060100)]
public sealed class M202611060100_Periods : Migration
{
    private const string Rating = "ranks_elopoints";
    private const string History = "ranks_elopoints_history";
    private const string HistoryPeriodIndex = "ix_ranks_elopoints_history_period";

    public override void Up()
    {
        if (!Schema.Table(Rating).Column("period").Exists())
            Alter.Table(Rating).AddColumn("period").AsInt64().NotNullable().WithDefaultValue(0);

        if (!Schema.Table(History).Exists())
            Create.Table(History)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("period").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("rating").AsDecimal(10, 4).NotNullable()
                .WithColumn("matches").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("abandons").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("updated_at").AsDateTime().NotNullable();

        if (!Schema.Table(History).Index(HistoryPeriodIndex).Exists())
            Create.Index(HistoryPeriodIndex).OnTable(History)
                .OnColumn("period").Ascending();
    }

    /// <remarks>Drops the index only — the column and the table hold player data (FR-DB-05 req. 6).</remarks>
    public override void Down()
    {
        if (Schema.Table(History).Index(HistoryPeriodIndex).Exists())
            Delete.Index(HistoryPeriodIndex).OnTable(History);
    }
}
