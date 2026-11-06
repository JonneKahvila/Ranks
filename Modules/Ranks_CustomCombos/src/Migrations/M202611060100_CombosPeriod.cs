using FluentMigrator;

namespace Ranks.CustomCombos.Migrations;

/// <summary>Per-period combo counts, kept beside the lifetime table. Core runs this migration, not the module.</summary>
/// <remarks>
/// <c>ranks_combos</c>'s columns plus <c>period</c>, which joins the primary key: the writer adds
/// into both tables, and each upsert needs a key to conflict on (FR-DB-10). The lifetime table is
/// left exactly as it was, because other plugins read it as lifetime totals.
///
/// No secondary index: every query against this table leads with <c>steam_id</c>, which the
/// primary key already covers.
/// </remarks>
[Migration(202611060100)]
public sealed class M202611060100_CombosPeriod : Migration
{
    private const string CombosPeriod = "ranks_combos_period";

    /// <remarks>
    /// The composite key is declared inline for the reason <c>M202610261200_Combos</c> gives: MySQL
    /// names every primary key PRIMARY, so a guarded <c>Create.PrimaryKey</c> can never see its own
    /// key, and inline is the only composite key SQLite accepts.
    /// </remarks>
    public override void Up()
    {
        if (!Schema.Table(CombosPeriod).Exists())
            Create.Table(CombosPeriod)
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("combo_key").AsString(64).NotNullable().PrimaryKey()
                .WithColumn("period").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("count").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("last_at").AsDateTime().NotNullable();
    }

    /// <remarks>Empty: the table holds player data, and downgrade is not a supported operation.</remarks>
    public override void Down()
    {
    }
}
