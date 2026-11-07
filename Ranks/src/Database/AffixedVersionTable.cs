using FluentMigrator.Runner.VersionTableInfo;

namespace Ranks.Database;

/// <summary>The migration ledger, scoped to one plugin and one affix set.</summary>
/// <remarks>
/// Takes the bare ledger name: AffixingProcessor affixes it and its index like any other table.
/// Implemented directly rather than subclassing DefaultVersionTableMetaData, whose only
/// constructor in FluentMigrator 8.x is (IConventionSet, IOptions&lt;RunnerOptions&gt;).
/// </remarks>
internal sealed class AffixedVersionTable : IVersionTableMetaData
{
    public AffixedVersionTable(string ledgerName)
    {
        TableName = ledgerName;
        UniqueIndexName = TableNames.Fit($"ux_{ledgerName}_version");
    }

    public string TableName { get; }

    /// <remarks>
    /// Derived from the ledger, never the stock constant UC_Version. Index names are per-table on
    /// MySQL but per-schema on PostgreSQL and database-wide on SQLite, so with one ledger per
    /// plugin a fixed name makes the second plugin to migrate fail outright on two of the three
    /// engines. Capped independently of the table name — it is eleven characters longer, so it
    /// overflows PostgreSQL's 63 first.
    /// </remarks>
    public string UniqueIndexName { get; }

    public string ColumnName => "version";

    public string AppliedOnColumnName => "applied_on";

    public string DescriptionColumnName => "description";

    public string SchemaName => string.Empty;

    public bool OwnsSchema => true;

    /// <remarks>False is what makes FluentMigrator create a unique index here rather than a primary key.</remarks>
    public bool CreateWithPrimaryKey => false;
}
