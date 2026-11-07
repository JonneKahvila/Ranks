using System.Data;
using FluentMigrator;
using FluentMigrator.Expressions;
using FluentMigrator.Runner.Processors;

namespace Ranks.Database;

/// <summary>Rewrites every table, index and constraint name with the operator's affixes.</summary>
/// <remarks>
/// FluentMigrator has no table-name convention — IConventionSet covers constraint, index,
/// foreign-key, primary-key, schema and auto-script names only — so IMigrationProcessor is the
/// only enforcement point (FR-DB-05 req. 10). Miss one expression and that statement targets an
/// unaffixed table, which on a prefixed server silently operates on nothing or on another
/// server's data.
/// </remarks>
internal sealed class AffixingProcessor : IMigrationProcessor
{
    private readonly IMigrationProcessor _inner;
    private readonly TableNames _tables;

    public AffixingProcessor(IMigrationProcessor inner, TableNames tables)
    {
        _inner = inner;
        _tables = tables;
    }

    public string DatabaseType => _inner.DatabaseType;

    public IList<string> DatabaseTypeAliases => _inner.DatabaseTypeAliases;

    // ---- IQuerySchema ---------------------------------------------------------
    //
    // These are not expressions: Schema.Table("x").Exists() calls the processor directly, so a
    // decorator covering only Process(...) answers for the unaffixed name. The failure is silent
    // and permanent in exactly the setup FR-DB-03 exists for — a default install and an awp_ one
    // in one database, where the guard finds the default's table, skips creation, and records the
    // migration as applied.

    public bool SchemaExists(string schemaName) => _inner.SchemaExists(schemaName);

    public bool TableExists(string schemaName, string tableName) =>
        _inner.TableExists(schemaName, Affix(tableName));

    public bool ColumnExists(string schemaName, string tableName, string columnName) =>
        _inner.ColumnExists(schemaName, Affix(tableName), columnName);

    public bool ConstraintExists(string schemaName, string tableName, string constraintName) =>
        _inner.ConstraintExists(schemaName, Affix(tableName), Affix(constraintName));

    public bool IndexExists(string schemaName, string tableName, string indexName) =>
        _inner.IndexExists(schemaName, Affix(tableName), Affix(indexName));

    public bool SequenceExists(string schemaName, string sequenceName) =>
        _inner.SequenceExists(schemaName, Affix(sequenceName));

    public bool DefaultValueExists(string schemaName, string tableName, string columnName, object defaultValue) =>
        _inner.DefaultValueExists(schemaName, Affix(tableName), columnName, defaultValue);

    // ---- Raw SQL --------------------------------------------------------------
    //
    // Execute/Read/Exists carry no expression tree, so nothing here can reach inside them. This is
    // the documented gap behind the post-migration verification in SchemaMigrator.

    public void Execute(string template) => _inner.Execute(template);

    public void Execute(string template, params object[] args) => _inner.Execute(template, args);

    public DataSet Read(string template, params object[] args) => _inner.Read(template, args);

    public bool Exists(string template, params object[] args) => _inner.Exists(template, args);

    public DataSet ReadTableData(string schemaName, string tableName) =>
        _inner.ReadTableData(schemaName, Affix(tableName));

    public void BeginTransaction() => _inner.BeginTransaction();

    public void CommitTransaction() => _inner.CommitTransaction();

    public void RollbackTransaction() => _inner.RollbackTransaction();

    // ---- Expressions ----------------------------------------------------------

    public void Process(CreateSchemaExpression expression) => _inner.Process(expression);

    public void Process(DeleteSchemaExpression expression) => _inner.Process(expression);

    public void Process(AlterSchemaExpression expression) => _inner.Process(expression);

    public void Process(CreateTableExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        foreach (var column in expression.Columns) AffixPrimaryKey(column);
        _inner.Process(expression);
    }

    public void Process(DeleteTableExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        _inner.Process(expression);
    }

    public void Process(AlterTableExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        _inner.Process(expression);
    }

    /// <remarks>Both names, or a rename moves an affixed table to an unaffixed one.</remarks>
    public void Process(RenameTableExpression expression)
    {
        expression.OldName = Affix(expression.OldName);
        expression.NewName = Affix(expression.NewName);
        _inner.Process(expression);
    }

    public void Process(CreateColumnExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        AffixPrimaryKey(expression.Column);
        _inner.Process(expression);
    }

