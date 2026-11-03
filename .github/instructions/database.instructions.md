---
description: 'Database access and FluentMigrator migrations: connections, table affixes, per-module ledgers, migration IDs. Trigger phrases: database, migration, FluentMigrator, table, schema, VersionInfo, SQL.'
applyTo: '**/{Database,Migrations}/*.cs'
---

# Database

> Part of the Ranks instruction set. `§N` cross-references resolve through the table in [`copilot-instructions.md`](../copilot-instructions.md), which is always loaded; `docs/` references are to the separate requirements and implementation guides.

`Ranks` owns its schema and talks to the database directly. Three engines must
work: **MySQL/MariaDB, PostgreSQL and SQLite**. See the requirements doc §5 for
the schema itself.

### Connections

Named connections are defined in SwiftlyS2's global `configs/database.jsonc`;
the plugin only stores the *name*. It never holds credentials.

```jsonc
{
    "default_connection": "host",
    "connections": {
        "host":  "mysql://user:pass@localhost:3306/database",
        "stats": "postgresql://user:pass@localhost:5432/database",
        "local": "sqlite://data/local.db"
    }
}
```

```csharp
using var connection = Core.Database.GetConnection(cfg.CurrentValue.Database.Connection);
connection.Open();
```

An unknown name silently falls back to the default connection — validate the
name at load and log loudly if it is missing, or a typo quietly writes to the
wrong database.

### ⚠️ Dispatch on `Driver`, never on the connection's CLR type

```csharp
// WRONG — the SQLite branch is dead code.
switch (connection)
{
    case MySqlConnection:  rb.AddMySql5();   break;
    case NpgsqlConnection: rb.AddPostgres(); break;
    case SqliteConnection: rb.AddSQLite();   break;   // never matches
    default: throw new NotSupportedException();
}

// RIGHT
var driver = Core.Database.GetConnectionInfo(name).Driver;   // "mysql" | "postgresql" | "sqlite"
switch (driver)
{
    case "mysql":      rb.AddMySql5();   break;
    case "postgresql": rb.AddPostgres(); break;
    case "sqlite":     rb.AddSQLite();   break;
    default: throw new NotSupportedException($"Unsupported driver: {driver}");
}
```

SwiftlyS2 builds SQLite connections from **`System.Data.SQLite`**, not
`Microsoft.Data.Sqlite`. A `case SqliteConnection:` is CLR *type identity*, so it
never matches, falls through to `default:` and throws — breaking every fresh
SQLite install while MySQL and PostgreSQL look fine.

### Portability rules

These are the ones that bite:

- **No identifier named `rank`.** Reserved in MySQL 8.
- **Upserts must be a single statement against a real PK/unique constraint.**
  `ON DUPLICATE KEY UPDATE col = VALUES(col)` on MySQL/MariaDB (MariaDB has no
  `AS new`), `ON CONFLICT (pk) DO UPDATE SET col = excluded.col` on
  PostgreSQL/SQLite. "UPDATE, then INSERT on zero rows, catch duplicate-key" is
  **not** portable — a failed statement aborts the surrounding PostgreSQL
  transaction.
- **A missing unique constraint fails differently per engine.** MySQL's
  `ON DUPLICATE KEY UPDATE` silently inserts duplicates forever; PostgreSQL and
  SQLite throw. Every upserted table needs its natural key declared.
- **SQLite cannot `ALTER COLUMN`** and rejects `ADD CONSTRAINT … PRIMARY KEY`.
  Declare multi-column primary keys inline on `Create.Table`, which works on all
  three.
- **Migration runners abort at the first failure**, so one engine-specific
  migration failing hides every migration after it.
- **Verify each engine end-to-end.** A migration that no-ops because of a
  mismatched database-identifier string records as applied having done nothing,
  with no error. Fresh *and* existing databases, all three engines.

### Schema versioning — FluentMigrator

Full rules in the requirements doc, FR-DB-05. The five that cause silent damage:

1. **The version ledger must carry the configured prefix/suffix.** Otherwise the
   first server to boot records every migration, and the second server — with a
   different prefix — sees them as applied, creates nothing, and fails every
   query. Supply it via `IVersionTableMetaData`; `DefaultVersionTableMetaData`
   cannot be cheaply subclassed in 8.x.
2. **`IfDatabase("Postgres")` matches nothing** — no error, the block is skipped
   and the migration records as applied. The working aliases are `MySql5` /
   `MariaDB` / `MySql`, **`PostgreSQL`**, and `SQLite`. Never match on
   `DatabaseType`; it is version-stamped and moves with releases.
