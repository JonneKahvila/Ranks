using FluentMigrator;

namespace Ranks.WeaponStats.Migrations;

/// <summary>The per-period copies of this module's three tables (FR-DB-10). Core runs this migration, not the module.</summary>
/// <remarks>
/// Same columns as the all-time tables, keyed by their key plus <c>period</c> and declared inline for
/// the reasons in <c>M202610121200</c>. The all-time tables are not touched: CustomHUD and the
/// importer read them as lifetime totals, so seasons sit beside them rather than inside them.
/// Nothing is backfilled — a period holds only what was written while core's periods were on.
/// </remarks>
[Migration(202611060100)]
public sealed class M202611060100_PeriodTables : Migration
{
    private const string WeaponStatsPeriod = "ranks_weapon_stats_period";
    private const string HitStatsPeriod = "ranks_hit_stats_period";
    private const string WeaponHitStatsPeriod = "ranks_weapon_hit_stats_period";

    public override void Up()
    {
        if (!Schema.Table(WeaponStatsPeriod).Exists())
            Create.Table(WeaponStatsPeriod)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("weapon").AsString(64).NotNullable().PrimaryKey()
                .WithColumn("period").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("shots").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("hits").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("misses").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("damage").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("kills").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("deaths").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("headshots").AsInt64().NotNullable().WithDefaultValue(0);

        if (!Schema.Table(HitStatsPeriod).Exists())
            Create.Table(HitStatsPeriod)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("hitgroup").AsInt16().NotNullable().PrimaryKey()
                .WithColumn("period").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("hits").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("damage").AsInt64().NotNullable().WithDefaultValue(0);

        if (!Schema.Table(WeaponHitStatsPeriod).Exists())
            Create.Table(WeaponHitStatsPeriod)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("weapon").AsString(64).NotNullable().PrimaryKey()
                .WithColumn("hitgroup").AsInt16().NotNullable().PrimaryKey()
                .WithColumn("period").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("hits").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("damage").AsInt64().NotNullable().WithDefaultValue(0);
    }

    /// <remarks>Empty: the tables hold player data, and downgrade is not a supported operation.</remarks>
    public override void Down()
    {
    }
}