    public void Process(AlterColumnExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        AffixPrimaryKey(expression.Column);
        _inner.Process(expression);
    }

    public void Process(DeleteColumnExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        _inner.Process(expression);
    }

    public void Process(RenameColumnExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        _inner.Process(expression);
    }

    public void Process(CreateIndexExpression expression)
    {
        AffixIndex(expression.Index);
        _inner.Process(expression);
    }

    public void Process(DeleteIndexExpression expression)
    {
        AffixIndex(expression.Index);
        _inner.Process(expression);
    }

    public void Process(CreateConstraintExpression expression)
    {
        AffixConstraint(expression.Constraint);
        _inner.Process(expression);
    }

    public void Process(DeleteConstraintExpression expression)
    {
        AffixConstraint(expression.Constraint);
        _inner.Process(expression);
    }

    public void Process(CreateForeignKeyExpression expression)
    {
        AffixForeignKey(expression.ForeignKey);
        _inner.Process(expression);
    }

    public void Process(DeleteForeignKeyExpression expression)
    {
        AffixForeignKey(expression.ForeignKey);
        _inner.Process(expression);
    }

    public void Process(InsertDataExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        _inner.Process(expression);
    }

    public void Process(DeleteDataExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        _inner.Process(expression);
    }

    public void Process(UpdateDataExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        _inner.Process(expression);
    }

    public void Process(AlterDefaultConstraintExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        _inner.Process(expression);
    }

    public void Process(DeleteDefaultConstraintExpression expression)
    {
        expression.TableName = Affix(expression.TableName);
        _inner.Process(expression);
    }

    public void Process(CreateSequenceExpression expression)
    {
        expression.Sequence.Name = Affix(expression.Sequence.Name);
        _inner.Process(expression);
    }

    public void Process(DeleteSequenceExpression expression)
    {
        expression.SequenceName = Affix(expression.SequenceName);
        _inner.Process(expression);
    }

    public void Process(PerformDBOperationExpression expression) => _inner.Process(expression);

    public void Dispose() => _inner.Dispose();

    /// <remarks>
    /// Index names are per-table on MySQL but schema-scoped on PostgreSQL and database-scoped on
    /// SQLite, so an unaffixed index on both ranks_players and awp_ranks_players works on one
    /// engine and collides on the other two.
    /// </remarks>
    private void AffixIndex(FluentMigrator.Model.IndexDefinition index)
    {
        index.TableName = Affix(index.TableName);
        index.Name = Affix(index.Name);
    }

    private void AffixConstraint(FluentMigrator.Model.ConstraintDefinition constraint)
    {
        constraint.TableName = Affix(constraint.TableName);
        constraint.ConstraintName = Affix(constraint.ConstraintName);
    }

    /// <remarks>
    /// FluentMigrator names a column's primary key from the bare table name before this sees it.
    /// PostgreSQL keeps constraint names schema-wide, so a second affix set in one database failed
    /// CREATE TABLE with <c>relation "PK_ranks_players" already exists</c> (FR-DB-03).
    /// </remarks>
    private void AffixPrimaryKey(FluentMigrator.Model.ColumnDefinition column)
    {
        if (column.IsPrimaryKey) column.PrimaryKeyName = Affix(column.PrimaryKeyName);
    }

    private void AffixForeignKey(FluentMigrator.Model.ForeignKeyDefinition foreignKey)
    {
        foreignKey.ForeignTable = Affix(foreignKey.ForeignTable);
        foreignKey.PrimaryTable = Affix(foreignKey.PrimaryTable);
        foreignKey.Name = Affix(foreignKey.Name);
    }

    /// <remarks>
    /// Null and empty pass through untouched: FluentMigrator leaves auto-generated index and
    /// constraint names unset until its own convention set fills them in, and affixing an empty
    /// string would turn that into a bare "prefix_suffix" identifier.
    /// </remarks>
    private string Affix(string name) =>
        string.IsNullOrEmpty(name) ? name : _tables.Resolve(name);
}

/// <summary>Wraps whichever accessor the engine registered so the runner sees the affixed processor.</summary>
/// <remarks>IProcessorAccessor is the single seam: MigrationRunner reads its Processor property.</remarks>
internal sealed class AffixingProcessorAccessor : IProcessorAccessor
{
    public AffixingProcessorAccessor(IProcessorAccessor inner, TableNames tables) =>
        Processor = new AffixingProcessor(inner.Processor, tables);

    public IMigrationProcessor Processor { get; }
}