3. **Guard each object individually, not the migration as a whole.** One
   `if (Schema.Table(x).Exists()) return;` covering a table *and* its indexes
   *and* a second table skips the rest while still recording the migration as
   applied — permanently, since `MigrateUp()` never retries.
4. **Except `Create.PrimaryKey`**, which must not be guarded that way: MySQL
   names every PK `PRIMARY`, so the existence check is always false and the
   `ADD CONSTRAINT` then fails. Declare composite keys inline on `Create.Table`.
5. **Pass the runner an unopened connection and dispose its `ServiceProvider`.**
   MySqlConnector strips the password from `ConnectionString` on open; a leaked
   provider holds the SQLite file open across hot-reload.

A migration failure must not disable a working database — shared hosting often
grants DML but not DDL. Run migrations, keep the exception, then probe
`SELECT 1 FROM {table} WHERE 1 = 0` unconditionally and decide from the probe.

Table names go through the affix resolver (requirements doc FR-DB-03) —
including index, constraint and version-table names, and including tables owned
by modules, which obtain names via `IRanksApi.ResolveTableName`. Index names are
per-table on MySQL but schema-scoped on PostgreSQL and database-scoped on
SQLite, so an unaffixed index name works on one engine and collides on the other
two. Anything that builds a name by string concatenation will drift.

### One connection, one ledger per plugin — one runner, owned by core

Core and every module share the connection from `Database.Connection`, and each
plugin gets its own ledger table. **Core runs every migration**: a module calls
`RegisterMigrations(assembly, pluginId)` from the shared-interface handshake and
core builds the runner, derives the ledger name and rewrites table names with
the configured affixes.

That ownership is the enforcement point. FluentMigrator has no table-name
convention, so while a module owned its own runner every affix helper core could
offer was opt-in — a convention, not a guarantee. It also fixes a timing
contradiction: a module's `Load()` runs before the handshake, so it never had an
`IRanksApi` to ask for a name at the moment it needed one. Core migrates its own
schema in `Load()` and registered module assemblies in `OnAllPluginsLoaded()`,
each inside its own try/catch so one bad module cannot take the schema down.

Core never scans the module assembly it was handed. A module ships its own
FluentMigrator — SwiftlyS2's load-time `GetTypes()` resolves its `Migration`
subclasses before core is involved — and that copy's `IMigration` is not the one
core's runner matches. `ModuleMigrationContext` loads the module's DLL a second
time with every `FluentMigrator*` reference bound to core's copy, and core
migrates that. **Never put FluentMigrator in `resources/exports`**: SwiftlyS2
forces every export onto every plugin, replacing the version each one ships —
VIPCore 6.2 then fails with `Method 'get_Quoter' … does not have an implementation`.
Private copies cannot leak that way, in either load order: SwiftlyS2's context
cannot adopt an assembly from a plugin's collectible context.

The official `SwiftlyS2-Plugins/Admins` shares one stock `VersionInfo` across
three modules and gets away with it only by hand-coordinating migration IDs into
one sequence across three folders. A colliding third-party ID is then recorded
as applied and its table is **never created**, silently. See FR-DB-05.

Core avoids that: `TableNames.LedgerFor(pluginId)` gives every module its own
`AffixedVersionTable`, so **migration IDs only need to be unique within a module**
and two modules may share one. `Ranks_EloPoints` and `Ranks_CustomCombos` both use
`202610261200` today, which is fine and is not something to "fix".

### ⚠️ Dommel's table-name resolver is process-wide

`[Table("…")]` is compile-time, so prefix support means
`DommelMapper.SetTableNameResolver(…)` — which is **static, per-process, and
shared with every other plugin using Dommel in that host**. `Admins` does, and
will be installed alongside this one.

A resolver that prefixes unconditionally renames `bans` to `awp_bans` and breaks
that plugin with no error on either side. Delegate to
`DefaultTableNameResolver` and prefix **only types from this assembly**:

```csharp
public string ResolveTableName(Type type) =>
    type.Assembly == typeof(PlayerRecord).Assembly
        ? TableNames.Resolve(_inner.ResolveTableName(type))
        : _inner.ResolveTableName(type);
```

Install it before the first query — `Resolvers.TypeTableNameCache` caches per
type on first use, so a late install leaves already-touched entities unprefixed.

### Access patterns

- All queries run **off the game thread** and marshal results back via the
  scheduler (§9).
- Writes are **absolute** and batched by a single writer; a record is marked
  saved only after the write commits.
- Each player is isolated behind a savepoint, so one bad row skips that player
  rather than aborting the whole batch.
- `using var connection = …` always. Dispose any migration-runner service
  provider too — a leaked processor holds the SQLite file open across hot
  reload.

---

