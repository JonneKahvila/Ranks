# Ranks — Implementation Guide

How to build what [`functional-requirements.md`](./functional-requirements.md)
specifies. That document is the authority on **what**; this one covers **how**,
and defers to it on every behavioural question.

Three documents, three jobs:

| Document | Answers |
|---|---|
| `functional-requirements.md` | What the plugin must do, and why each decision was taken |
| `implementation-guide.md` *(this)* | How to build it against SwiftlyS2 1.4.13 |
| `.github/copilot-instructions.md` | Framework rules and traps that apply to any work in this repo |
| `.github/instructions/*.instructions.md` | The same, narrowed to one area — menus, database, commands, config, translations, shared interfaces, release. An editor loads these automatically when the matching file is open; otherwise open the one you need |
| `AGENTS.md` | Build and verify commands, for a tool that does not read `.github/` |

---

## 0. API validation status

Everything in this guide that names a SwiftlyS2 type has been checked against
**two independent sources**:

1. **The stable API documentation and developer guides** at `swiftlys2.net`,
   via the SwiftlyS2 docs MCP and the published guide pages — audited
   2026-10-12.
2. **Reflection over the shipping assembly**, which is the exact code that will
   run:
   `~/.nuget/packages/swiftlys2.cs2/1.4.13/lib/net10.0/SwiftlyS2.CS2.dll`

3. **The game-events and schema dumps**, for anything the C# API docs cannot
   cover — source-generated event wrappers like `EventPlayerDeath` are not in
   the API docs, but the events they are generated from are.

Verified signatures are in [Appendix A](#appendix-a--verified-api-surface);
points where the two sources **disagree** are in
[Appendix B](#appendix-b--stable-docs-vs-1413).

**Two things are still unverified and are marked ⚠️ where they appear.** Do not
treat them as settled:

| Item | Why it is open |
|---|---|
| `m_iCompetitiveRankType` mode values | The field is confirmed (networked `signed char` on `CCSPlayerController`, from the CS2 schema dump) but the engine's enum values are not. A wrong value renders nothing, silently. |
| Whether the scoring events still **fire** | Every event and field core scores on is confirmed *declared* (below). Declaration is not delivery — see FR-PTS-01. |

**Confirmed since the last audit**, against the live game-events and schema
dumps — these are no longer open:

| Claim | Result |
|---|---|
| `noscope`, `thrusmoke`, `penetrated`, `attackerblind` exist on `player_death` | ✅ all four, `core.gameevents` + `mod.gameevents` |
| `IGameEventAccessor.GetBool` / `GetInt32` exist | ✅ both, plus 20 more typed accessors |
| `cs_win_panel_match` exists | ✅ declared — but **0 fields**, so the winner must be derived (§6.4) |
| `CTeam.Score` / `.PlayerControllers` are networked and inherited by `CCSTeam` | ✅ both |
| An entity-class lookup exists | ✅ `IEntitySystemService.GetAllEntitiesByClass<T>()` |

**The project floats `SwiftlyS2.CS2` to `*-*` — the newest version, prereleases
included.** `Core.Menu` (`IMenuService`), the menu framework FR-CMD-06 requires,
ships only in the 1.5.0 prereleases; the classic `Core.MenusAPI` family it
replaces is already `[Obsolete]` there, pointing at `Core.Menu` by name. A fixed
`1.5.0` would not necessarily resolve at all, and building against 1.4.13 would
mean shipping against an API marked for removal.

> **This trades CI reproducibility for access to the API, deliberately.** A
> floating range means two builds of the same commit can resolve different
> framework versions, which is exactly what a pin exists to prevent. The
> mitigations are to **record the resolved `SwiftlyS2.CS2` version in the
> release notes** — otherwise a bug report cannot be tied to a framework build
> — and to pin to the first stable 1.5.x once it exists.

> [!WARNING]
> **The `Microsoft.Extensions.*` references must float too — `10.*`.**
> SwiftlyS2 raises its own minimum between betas, and a fixed version here
> becomes `NU1605: Detected package downgrade` on the next bump: a build
> failure, not a warning. Observed immediately — `1.5.1-beta.12` requires
> `10.0.13` against the scaffold's `10.0.0`. Float within the major rather than
> with a bare `*`, so a future .NET 11 package cannot be pulled into a
> `net10.0` plugin.

> [!WARNING]
> **Appendix A was reflected from 1.4.13 and has not been re-run.**
> Everything it records is a *lower* bound — later versions are additive over it
> — but re-run the dump before relying on any member's exact signature, and
> treat Appendix B's stable-docs-vs-assembly deltas as unverified until then.

---

## 1. Solution layout

```
Ranks/
├── Ranks/                       # the core plugin
│   ├── Ranks.csproj
│   ├── src/
│   │   ├── Entrypoint.cs        # BasePlugin, lifecycle only
│   │   ├── Config/
│   │   ├── Database/            # connection, writer, migrations
│   │   ├── Players/             # record + slot cache
│   │   ├── Scoring/             # gates, multipliers, award path
│   │   ├── Ranks/               # ladder resolution
│   │   ├── Scoreboard/
│   │   ├── Commands/
│   │   └── Api/                 # IRanksApi implementation
│   └── resources/
│       ├── configs/             # config.jsonc, points.jsonc, ranks.jsonc, commands.jsonc
│       ├── translations/        # en.jsonc (key-complete) + contributions
│       ├── examples/            # alternative ladders
│       └── exports/             # Ranks.Contract.dll (copied by target, not glob)
├── Ranks.Contract/              # interfaces, DTOs, enums, RanksModuleBase
└── Modules/
    └── Ranks_WeaponStats/       # one folder per module
        ├── README.md           # features shipped + how to configure
        └── docs/               # features doc + implementation doc
```

**Modules are named `Ranks_<Name>`** — underscore, not a dot or a hyphen. The
folder name, the project name and the `[PluginMetadata] Id` all use it, so the
ledger derivation has one input and not three spellings of it.

One repository (Q1). The contract ships **only** as `Ranks.Contract.dll` in
`resources/exports/` — there is **no NuGet package**. Modules reference it with
**`<Private>false</Private>`**; a copy sitting next to a module is a second
`Type` identity and `TryGetSharedInterface<T>` silently stops matching. See
FR-API-00 for why that also makes the contract additive-only after 1.0.

---

## 2. Plugin lifecycle

Verified call order for 1.4.13, and confirmed by the stable *Shared API* guide:

```
ctor → Load(hotReload) → ConfigureSharedInterface → UseSharedInterface
     → OnSharedInterfaceInjected → OnAllPluginsLoaded → … → Unload()
```

Each shared-interface stage runs for **every** plugin before the next begins:
all plugins register, then all plugins resolve, then all plugins react.

`Load(bool)` and `Unload()` are **`abstract`** — they must be implemented. The
four shared-interface / lifecycle hooks are `virtual`. `Unload()` takes **no
parameter**. (A `Unload(bool)` signature appears in one framework guide page; it
is wrong for this version.)

### 2.1 The constructor stays empty

`base.Core` is not safely usable there — the plugin manager activates the type
before wiring DI, config and logging. Everything touching `Core.*` goes in
`Load`.

> The official `Admins` plugin runs **database migrations in its constructor**.
> Do not copy that: there is no logger context yet, and a schema failure becomes
> a construction throw with no path to the degraded-but-running state FR-DB-05
> requires.

### 2.2 What goes where

| Stage | Put here | Never here |
|---|---|---|
| `Load` | config binding, schema check, event hooks, command registration, building the API instance | — |
| `ConfigureSharedInterface` | `AddSharedInterface` of the already-built instance | building anything |
| `UseSharedInterface` / `OnSharedInterfaceInjected` | acquiring *other* plugins' interfaces, fail-closed | `HookPost`, bare `+=`, logging |
| `OnAllPluginsLoaded` | one-shot absence reports, the `UseCoreRankingLogic` warning, raising `CoreReady` | — |
| `Unload` | unsubscribe, flush, dispose | — |

The handshake re-runs on **every** plugin load *and* unload, for every loaded
plugin. Anything non-idempotent there accumulates one registration per rebuild.

### 2.3 Attribute handlers outside the plugin class must be registered

**Attribute discovery runs on registered object instances, not on the assembly.**
The class inheriting `BasePlugin` is registered automatically; **every other
class is not**:

```csharp
public override void Load(bool hotReload)
{
    Core.Registrator.Register(_scoring);
    Core.Registrator.Register(_commands);
    Core.Registrator.Register(_scoreboard);
}
```

This is load-bearing for the §1 layout, where `Entrypoint.cs` is lifecycle only
and every `[GameEventHandler]`, `[Command]` and `[EventListener<T>]` lives in
`Scoring/`, `Commands/` or `Scoreboard/`. **An unregistered handler is not an
error — it simply never fires**, so the symptom is "scoring does nothing" with a
clean log and a plugin that loaded fine.

Two further silent-failure modes on the same path:

- **Register each instance exactly once.** Registering the same instance twice
  duplicates its callbacks, so every kill scores twice. This compounds with the
  handshake in §2.2: `Register` belongs in `Load`, never in
  `UseSharedInterface` / `OnSharedInterfaceInjected`, which re-run per rebuild.
- **The signature must match the delegate exactly** — parameter order and types,
  return type (`void` vs `HookResult`), and the generic event type. A mismatch
  is not a compile error; the method is simply not picked up.

Method visibility is not a factor — the framework's own examples decorate
`private` methods.

### 2.4 `Load(hotReload: true)`

Re-adopt already-connected players rather than starting empty (NFR-05). Iterate
`Core.PlayerManager`, filter `IsFakeClient`, and load records for everyone
already `IsAuthorized`.

### 2.5 Raising `CoreReady` (FR-API-10)

Set `IsCoreReady = true` and raise `CoreReady` from `OnAllPluginsLoaded`, after
config binding and the schema probe — **whether or not the probe passed**. A
module needs to know core settled either way. Clear handlers in `Unload`.

---

## 3. Configuration

Bind with `Core.Configuration`, and read every value through a **snapshot the
reload handler swaps**. Never cache a value in a field at construction: that
pins the config and silently ignores every reload, which is the whole point of
NFR-08.

Four files (functional-requirements §13). **The framework creates each one from
its model on first run** — the plugin contains no file-writing code of its own,
never appends to a config and never round-trips one. The model defaults are
therefore the shipped config, so every property needs a sensible one.

**Use the framework's own configuration root. It already does all of this.**
`Core.Configuration.Manager` is an `IConfiguration` built over the files below,
with file watching and a reload token already wired — so binding needs no
`ServiceCollection`, no `AddSwiftly`, no `AddOptions`/`BindConfiguration`, no
`IOptionsMonitor` and no `ServiceProvider` to dispose:

```csharp
Core.Configuration
    // addMissingKeys: a plugin upgrade inserts new keys into an existing file with
    // their defaults and [Description] comments, leaving operator values untouched.
    .InitializeJsonWithModel<Config>("config.jsonc", "Ranks", true)
    .InitializeJsonWithModel<PointsConfig>("points.jsonc", "Points", true)
    .Configure(builder =>
    {
        builder.AddJsonFile(Core.Configuration.GetConfigPath("config.jsonc"),
                            optional: false, reloadOnChange: true);
        builder.AddJsonFile(Core.Configuration.GetConfigPath("points.jsonc"),
                            optional: false, reloadOnChange: true);
    });

var root = Core.Configuration.Manager;
var config = root.GetSection("Ranks").Get<Config>() ?? new Config();

// Live reload, hot-save included. One subscription, no container.
ChangeToken.OnChange(root.GetReloadToken, () => Apply(root.GetSection("Ranks").Get<Config>()));
```

> **`[Description]` on a model property becomes a `//` comment in the generated
> `.jsonc`.** That is the mechanism for every "say so in the shipped config's
> comments" requirement in the FR — write the explanation on the property, not
> in a README nobody opens while editing the file.

### ⚠️ `AddJsonFile` needs the full path from `GetConfigPath`

`AddJsonFile` / `AddTomlFile` resolve **relative to the process working
directory, not the plugin folder**. A bare `"config.jsonc"` is wrong, and wrong
in the worst way — `optional: false` means it throws if the CWD happens not to
contain one, and silently binds the *wrong file* if it does.

`GetConfigPath` requires the extension: `GetConfigPath("config.jsonc")`, not
`"config"`.

> Both `Admins` and `VIPCore` call `AddJsonFile("config.jsonc", false, true)`
> with a bare name. Do not take that as precedent — the stable guide is explicit
> about the full path, and those plugins are relying on the working directory
> happening to be right.

### 3.1 ⚠️ Per-file reload does not come for free

NFR-08 requires that saving `points.jsonc` re-applies **only** `points.jsonc`.
The framework's documented pattern does not give you that, and the gap is silent.

All four files go into **one** `IConfigurationBuilder`, which produces **one**
`IConfigurationRoot` with **one** reload token. `BindConfiguration("Section")`
registers a change-token source against that root, not against the file the
section came from. So a save to any watched file raises `OnChange` on
**`Config`, `PointsConfig`, `RanksConfig` and `CommandsConfig` alike**.

Left alone, editing one point value re-validates the rank ladder, re-clamps
every multiplier and re-emits every clamp warning from §3.5 — four files' worth
of work and log noise for a one-line edit.

**Compounding it: one save fires `OnChange` more than once.** Editors truncate
then write, and `reloadOnChange` watches both operations, so two or three
notifications per save is normal. A handler with side effects runs them all.

Both problems have the same fix — **compare before applying**:

```csharp
// One per bound config file. The same handler owns dedup, the snapshot swap,
// and every derived table built from this file.
private volatile PointsConfig _points = null!;   // hot-path reads come from here
private string? _lastApplied;

private void OnPointsChanged(PointsConfig cfg)
{
    // Fingerprint the bound MODEL, not the file bytes: a reformat or a comment
    // edit is correctly a no-op, and another file's save cannot look like ours.
    var fingerprint = Hash(JsonSerializer.Serialize(cfg));
    if (fingerprint == _lastApplied) return;     // not our file, or a duplicate notification
    _lastApplied = fingerprint;

    Validate(cfg);                               // clamp + warn, §3.5
    _points = cfg;                               // atomic reference swap
    _pointsByEvent = BuildLookup(cfg);           // rebuild derived tables, §3.2
    logger.LogWarning("Ranks: points.jsonc reloaded.");
}
```

- Seed `_lastApplied` and `_points` during the initial load, so the first real
  save is the first *apply* rather than a spurious one.
- Everything with a side effect — validation warnings, ladder rebuild, lookup
  rebuild, the reload notice — goes **after** the guard.

> Separate `IConfigurationRoot`s per file would isolate the tokens properly, but
> that means building configuration outside `Core.Configuration` and giving up
> its path handling and initialisation. The guard above costs one hash per
> notification and keeps the framework integration.

### 3.2 Reading config on hot paths

**`IOptionsMonitor<T>.CurrentValue` does not re-read the file.** Worth
correcting the usual assumption, because it changes what the real problem is:
`CurrentValue` → `Get(Options.DefaultName)` → `OptionsCache<T>.GetOrAdd`, which
is a `ConcurrentDictionary<string, Lazy<T>>` lookup returning a cached instance.
Binding happens once; a change token evicts the entry and the next access
re-binds a single time.

So there is no I/O — but there *is* a concurrent-dictionary lookup on a string
key plus a `Lazy<T>` dereference **per access**, and the award path reads
several values per kill:

```csharp
// four dictionary lookups where one reference read would do
if (_cfg.CurrentValue.General.DetailedLogging) …
var kill = _cfg.CurrentValue.Points.Kill;
var hs   = _cfg.CurrentValue.Points.Headshot;
var min  = _cfg.CurrentValue.Rank.MinPlayers;
```

**Hold a snapshot reference, swapped on reload** (§3.1). The hot path then reads
a plain field:

```csharp
var cfg = _points;        // ONE volatile read at the top of the operation
var value = cfg.Kill + (headshot ? cfg.Headshot : 0);
```

Three rules make that safe:

1. **`volatile`**, because the config watcher thread writes the reference while
   the game thread reads it. Reference assignment is already atomic; `volatile`
   is what guarantees the new one becomes visible.
2. **Treat the snapshot as immutable.** Never mutate a bound config object —
   reload replaces it wholesale, so a mutation is both lost and racy.
3. **Read the reference once per logical operation**, at the top. Re-reading
   `_points` between two steps of one award lets a reload land in the middle and
   mix old and new values. One read gives that award a consistent view; the next
   award picks up the change.

**Derived lookups are rebuilt in the reload handler, not computed per use.** The
points catalogue is the clear case: resolving `headshot` through a string
dictionary on every kill is wasted work when the key set is closed and known at
load. Build an array indexed by the event enum once per reload and index it.

> This is the one place the "never snapshot config" rule is **wrong**, and the
> distinction is narrow enough to state explicitly. A snapshot captured once and
> never refreshed is frozen and silently serves stale values. A snapshot
> **reassigned by `OnChange`** is both current and fast. What is always wrong is
> `IOptions<T>.Value`, which has no change notification at all.

### 3.3 Reload safety

`OnChange` fires on file change. **Writing the watched file, or calling
`IConfigurationRoot.Reload()`, from inside that callback re-raises the token and
loops** — and a re-entrancy flag does not suppress it. Seed, migrate and
sanitize on explicit load paths only.

### 3.4 Restart-only keys

`Database.Connection`, `Database.TablePrefix` and `Database.TableSuffix` point
the server at a different table set. Changing them live would leave loaded
records writing to tables they were not read from. Validate at load, require a
restart, and say so in the shipped comments.

**`commands.jsonc` is restart-only for the same class of reason**, unless the
unregister path in §8.1 is implemented: a live reload can register the new name
but nothing drops the old one, so the server ends up answering both. Reloading
the file is still useful — it is how `HelpText` and `Permission` changes take
effect — but a renamed command needs the restart.

### 3.5 Validation at load

| Key | Rule | On violation |
|---|---|---|
| `TablePrefix` / `TableSuffix` | `^[A-Za-z0-9_]*$` | **throw** — these become SQL identifiers |
| `Dynamic*Min` | `(0, 1]` | clamp + warn |
| `Dynamic*Max` | `>= 1` | clamp + warn |
| `ranks.jsonc` first entry | `Points == 0` | **throw** — a player below the lowest threshold has no rank |
| `Rank.MinPoints` | `>= 0` | clamp + warn (FR-PTS-05's formula assumes non-negative) |
| `Database.MaintenanceIntervalHours` | `>= 0` | clamp to `0` + warn (`0` = load-only) |
| `Database.VirtualPurgeMode` | a defined `VirtualPurgeMode` | reset to `Days` + warn — `VirtualPurgeDays` stays in charge |

Throw for identifiers, clamp for numbers: a bad multiplier is a bad game, a bad
identifier is a SQL injection surface.

---

## 4. Database

### 4.1 Connection

```csharp
IDbConnection          GetConnection(string connectionName);
DatabaseConnectionInfo GetConnectionInfo(string connectionName);   // .Driver is a string
string                 GetConnectionString(string connectionName);
```

`Ranks` never holds credentials — `Database.Connection` names an entry in
SwiftlyS2's global `configs/database.jsonc`. The returned `IDbConnection` is
**not open**; call `Open()` yourself — and note §4.5's rule that the migration
runner needs it *unopened*.

#### An unknown connection name silently returns the default connection

All three methods **fall back to the default connection when the name is not
found**. That is documented behaviour, not a bug: no exception, no warning.

**Core does not try to detect it.** There is no `HasConnection` on
`IDatabaseService` in any version, so "was this name found" can only be inferred
from the resolved values — and an entry that legitimately matches `default`
is indistinguishable from one that is missing. Inferring would mean refusing to
start on a valid configuration, which is a worse failure than the one it
prevents. Which database an operator points the plugin at is their call.

What core owes them instead is **visibility and a real error**:

```csharp
// Resolved once at boot and passed down: §4.2's engine dispatch needs Driver off this same struct.
var info = Core.Database.GetConnectionInfo(name);
logger.LogInformation("Database '{Name}' resolved to {Driver} {Host}:{Port}/{Database}",
                      name, info.Driver, info.Host, info.Port, info.Database);

try
{
    using var probe = Core.Database.GetConnection(name);
    probe.Open();
}
catch (Exception ex)
{
    throw new InvalidOperationException(
        $"Database.Connection '{name}' could not be opened. Check the entry in SwiftlyS2's " +
        "configs/database.jsonc — host, port, credentials and database name.", ex);
}
```

- **The probe cannot supply the log line.** `IDbConnection` exposes `Database`
  and `ConnectionString` and nothing else useful — no `Driver`, `Host` or
  `Port`. Those would have to be parsed back out of `ConnectionString`, which
  carries the password, so logging from the probe means taking a secret-bearing
  string apart in order to avoid printing the secret. `GetConnectionInfo` hands
  them over already separated, with `Pass` sitting in its own field that simply
  never gets logged.
- **Log before opening.** A connection that fails to open still has to say where
  it pointed, or the error names a connection and nothing else.
- `DatabaseConnectionInfo` is a `record struct`, so passing `info` down to the
  migration runner copies a handful of fields rather than re-querying.
- The probe is opened and disposed on its own connection; §4.5's runner still
  needs an **unopened** one.
- A host that is unreachable, credentials that are wrong, or a database that
  does not exist are all misconfigurations with a clear owner, so they throw
  (FR-DB-02) rather than degrading into a plugin that half-works.

#### Connection options

A connection entry may be an object carrying an `options` map of raw driver
keywords (`MaximumPoolSize`, `Journal Mode`, `SslMode`, …), applied *after* the
other fields so an option can override one of them. An unsupported keyword
throws `ArgumentException` naming the option when the connection is requested.

Pool sizing is the operator's lever through that map — not something this
plugin should try to manage around FR-DB-06's single writer.

### 4.2 ⚠️ Dispatch on `Driver`, never on the CLR type

```csharp
// info is the struct resolved once in §4.1 — Driver is a string, verified by reflection.
switch (info.Driver)
{
    case "mysql":      rb.AddMySql5();   break;
    case "postgresql": rb.AddPostgres(); break;
    case "sqlite":     rb.AddSQLite();   break;
    default: throw new NotSupportedException($"Unsupported driver '{info.Driver}'.");
}
```

The plugin template dispatches on `connection is SqliteConnection` using
**`Microsoft.Data.Sqlite`**. The stable database guide names
**`System.Data.SQLite`** as the SQLite provider, so that case is dead code and
SQLite silently falls through to the default branch. (`Admins` gets the import
right; it is still CLR-type dispatch, which breaks the moment the provider
changes.)

Driver strings are `mysql`, `postgresql` and `sqlite`, confirmed against the
stable guide.

### 4.3 Table affixes

One resolver, used by everything: tables, indexes, constraints, **and the
migration ledger**.

```csharp
// "awp" / "awp_" / "_awp" must all produce the same name.
prefix = string.IsNullOrEmpty(p) ? "" : p.Trim('_') + "_";
suffix = string.IsNullOrEmpty(s) ? "" : "_" + s.Trim('_');
```

Normalising the separator closes the likeliest operator trap: setting `"awp"`,
accumulating data in `awpranks_players`, then "fixing" it to `"awp_"` and being
silently pointed at a fresh empty table set — indistinguishable from every
player vanishing.

### 4.4 Dommel's resolver is process-wide

`[Table("…")]` is compile-time, so affix support means
`DommelMapper.SetTableNameResolver(…)` — **static, per-process, shared with every
other plugin using Dommel in that host**. `Admins` uses Dommel and will be
co-installed.

```csharp
public string ResolveTableName(Type type) =>
    type.Assembly == typeof(PlayerRecord).Assembly
        ? TableNames.Resolve(_inner.ResolveTableName(type))
        : _inner.ResolveTableName(type);
```

Install before the first query — `Resolvers.TypeTableNameCache` caches per type
on first use, so a late install leaves already-touched entities unprefixed.
Without the assembly filter, Ranks renames `bans` to `awp_bans` and breaks
`Admins` with no error on either side.

### 4.5 Migrations — FluentMigrator

**Core runs every migration, its own and every module's** (FR-DB-05). One
builder, called once per assembly, each with its own derived ledger.

```csharp
private void Migrate(Assembly assembly, string ledgerName)
{
    var services = new ServiceCollection()
        .AddFluentMigratorCore()
        .ConfigureRunner(rb =>
        {
            ConfigureEngine(rb, info);                        // §4.2, same struct resolved in §4.1
            rb.WithGlobalConnectionString(connection.ConnectionString)
              .WithVersionTable(new AffixedVersionTable(ledgerName))
              .ScanIn(assembly).For.Migrations();
        })
        .AddLogging(lb => lb.AddFluentMigratorConsole())
        .BuildServiceProvider(false);

    using (var scope = services.CreateScope())
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

    services.Dispose();     // ← not optional, see below
}
```

Core's own schema migrates in `Load`. Registered module assemblies migrate in
`OnAllPluginsLoaded`, because that is the first point at which the handshake has
delivered the API and every module has had a chance to register.

A module assembly is **not** scanned as loaded: the module ships its own
FluentMigrator, so its migrations implement an `IMigration` core's runner does not
match. `ModuleMigrationContext` loads the module DLL a second time with
`FluentMigrator*` bound to core's copy; core migrates that and unloads it. Why
FluentMigrator cannot simply be exported instead:
`.github/instructions/database.instructions.md`.

**Each module is migrated inside its own `try`/`catch`** (FR-DB-05 req. 9). Core
has taken on running third-party code; an unhandled throw from one module's
migration would otherwise take core's schema and every other module down with
it. Log at `Error`, naming the module, and continue.

**`RegisterMigrations` must be idempotent.** The handshake re-runs on every
plugin load *and* unload, so modules re-register constantly. Keep a
`HashSet<(Assembly, string)>` of what has already been migrated this process and
skip repeats — `MigrateUp()` on an up-to-date ledger is cheap but not free, and
re-running it per rebuild is pure waste.

#### Enforcing the affixes

FluentMigrator has **no table-name convention** — `IConventionSet` covers
constraint, index, foreign-key, primary-key, schema and auto-script names only.
The interception point is `IMigrationProcessor`: decorate it and rewrite
`TableName` before delegating.

Expressions carrying a table name: `CreateTable`, `DeleteTable`, `AlterTable`,
`RenameTable` (both names), `CreateColumn`, `AlterColumn`, `DeleteColumn`,
`RenameColumn`, `CreateIndex` / `DeleteIndex` (`.Index.TableName`),
`CreateConstraint` / `DeleteConstraint` (`.Constraint.TableName`),
`CreateForeignKey` / `DeleteForeignKey` (`.ForeignKey.ForeignTable` **and**
`.PrimaryTable`), `InsertData`, `DeleteData`, `UpdateData`,
`AlterDefaultConstraint`, `DeleteDefaultConstraint`.

Miss one and that statement targets an unprefixed table — which on a prefixed
server means it silently operates on nothing, or on another server's data.

> **Rewrite the existence checks too, not just the expressions.**
> `IMigrationProcessor` also implements `IQuerySchema` — `SchemaExists`,
> `TableExists`, `ColumnExists`, `ConstraintExists`, `IndexExists`,
> `SequenceExists`, `DefaultValueExists`. These are **not** expressions:
> `Schema.Table("x").Exists()` calls the processor directly, so a decorator that
> only rewrites expressions answers it for the unaffixed name — and authoring
> rule 2 below is built entirely on that call.
>
> The failure is silent and permanent in exactly the setup FR-DB-03 exists for.
> One database holding a default install *and* an `awp_` one: the `awp_` ledger
> is separate so the migration runs, the guard asks whether `ranks_players`
> exists, finds the **default install's** table, skips creation, and records the
> migration as applied. `awp_ranks_players` is never created and `MigrateUp()`
> never retries.

> `Execute.Sql` has no expression tree and cannot be rewritten. After migrating
> a module, list the tables it created and log at `Error` any that lack the
> expected affixes (FR-DB-05 req. 10).

#### Engine identifiers

| Registration | `DatabaseType` | Stable aliases |
|---|---|---|
| `AddMySql5()` | `MySql5` | `MariaDB`, `MySql`, `MySQL 5` |
| `AddPostgres()` | `PostgreSQL15_0` | `PostgreSQL` |
| `AddSQLite()` | `SQLite` | — |

**`IfDatabase("Postgres")` matches nothing.** No error — the block is skipped and
the migration records as applied having done nothing. Use `"PostgreSQL"`. Never
match on `DatabaseType`; it is version-stamped and moves with releases. To check
in any build, resolve `IMigrationProcessor` from the runner scope and print
`DatabaseType` + `DatabaseTypeAliases`.

#### The ledger

**The ledger is scoped to the table set it tracks.** `awp_ranks_players` and
`dm_ranks_players` are different tables, so "migration 202601010000 applied" is
meaningless unless the record says *applied to which set*. Affixing the ledger
is what supplies that scope — one ledger per affix per plugin.

`IVersionTableMetaData` must be implemented directly —
`DefaultVersionTableMetaData` cannot be cheaply subclassed in FluentMigrator 8.x
(its only constructor is `(IConventionSet, IOptions<RunnerOptions>)`).

```csharp
internal sealed class AffixedVersionTable : IVersionTableMetaData
{
    public AffixedVersionTable(string ledgerName)
    {
        TableName = ledgerName;
        UniqueIndexName = Fit($"ux_{ledgerName}_version");
    }

    public string TableName          { get; }
    public string UniqueIndexName    { get; }
    public string ColumnName            => "version";
    public string AppliedOnColumnName   => "applied_on";
    public string DescriptionColumnName => "description";
    public string SchemaName            => string.Empty;
    public bool   OwnsSchema            => true;
    public bool   CreateWithPrimaryKey  => false;
}
```

**`UniqueIndexName` must be derived from the ledger name, not left constant.**
This is the step the whole per-plugin-ledger design fails at if missed.
`DefaultVersionTableMetaData` returns the fixed string `UC_Version`, and
VIPCore's custom table hardcodes `ix_version` — safe only because each is the
single ledger in its database. With one ledger per plugin:

| Engine | Index name scope | Second ledger |
|---|--:|---|
| MySQL / MariaDB | per table | fine |
| PostgreSQL | per schema | `CREATE INDEX` **fails** |
| SQLite | database-wide | `CREATE INDEX` **fails** |

So the second plugin to migrate kills itself on two of the three supported
engines. `CreateWithPrimaryKey = false` is what makes FluentMigrator create a
unique *index* here rather than a primary key — keep it, and keep the name
unique.

> **The index name is the binding length constraint, not the table name.** It is
> the ledger name plus eleven characters, so a ledger that fits in PostgreSQL's
> 63 can still produce an index that does not. Apply the FR-DB-05 hash-folding
> rule to **both**, independently — capping only the table name leaves the real
> overflow unhandled.

The ledger name is `ranks_migrations` for core, and for a module:

```
lowercase(pluginId) → non-alphanumeric runs to "_" → strip leading "ranks_"
                    → prepend "ranks_migrations_" → apply affixes
```

⚠️ **Cap the result at 63 characters.** MySQL allows 64 and **PostgreSQL 63 —
truncating silently**, which can merge two modules' ledgers back into one and
recreate exactly the failure the per-plugin ledger exists to prevent. Fold the
tail into a short deterministic hash rather than letting the engine cut it.

#### Authoring rules

1. Version numbers are `YYYYMMDDHHmm`, assigned once, **never reused or
   renumbered** — the ledger stores the number, not the file.
2. **Guard every object individually.** One
   `if (Schema.Table(x).Exists()) return;` covering a table, its indexes and a
   second table skips the rest while still recording the migration as applied.
   Permanent: `MigrateUp()` never retries. MySQL DDL is not transactional, so a
   first run failing *after* a `Create.Table` reproduces this with no legacy
   database involved. **The guard is only correct once the decorator rewrites
   `IQuerySchema` too** — see "Enforcing the affixes" above; until then it reads
   the unaffixed name.
3. **Except `Create.PrimaryKey`.** MySQL names every PK `PRIMARY`, so
   `Schema.Table(t).Constraint("pk_x").Exists()` is always false and
   `ADD CONSTRAINT … PRIMARY KEY` then fails with *Multiple primary key defined*.
   Declare composite keys inline on `Create.Table`.
4. **Affix index and constraint names too.** Index names are per-table on MySQL
   but schema-scoped on PostgreSQL and database-scoped on SQLite, so one
   unaffixed `idx_points` on both `ranks_players` and `awp_ranks_players` works
   on MySQL and fails on the other two.
5. `Down()` must exist, but downgrade is unsupported and **no `Down()` may drop a
   table holding player data**.

#### A worked example

Core's first migration, showing every rule above in one place:

```csharp
[Migration(202601010000)]
public sealed class M202601010000_Players : Migration
{
    public override void Up()
    {
        // Base name carries the ranks_ namespace; core's decorator adds prefix/suffix.
        if (!Schema.Table("ranks_players").Exists())
            Create.Table("ranks_players")
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("name").AsString(128).NotNullable()
                .WithColumn("points").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("kills").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("last_seen").AsDateTime().NotNullable();

        // Guarded separately — rule 2. A combined guard would skip this silently.
        if (!Schema.Table("ranks_players").Index("ix_ranks_players_last_seen").Exists())
            Create.Index("ix_ranks_players_last_seen")
                  .OnTable("ranks_players").OnColumn("last_seen").Ascending();
    }

    public override void Down()
    {
        // Drops the index only; the table holds player data — rule 5.
        Delete.Index("ix_ranks_players_last_seen").OnTable("ranks_players");
    }
}
```

- **Id `202601010000`** is `YYYYMMDDHHmm`, assigned once and never renumbered.
  Core's ids and each module's are independent — separate ledgers (FR-DB-05) —
  so there is nothing to coordinate across plugins the way `Admins` must.
- **Composite keys go inline on `Create.Table`**, never `Create.PrimaryKey` —
  rule 3, and the only portable form on SQLite.
- **The index name is affixed by the decorator** along with the table name, so
  write it bare here; rule 4 explains why an unaffixed index breaks PostgreSQL
  and SQLite rather than MySQL.
- `varchar(128)` for `name` is utf8mb4 on MySQL — FR-DB-04.

#### SQLite constraints

- **No `ALTER COLUMN`** — the SQLite generator routes it to
  `HandleCompatibility`, which throws in default STRICT mode.
  `CompatibilityMode.LOOSE` does not rescue a primary-key change: it emits
  `ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY` and the engine rejects it.
- Changing or dropping a column portably means create-new → copy → drop →
  rename. Plan for it.
- Multi-column inline `.PrimaryKey()` on `Create.Table` is the only portable
  composite key.

#### Runner lifecycle

- **Pass an unopened connection.** MySqlConnector strips the password from
  `ConnectionString` once opened, and FluentMigrator builds its own connection
  from that string.
- **Dispose the `ServiceProvider`.** Leaving it alive leaks the processor and its
  connection; on SQLite it holds the database file open across hot reload.
  (`Admins` leaks one per module per load.)
- `MigrateUp()` **aborts at the first failing migration**, so one
  engine-specific failure hides every migration after it. A green MySQL run says
  nothing about SQLite.

### 4.6 A failed migration must not disable a working database

Shared hosting commonly grants DML but not DDL. A catch-all that disables the
plugin on any migration failure kills it on those hosts even when every table
exists and is perfectly usable.

> **This is the opposite of §4.1's response to an unopenable connection, and the
> difference is what is still usable.** A migration that fails against a
> reachable database leaves an install that can still read and write, so warn
> and continue. A connection that will not open leaves nothing to run on, so
> throw. Same subsystem, opposite failure, opposite answer.

```csharp
try { RunMigrations(connection); }
catch (Exception ex) { schemaError = ex; }

var unusable = FindUnusableTables(connection);        // SELECT 1 FROM {t} WHERE 1 = 0
if (unusable.Count > 0)       { /* disable, naming tables + missing grants */ }
else if (schemaError != null) { /* warn, keep running */ }
```

- `SELECT 1 FROM {t} WHERE 1 = 0` is provider-agnostic and tests *readability*,
  which is what the plugin needs — better than an `information_schema` query.
- Run the probe **unconditionally**, even when migrations report success. That
  turns "an exception on every player connect" into one line at boot.
- The probe opens the connection, so it runs **after** the runner, which needs it
  unopened.
- Gate the required-table list on installed modules, or the probe demands tables
  the operator deliberately never enabled.
- Permission errors, walking `InnerException` because FluentMigrator wraps
  several layers deep: MySQL `1044` / `1142` / `1143` / `1227` — **not `1045`**,
  which is a bad password and fails at `Open()` anyway; PostgreSQL
  `SqlState == "42501"`; SQLite codes `8` / `23`.

### 4.7 The writer

One writer, absolute values, one statement per record.

```sql
-- MySQL / MariaDB
INSERT INTO {t} (...) VALUES (...) ON DUPLICATE KEY UPDATE ...
-- PostgreSQL / SQLite
INSERT INTO {t} (...) VALUES (...) ON CONFLICT (steam_id) DO UPDATE SET ...
```

"`UPDATE`, then `INSERT` on zero rows affected, catch duplicate-key" is **not
portable** — a failed statement aborts the surrounding PostgreSQL transaction.

Each player sits behind a **savepoint** so one bad row (an unencodable name)
skips that player instead of aborting the batch. Mark a record saved only after
the write commits.

`SaveAllAsync` coalesces: a concurrent call joins the in-flight pass. A module
calling it every round end on a 64-slot server must cost one pass, not 64.

### 4.8 Maintenance timing (FR-DB-09)

Purges and roll-ups go through one gate, not scattered across call sites:

```csharp
private DateTime _lastMaintenance = DateTime.MinValue;   // resets on restart, by design

private async Task TryRunMaintenanceAsync(bool force)
{
    if (!force)
    {
        var hours = _cfg.Database.MaintenanceIntervalHours;
        if (hours <= 0) return;
        if (DateTime.UtcNow - _lastMaintenance < TimeSpan.FromHours(hours)) return;
        if (Core.PlayerManager.GetAllPlayers().Any(p => !p.IsFakeClient)) return;
    }

    await RollUpHistoryAsync();
    await PurgeAsync();
    _lastMaintenance = DateTime.UtcNow;
}
```

`Load` calls it with `force: !hotReload`; the round-end handler calls it with
`force: false`. **A hot reload passes neither**, which is the point — that path
runs with players connected.

**Roll-up precedes purge and the order is not cosmetic.** Pruning a period
before folding it into the next tier destroys it silently: the rows are gone and
nothing reports a gap.

`_lastMaintenance` is set **after** the pass, so a failure retries on the next
eligible round end rather than being marked done. The whole call is wrapped by
the caller — a failed purge must not abort `Load`.

### 4.9 Periods (FR-DB-10)

`PeriodClock` maps a moment onto the N-day grid; `PlayerSessions` owns the active
period; `PlayerStore` owns every period-aware statement. Three rules carry it:

- **The active period moves only on the game thread**, in `RollOverIfDue` (round
  end, save timer, and a loaded record found on a newer period). Each resident
  record goes through `PlayerRecord.StartPeriod`, which queues its figures as
  `PendingArchives` and clears progress through the one `ClearProgress`.
- **An archive is written with its record, never on its own.** `Save` writes each
  pending archive into `ranks_players_history` inside the record's savepoint,
  then the fresh row. `Settle` runs off the game thread, so it clears archives
  with a compare-and-swap (`SettleArchives`): a rollover replacing the array
  mid-write keeps what the write never carried, at the cost of one idempotent
  re-write.
- **Only an older stored period is stale.** `Load` closes it in one transaction —
  `INSERT IGNORE`/`ON CONFLICT DO NOTHING` into history, then an `UPDATE` guarded
  on the stale period — and leaves a newer one alone for the caller to catch up
  to. Reads that must not write (`ReadCurrent`, snapshots, menus) show a stale row
  as the fresh record its next load will make.

Verified against MySQL 8.4, MariaDB 11, PostgreSQL 17 and SQLite with a reflection
harness over the compiled `SchemaMigrator` and `PlayerStore`: upgrade over
pre-period rows, fresh install with affixes, both archive paths, the conflict and
newer-period cases, all-time arithmetic and purge.

---

## 5. Per-player state

Two structures, both required (NFR-02):

```csharp
ConcurrentDictionary<ulong, PlayerRecord> _records;   // authoritative, survives disconnect
PlayerRecord?[] _slots = new PlayerRecord?[64];       // hot path, cleared on disconnect
```

Measured, 20 M lookups × 3 field reads, .NET 10 Server GC:

| Strategy | ns/op |
|---|--:|
| Slot array holding the record | **2.8–3.3** |
| Slot array holding identity + dictionary lookup | 11.6–11.9 |
| One `ConcurrentDictionary<int, T>` | 13.2 |
| `ConditionalWeakTable<IPlayer, T>` | 17.7–19.1 |

Caching the **record reference** rather than the identity is what separates 3 ns
from 11.7. Clearing the slot on disconnect is the invariant that makes it safe.
CWT is the slowest option and additionally cannot answer "adopt this reconnecting
SteamID", which the dictionary is for.

### 5.1 Load and save triggers

Load is driven by the **`OnClientSteamAuthorize` core event**, not by connect and
not by polling `IPlayer.IsAuthorized`. Before Steam authorization the real
SteamID is not available — `IPlayer.UnauthorizedSteamID` holds a provisional
value — so loading on connect keys the record to the wrong id.
`OnClientSteamAuthorizeFail` is the paired failure signal; that player is never
tracked.

```csharp
public override void Load(bool hotReload)
{
    Core.Event.OnClientSteamAuthorize += OnSteamAuthorized;   // in Load, never in the handshake
}
```

Subscribe in `Load`. The framework tears down every listener a plugin registered
on unload or hot reload automatically, so `Unload` only needs an explicit `-=`
to stop listening *earlier* than that. `-=` removes the exact delegate instance,
so keep the field if you do unsubscribe by hand.

A failed load leaves the player **untracked** and retries. Never substitute a
fresh empty record: the next absolute save would wipe the real row.

`IsFakeClient` is filtered at load. Bots never get a record and never get a row,
independent of `Rank.BotsEnabled` (FR-DB-07).

> **No per-tick subscription** (NFR-11). `OnTick` and `OnWorldUpdate` fire every
> tick and must be allocation-free; nothing here is tick-driven. The periodic
> save pass is an `ISchedulerService` timer.

---

## 6. Scoring

### 6.0 Hooking the events

```csharp
[GameEventHandler(HookMode.Post)]
public HookResult OnPlayerDeath(EventPlayerDeath e) { …; return HookResult.Continue; }
```

Scoring hooks are **`HookMode.Post`** — core observes an outcome, it never
cancels one.

> These handlers live in `Scoring/`, not in the plugin class, so the instance
> holding them **must** be passed to `Core.Registrator.Register` in `Load`
> (§2.3). Without it every one of them is silently inert.

`HookResult` has four members, and only `Continue` is correct here:

| | Value | Effect |
|---|--:|---|
| `Continue` | 0 | following hooks and the original both run |
| `Stop` | 1 | cancels following hooks **and** the original — **no effect in a post hook** |
| `Handled` | 2 | cancels following hooks, original still runs — **no effect in a post hook** |
| `CancelOriginal` | 3 | following hooks run, original is cancelled |

**`Handled` does not block the original function**, and neither it nor `Stop`
does anything at all from a post hook. Returning either from a scoring handler
is a silent no-op that reads like a deliberate block.

#### ✅ The special-kill attributes are typed properties, not accessor keys

An earlier revision of this guide said `noscope`, `thrusmoke`, `penetrated` and
`attackerblind` had no generated property and had to be read off
`e.Accessor.GetBool(...)`. **That is wrong against `1.5.1-beta.12`**, verified by
reflecting `EventPlayerDeath`:

| Property | Type | Property | Type |
|---|---|---|---|
| `NoScope` | `bool` | `AttackerBlind` | `bool` |
| `ThruSmoke` | `bool` | `Penetrated` | **`short`** |
| `AssistedFlash` | `bool` | `Headshot` | `bool` |
| `Distance` | `float` | `ActualHitGroup` | `HitGroup_t` |

```csharp
if (e.NoScope)        … ;     // not e.Accessor.GetBool("noscope")
if (e.Penetrated > 0) … ;     // short, so it is a count — compare, don't cast to bool
```

**Prefer the typed property in every case.** An accessor miss returns a default
silently; a renamed or absent property fails the build, which is the whole point
of having them. `Penetrated` being a `short` rather than a `bool` is the one
that would have been wrong either way — it is a wallbang *count*.

`e.Accessor` remains the escape hatch for a key the generator genuinely does not
cover; re-check by reflection before assuming a key is in that category.

#### ⚠️ `UnhookPre<T>()` / `UnhookPost<T>()` drop *every* hook for that type

They are not the inverse of one registration. Within this plugin they remove
all hooks for the event type at once, so a targeted "stop scoring deaths" that
calls `UnhookPost<EventPlayerDeath>()` also removes any other death handler the
plugin registered. Gate behaviour behind a flag instead of unhooking.

`Core.GameEvent.Fire*` sync overloads are **thread-unsafe**; `FireAsync` /
`FireToPlayerAsync` / `FireToServerAsync` exist for background flows. Core does
not fire game events, but FR-SCB-01's `EventNextlevelChanged` is the exception
— fire it on the game thread.

### 6.1 Order of operations

```
1. gate check            → MinPlayers / warmup / bots / FFA   (FR-PTS-03)
2. base value            → points.jsonc × count
3. PointsChanging        → subscribers may mutate or cancel   (FR-API-04)
4. multipliers           → VIP, dynamic kill/death            (FR-PTS-04)
5. round once            → away from zero
6. clamp                 → >= Rank.MinPoints
7. apply + PointsChanged + RankChanged if the tier moved
```

Round **away from zero** at step 5, once, after all multipliers. Rounding per
multiplier compounds error, and rounding toward zero makes a small non-zero
award silently `0`.

`UseCoreRankingLogic = false` mutes steps 1–6 entirely. `AddPoints` and
`SetPoints` skip 1–4 in **both** modes — they are stored raw.

### 6.2 Dynamic kill/death

```
d = (Pv − Pa) / max(Pv, Pa)                     // 0 when both are 0
m = d >= 0 ? 1 + d·(Max−1) : 1 + d·(1−Min)
```

Edge cases, each with a correct answer rather than a guard:

| Case | Result |
|---|---|
| No attacker (suicide, world, fall) | `1.0`; the kill side never arises |
| Bot counterparty, bots scoring | bot has no record → `0` → `d = −1` → kill pays `DynamicKillMin` |
| Both on `0` | `d = 0` → `1.0` |
| Team kill | own event, unaffected |

Bot farming pricing itself at the floor is a useful property, not an accident.

### 6.3 Killstreaks

Eleven tiers (2 → 12+). Per-round accumulation, reset on death and optionally on
round end. `Points.SecondsBetweenKills = 0` means no time limit.

### 6.4 Match win and loss

**`EventCsWinPanelMatch` has zero fields.** It is a notification that the match
ended, not a result — there is no winner to read off it. The side has to be
derived from the team entities at the moment it fires:

```csharp
[GameEventHandler(HookMode.Post)]
public HookResult OnMatchEnd(EventCsWinPanelMatch e)
{
    var teams = Core.EntitySystem.GetAllEntitiesByClass<CCSTeam>()
        .Where(t => t.TeamNum is 2 or 3)
        .ToArray();

    if (teams.Length != 2 || teams[0].Score == teams[1].Score) return HookResult.Continue;

    var winner = teams[0].Score > teams[1].Score ? teams[0] : teams[1];
    ...
    return HookResult.Continue;
}
```

`Score` is a networked `int` on `CTeam`, which `CCSTeam` inherits, and
`PlayerControllers` on the same class is the roster to award. `TeamNum` is a
networked `uint8` inherited from `CBaseEntity`.

Filtering to team numbers 2 and 3 is not decoration —
`GetAllEntitiesByClass<CCSTeam>()` also returns the spectator and unassigned
team entities, and their scores would otherwise take part in the comparison. An
equal score awards neither side, per FR-PTS-06.

> The event is declared in `mod.gameevents` only, not `core.gameevents`. Its
> existence in the dump is **not** evidence that it still fires — that is the
> FR-PTS-01 deprecation risk, and it stays on the live-verification list.

---

## 7. Scoreboard

### 7.1 Every networked write needs its notifier

```csharp
controller.Clan = tag;                controller.ClanUpdated();
controller.Score = points;            controller.ScoreUpdated();
controller.CompetitiveRanking = r;    controller.CompetitiveRankingUpdated();
controller.CompetitiveRankType = t;   controller.CompetitiveRankTypeUpdated();
```

Without the notifier the value changes server-side and is never networked — the
scoreboard keeps rendering the old one and the bug looks like a failed
calculation.

The clan tag additionally needs `EventNextlevelChanged` fired at that player, or
the scoreboard will not refresh until its next natural redraw.

### 7.2 ⚠️ Rank type values are unverified

`m_iCompetitiveRankType` is confirmed as a **networked `signed char` on
`CCSPlayerController`** (CS2 schema dump). The engine values its modes take are
**not** confirmed, and the `0`–`4` table in FR-SCB-03 is a plugin-level enum that
must be translated to them.

This is load-bearing: Premier is the default mode, and a wrong rank type renders
**nothing at all, with no error anywhere** — identical in appearance to a broken
points calculation. Confirm each value on a live server before shipping
(acceptance criterion 7).

In Premier mode `CompetitiveRanking` carries the points value itself and CS2
colours it by its own band; `SkillGroup` and the offset are ignored.

> **Settle this in the same session as §7.4's probe.** Both questions are
> "write the field, look at the scoreboard, see what happens" — the rank type
> decides whether anything renders at all, and the probe decides whether what
> renders survives. Running them separately means a blank scoreboard with two
> candidate causes and no way to tell them apart.

### 7.3 Do not fight other plugins for the clan tag

Write it on rank change, not on a tick. Expose `GetDesiredClanTag` (FR-API-02)
read-only so a tag arbiter can take ownership of the field.

### 7.4 🧪 Refresh-strategy harness — testing only, never shipped

FR-SCB writes on change and calls the notifier. That is correct **if the engine
leaves the fields alone afterwards**, and nobody has established that it does.
This harness answers it with measurements instead of assumptions, then gets
deleted.

**Default `Off`, and the mode being `Off` is what keeps it out of production.**
It is deliberately absent from the FR §13 config summary: it is an
investigation tool, not surface anyone configures.

```jsonc
"Experiment": {
  "RefreshStrategy": "Off",   // Off | Probe | Timer | Hook
  "IntervalSeconds": 1.0,
  "SummarySeconds": 10.0
}
```

| Mode | Does | Signature |
|---|---|---|
| `Off` | FR-SCB as specified | no |
| `Probe` | **reads** the field back on a timer and reports divergence | **no** |
| `Timer` | re-stamps every `IntervalSeconds` | **no** |
| `Hook` | stamps inside `UpdateResourceData`, counts call rate | yes |

#### Probe first — most of the answer needs no signature at all

The decisive question is "does the engine overwrite `CompetitiveRanking` after
we set it, and how fast". A read-back answers that outright:

```csharp
// Divergence from what we last wrote is the engine clobbering it — nothing else writes this field.
if (controller.CompetitiveRanking != expected) { _clobbers++; _firstClobberMs ??= sinceWrite; }
```

Run `Probe` for a few rounds before touching a byte pattern. Its result decides
whether the rest of the harness is even needed:

| Probe result | Means | Action |
|---|---|---|
| zero divergence | engine does not touch the field | **FR-SCB is already complete — delete the harness** |
| divergence after seconds | slow periodic refresh | `Timer`, no signature |
| divergence every frame | per-frame rebuild | `Hook` is the only option that holds |

#### Measure with counters, not log lines

Logging per call at hook rate dominates the cost being measured. Accumulate
`long` counters and emit **one** summary line every `SummarySeconds`:
invocations, invocations/second, clobbers detected, milliseconds to first
clobber. That line is ungated — the mode defaults to `Off`, so the mode is the
gate, and an experiment nobody can read is pointless.

#### The hook, if `Probe` says it is needed

```csharp
// Arity and convention are readable from the prologue; the return type is not.
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
private delegate void UpdateResourceDataFn(nint thisPtr);
```

Both patterns move the first integer argument straight into a callee-saved
register and use nothing else — Windows `48 8B F9` (`mov rdi, rcx`), Linux
`48 89 FB` (`mov rbx, rdi`). So it is a **single-pointer `this`-call** under the
one x64 convention each platform has. The return type cannot be inferred this
way; start at `void` and confirm on the live server.

```csharp
var fn = Core.Memory.GetUnmanagedFunctionByAddress<UpdateResourceDataFn>(addr);
_hookId = fn.AddHook(next => thisPtr =>
{
    next()(thisPtr);                                                   // let the engine write first
    var controller = Core.Memory.ToSchemaClass<CCSPlayerController>(thisPtr);
    Stamp(controller);                                                 // then overwrite it
});
```

Stamping **after** `next()` is the entire point — before it, the engine's own
write lands last and wins.

Four rules, each load-bearing:

1. **Resolve with `TryGetSignature` and fall back to `Timer`.** A pattern that
   stops matching after a CS2 update must degrade, never throw. Log at
   `Warning`, ungated.
2. **`RemoveHook(_hookId)` in `Unload`.** A hot reload otherwise leaves the
   engine calling into a managed delegate that no longer exists, which crashes
   the server on the next invocation rather than failing to load.
3. The hook runs on the **game thread** (it is server game code), so touching
   the controller directly is legal here — one of the few places that is true
   without a scheduler hop.
4. Keep every mention of the delegate, the signature and `_hookId` in **one
   file**, so removing the harness is a file delete plus one config block.

#### Exit criteria

The harness has done its job once `Probe` has run for several rounds on a live
server. Record the numbers in §7.2 alongside the rank-type findings — the two
need the same session — then delete it. **It must not reach 1.0.**

---

## 8. Commands and menus

### 8.1 Ranks registers commands at runtime, not by attribute

**`[Command]` cannot implement FR-CMD-01.** Attribute arguments are compile-time
constants, and FR-CMD-01 makes the command name, its permission and whether it
registers at all operator-editable in `commands.jsonc`. An attribute also has no
way to express "an empty `Command` string means do not register".

`ICommandService.RegisterCommand` takes every one of those at runtime:

```csharp
Guid RegisterCommand(string commandName, CommandListener handler,
                     bool registerRaw = false, string permission = "",
                     string helpText = "SwiftlyS2 registered command");
```

So registration is a loop over the bound config, in `Load`:

```csharp
foreach (var (key, info) in _commands.CurrentValue.All)
{
    if (string.IsNullOrWhiteSpace(info.Command)) continue;   // operator removed it

    var guid = Core.Command.RegisterCommand(
        info.Command, _handlers.For(key), registerRaw: true,
        permission: info.Permission,
        helpText: Core.Localizer[$"command.{key}.help"]);

    _registered.Add(guid);

    foreach (var alias in info.Aliases)
        Core.Command.RegisterCommandAlias(info.Command, alias, registerRaw: true);
}
```

FR-CMD-01 puts `Command`, `Aliases` and `Permission` in `commands.jsonc` and
nothing else. **Help text is not a config field** — it is player-facing, so it
resolves through `Core.Localizer` like every other player string (FR-CMD-05),
which also makes it translatable.

Keep the returned `Guid`s. Plugins auto-unregister on unload, so `Unload` needs
no loop — but a **config reload that changes a command name does**, and
`UnregisterCommand(Guid)` is the only way to drop the old name without a full
reload. Until that path exists, `commands.jsonc` is restart-only for renames
(§3.4).

> `helpText` has a framework default (`"SwiftlyS2 registered command"`), so a
> missing localizer key degrades to that rather than to an empty string — ugly
> in `sw_help` output, but not a failure.

`[Command]` remains correct for any command whose name is genuinely fixed. Ranks
has none.

**`permission` is checked before the handler runs**, whichever registration path
is used, so an admin command needs no in-body permission check.

```csharp
public void OnRank(ICommandContext ctx)
{
    if (!ctx.IsSentByPlayer) { ctx.Reply("Players only."); return; }
    var player = ctx.Sender!;        // null from server console — check IsSentByPlayer first
}
```

### 8.2 ⛔ Never register a command raw

`registerRaw` stays **`false`**, on the primary and on every alias.

A command then registers as **`sw_rank`** in console, and the framework's chat
layer owns the `!rank` / `/rank` triggers. That is the whole point: `sw_` is the
namespace every SwiftlyS2 plugin shares, so two plugins wanting `rank` collide
visibly inside it rather than one silently taking the bare console name from the
other. First registration wins and the loser never fires, so the collision is
worth keeping where an operator can see it.

```csharp
_core.Command.RegisterCommand(info.Command, handler, registerRaw: false, …);
_core.Command.RegisterCommandAlias(info.Command, alias, registerRaw: false);
```

The operator still types `!rank` — `commands.jsonc` names the chat trigger, and
the console name is derived from it. Say which surface is which in the shipped
comments.

### 8.3 Localization and menus

Player-facing strings resolve through **`Core.Localizer`**. `en.jsonc` must stay
**key-complete** — a missing key renders the literal marker `{lang}.{key}` to the
player rather than falling back to English, so a partial contributed translation
is worse than no translation.

**Build every menu through `Core.Menu` (`IMenuService`)** — the 1.5.0 framework
(FR-CMD-06). The replacement map, because the old names are the ones in every
existing plugin and tutorial:

| Use | Replaces |
|---|---|
| `Core.Menu` → `IMenuService` | `Core.MenusAPI` → `IMenuManagerAPI` |
| `CreateMenu(id)` → `IMenuBuilder` | `CreateBuilder()` → `IMenuBuilderAPI` |
| `.Add(IMenuComponent)` | `.AddOption(IMenuOption)` |
| `IMenu` / `IMenuSession` | `IMenuAPI` |

The right-hand column is `[Obsolete]` in 1.5.0 — *"the classic menu system is
deprecated and will be removed in future versions. Use Core.Menu (IMenuService)
instead."*

```csharp
var menu = Core.Menu.CreateMenu("ranks.main")      // id is also the keybind config scope
    .WithTitle(Core.Localizer["menu.rank.title"])
    .WithItemsPerPage(8)
    .Add(new TextComponent(/* current rank, points */))
    .Add(new ProgressBarComponent(/* progress to next band */))
    .Add(new SubmenuComponent(/* !top */))
    .Build();
```

Three consequences worth holding on to:

- **A player has at most one open menu across every plugin.** Opening core's
  menu closes someone else's; that is normal, not an error to guard against.
- **Menus are released automatically when the plugin unloads** — each plugin
  gets its own `IMenuService` over core-owned registries — so `Unload` needs no
  menu teardown.
- `PagedCollectionComponent<T>` already pages, so the leaderboard must not hand-roll it.

Core ships one root menu of categories and modules hang entries off it
(FR-CMD-04). Nest with `SubmenuComponent`, never `ButtonComponent` + `Open()`:
`Open()` starts a new root with no back-stack, so the player's back key would
have nothing to return to. The same adoption rule is what lets a module's menu
sit under Statistics and still come back to core's.

---

## 9. Shared API — provider side

Build the implementation in `Load`, register the built instance in
`ConfigureSharedInterface`:

```csharp
public override void ConfigureSharedInterface(IInterfaceManager im)
    => im.AddSharedInterface<IRanksApi, RanksApi>("Ranks.Api.v1", _api);
```

`AddSharedInterface` throws on a duplicate key, but the rebuild disposes the
manager (clearing the key set) before re-running registration, so
re-registration is safe.

**Reset the provider instance on reconfiguration**, or event handlers accumulate
across rebuilds. The stable *Shared API* guide states the same rule from the
consumer side: *"Since registration can re-run on hot reload, unsubscribe before
re-subscribing to provider events in `UseSharedInterface`, or you'll get
duplicate handlers."*

### 9.1 Shipping the contract DLL

```xml
<Target Name="CopyContract" AfterTargets="Build;Publish">
  <Copy SourceFiles="$(ContractOutput)" DestinationFolder="$(OutDir)resources/exports" />
</Target>
```

**Do not add `resources/exports` to a static `<Content Include>` glob.** Item
globs are evaluated during project evaluation, before any target runs, so a
build-generated DLL is not on disk yet. It works on a dev machine (a previous
build left the file behind) and ships **nothing** from a clean CI checkout —
producing a `ReflectionTypeLoadException` at runtime with no build failure.

---

## 10. Module authoring

### 10.1 Use `RanksModuleBase`

A **data and lifecycle base** (FR-API-08), not a feature base. It carries the
API and core handles, the database-coordination members (`ConnectionName`,
`Table(baseName)`, `RegisterMigrations(assembly)`), `virtual` no-op handlers for
core's lifecycle events, and a `Register()` / `Unregister()` pair.

It is **not** VIPCore's `VipFeatureBase`. That class registers a per-player
toggle into a shared VIP menu; a Ranks module registers a whole screen it renders
itself (`RegisterMenuEntry`, FR-CMD-04), and Ranks has no per-player toggle at
all. Only the mechanical shape is shared.

`Unregister()` drops the module's menu entries unconditionally — an entry that
outlives its module is a delegate into an unloaded assembly load context.

It deliberately does **not** acquire the API or isolate the JIT — it cannot. A
constructor parameter typed `IRanksApi` means the caller already holds one, so
§10.2's isolation stays in the module's own `BasePlugin`.

Subscription goes in `Register()`, not the constructor — see §10.4.

### 10.2 ⚠️ An absent contract DLL kills the entire host

The CLR resolves every type a method *names* when it JITs that method, not when
execution reaches the line. On a server without `Ranks`:

- a `TryGetSharedInterface<IRanksApi>(…)` call site — **or merely a field typed
  `IRanksApi?`** — throws `FileNotFoundException` when its enclosing method is
  prepared;
- the throw surfaces at the **caller's** frame, so the method's own try/catch
  never sees it.

**Which callback it escapes from decides the blast radius** (verified in
`PluginManager.cs`): core wraps `Load` and `Unload` per plugin, so a throw there
fails only this plugin. It does **not** wrap `ConfigureSharedInterface`,
`UseSharedInterface`, `OnSharedInterfaceInjected` or `OnAllPluginsLoaded` —
`RebuildSharedServices` and `NotifyAllPluginsLoaded` run those loops bare, and an
escape propagates to `StartupService`'s constructor as
`Hosting failed to start`, taking **every plugin on the box** with it.
Acquisition happens in exactly those unguarded callbacks.

`Try…` / `Has…` look defensive and are not. Isolate: nothing outside a
`[MethodImpl(MethodImplOptions.NoInlining)]` helper may name the contract type,
every other method reads a plain `bool`, and the `try`/`catch` goes at the **call
site** — inside the helper it is JITted too late to fire.

`Unload` is the classic miss — `_api.PointsChanged -= handler` needs the contract
loaded to unwire. Core catches it there, so the cost is a failed unload and a
plugin left `Indeterminate` rather than a dead host, but it still needs the same
isolated-helper treatment. When auditing, grep the **field name** across the
whole plugin.

### 10.3 Gate on readiness

```csharp
if (api.IsCoreReady) OnRanksReady();
else                 api.CoreReady += OnRanksReady;
```

Check first, then subscribe — racing it the other way round misses the event.

### 10.4 ⚠️ Do not copy VIPCore's subscription shape

Several `SwiftlyS2-Plugins/VIPCore` modules reset a registration flag at the top
of `UseSharedInterface` and re-run a method ending in `_vipApi.PlayerLoaded += …`.
`RegisterFeature` is idempotent (core overwrites `_features[key]`); the `+=`
lines are **not**, and are only removed in `Unload`. On a busy server that is a
handler leak with duplicate invocations. Subscribe in `Load`, or guard with a
flag the handshake does not reset.

### 10.5 Module database checklist

- Same connection (`ConnectionName`), own tables, own ledger — but **core runs
  the migrations**. Call `RegisterMigrations(assembly)` from the handshake, not
  from `Load`, where there is no API yet.
- Reference FluentMigrator as a plain `PackageReference` so it ships beside your
  DLL. `ExcludeAssets="runtime"` leaves the module unable to load, and
  `PrivateAssets="all"` silently drops the package from publish. Never copy it to
  `resources/exports`.
- Runtime query names come from `ResolveTableName` — never string concatenation.
  Table names *inside migrations* are rewritten by core, so write the base name
  and let it apply the affixes. **The base name carries the `ranks_` namespace**
  — FR-DB-03 affixes a base of `ranks_players`, so a module writes
  `Create.Table("ranks_weapon_stats")` and gets `awp_ranks_weapon_stats` on a
  prefixed server. Dropping the namespace puts module tables outside the group
  an operator filters on.
- **Avoid `Execute.Sql` for DDL.** It has no expression tree, so core cannot
  rewrite it and your tables will miss the affixes on a prefixed server.
- Every upserted table needs the unique/PK its upsert depends on. Without one,
  MySQL's `ON DUPLICATE KEY UPDATE` silently inserts duplicates forever while
  PostgreSQL and SQLite reject the statement outright.
- Subscribe to `PlayersPurged` and sweep your own rows (FR-DB-08) — nothing else
  ever will.
- Follow core's period (FR-DB-10) rather than keeping a clock: stamp each write
  with the period captured on the game thread when the figures were earned, flush
  what you hold with `OldPeriod` in `OnPeriodChanged`, and clear the player's
  current period in `OnPlayerReset`. Never change an existing table's key to add
  a period — add a `_period` or `_history` table with the composite key inline.

---

## 11. Threading

**Game state — players, entities, pawns, controllers — is only safe to touch
from the main thread.** Thread-unsafe methods carry
**`[ThreadUnsafe]`** in source, so the answer is checkable rather than folklore.
`Core.IsGameThread` answers "am I on it now" and is worth asserting at the top
of any helper reachable from both a hot path and a background continuation; the
failure mode otherwise is rare corruption with no stack pointing at the caller.

**Prefer the `Async` counterpart over marshalling by hand.** Where one exists it
marshals for you, and it is what the stable guide recommends. Every player-facing
send has one — `SendChatAsync`, `SendCenterHTMLAsync`, `KickAsync`,
`ExecuteCommandAsync`, `TeleportAsync`, `SwitchTeamAsync`, and
`IGameEventService.FireAsync`. Reach for `Core.Scheduler` only when there is no
`Async` variant.

### ⚠️ Never pass an async callback to `NextTick` / `NextWorldUpdate`

The `Func<Task>` / `Func<Task<T>>` overloads of `NextTick`, `NextTickAsync`,
`NextWorldUpdate` and `NextWorldUpdateAsync` are **`[Obsolete]` and throw
`InvalidOperationException` at runtime**. An async callback can resume on a
different thread, which defeats the entire guarantee these methods exist to
provide.

```csharp
Core.Scheduler.NextTick(async () => await SomethingAsync());   // THROWS
Core.Scheduler.NextTick(() => ApplyToPawn(record));            // correct
```

This is why the reflection dump shows several same-looking `Func\`1` overloads —
the extra ones are the obsolete traps, not useful alternatives.

```csharp
var steamId = player.SteamID;            // capture primitives BEFORE the hop

Task.Run(async () =>
{
    try
    {
        var record = await LoadRecordAsync(steamId);
        Core.Scheduler.NextTick(() =>
        {
            var p = Core.PlayerManager.GetAllPlayers()
                        .FirstOrDefault(x => x.SteamID == steamId);
            if (p?.Controller is not { IsValid: true }) return;   // they may have left
            _records[steamId] = record;
        });
    }
    catch (Exception ex) { logger.LogError(ex, "Load failed for {SteamId}", steamId); }
});
```

- `NextTick` for data; `NextWorldUpdate` for entity/pawn modification.
- `Repeat`, `RepeatBySeconds` and friends **fire the first callback immediately**,
  not after one period — the stable guide documents `Repeat(64, …)` as "runs
  immediately, then every 64 ticks". All six timer methods return a
  `CancellationTokenSource`; pair with `StopOnMapChange` where a timer should not
  survive a map change.
- Second-based timers are still driven by game ticks, so they get inaccurate as
  the interval approaches one tick (~15 ms). The save pass at 300 s is nowhere
  near that; a sub-second timer would be.
- `AddTimer(ctx => …)` gives per-execution control — return `TimerStep.Stop()`,
  `WaitForSeconds`, `WaitForTicks`, `WaitForMilliseconds` or `Spin()`.
  `ITimerContext.ExecutionCount` is a `ulong` starting at `0`.
- `IsValid` is not "not null" — it requires a pawn, so it is the wrong gate in
  connect and disconnect handlers.
- Don't fire-and-forget gameplay-critical calls; await them or handle failure.

---

## 12. Logging

| Level | Gated behind `DetailedLogging` | Use |
|---|---|---|
| `Debug` | yes | scoring decisions — gate rejected, multiplier applied |
| `Information` | yes | flow tracing |
| `Warning` | **no** | recoverable persistence failure, missing config |
| `Error` | **no** | data-loss risk, failed save, schema unusable |
| thrown exceptions | **no** | never swallowed to honour the flag |

`General.DetailedLogging` is a `bool` in `config.jsonc`, default **`false`**.

**Read it from `CurrentValue` at the call site, never from a cached copy** — it
lives in a hot-reloaded file, so an operator flipping it must take effect on the
next log call without a restart. A field captured at construction silently pins
the value, and the symptom is "I turned logging on and nothing happened".

```csharp
// Gated — the check short-circuits before any string work happens
if (_cfg.CurrentValue.General.DetailedLogging)
    logger.LogInformation("Awarding {Points} for {EventKey} to {SteamId}",
                          points, eventKey, steamId);

// Never gated, and always pass the exception object
logger.LogError(ex, "Failed to load record for {SteamId}", steamId);
logger.LogWarning("Core ranking logic is disabled; no module is writing points.");

// Structured placeholders, not interpolation
logger.LogInformation($"Awarding {points}");   // BAD — builds the string regardless
```

A one-line helper is worth it given how many call sites there are, but it must
take a delegate or be a plain `if` — passing an already-formatted string defeats
the gate:

```csharp
// The gate must run BEFORE the arguments are evaluated.
public void Detail(string template, params object?[] args)
{
    if (_cfg.CurrentValue.General.DetailedLogging) _logger.LogInformation(template, args);
}
```

**Nothing is downgraded or suppressed to honour the flag.** `DetailedLogging`
decides whether *successful, expected* flow is narrated. A failure is a failure
at `false`: warnings still print, errors still print with their exception
object, and no `catch` becomes silent because verbose logging is off.

Anything on the award path is gated without exception — it runs at kill rate.

`UseCoreRankingLogic = false` logs **one** ungated warning at
`OnAllPluginsLoaded`. Core has no registry and cannot tell whether a rating
module is present, so the warning is unconditional and must say so.

---

## 13. Build and release

```bash
dotnet publish -c Release        # → build/publish, plus a distributable zip
```

Releases are cut by `.github/workflows/release.yml`, which takes no inputs. One run
builds core and all four modules from the same commit and publishes **a single
release**, `v<version>`, with all five zips attached. Modules are versioned
independently — their zips carry their own version in the filename — but they get no
tag and no release of their own. The version and notes come from core's
`CHANGELOG.md` and must match `PluginMetadata.Version`.
`docs/verify-release-workflow.sh` exercises that logic with `gh` stubbed.

Before tagging:

- [ ] `Ranks.Contract.dll` is physically present in `resources/exports/` in a
      **clean** checkout build
- [ ] No `FluentMigrator*.dll` in any `resources/exports/` — SwiftlyS2 forces
      exports onto every plugin
- [ ] The resolved `SwiftlyS2.CS2` version is recorded in the release notes —
      the reference floats (`*-*`), so nothing else ties a bug report to a
      framework build
- [ ] Migrations verified on MySQL, PostgreSQL **and** SQLite, against both a
      fresh and an existing database
- [ ] An unreachable or misconfigured `Database.Connection` **fails to load**
      with an error naming the connection (FR-DB-02)
- [ ] ⚠️ Rank-type values confirmed on a live server
- [ ] 🧪 §7.4 harness **deleted** — no `Experiment` config block, no signature,
      no `AddHook` anywhere in the tree. Grep for `UpdateResourceData`
- [ ] §7.4's probe findings recorded in §7.2, so the next person does not
      re-run the investigation
- [ ] ⚠️ `EventCsWinPanelMatch` confirmed to **fire** (it is confirmed declared, and
      carries no winner — §6.4)
- [ ] No identifier named `rank` (reserved in MySQL 8.0+)
- [ ] Command names and aliases grepped against sibling plugins — first
      registration wins silently and the loser never fires

Real-database verification is cheap and catches what a build cannot:

```bash
docker run -d -e MARIADB_ROOT_PASSWORD=test -e MARIADB_DATABASE=ranks \
           -p 33061:3306 mariadb:11
```

Three prefixes against one schema should yield three isolated table sets, three
ledgers with one row each, and prefixed index names.

---

## Appendix A — Verified API surface

Reflected from `SwiftlyS2.CS2.dll` 1.4.13 on 2026-10-12. Re-run before any
framework bump.

### `ISwiftlyCore` (`SwiftlyS2.Shared`)

```
Event IEventSubscriber          Engine IEngineService        GameEvent IGameEventService
NetMessage INetMessageService   Helpers IHelpers             GameHooks IGameHooks
Game IGameService               Command ICommandService      ConsoleOutput IConsoleOutputService
EntitySystem IEntitySystemService  ConVar IConVarService     Configuration IPluginConfigurationService
GameData IGameDataService       PlayerManager IPlayerManagerService   Memory IMemoryService
LoggerFactory / Logger          Profiler IContextedProfilerService    Trace ITraceManager
Scheduler ISchedulerService     Database IDatabaseService    Translation ITranslationService
Localizer ILocalizer            Permission IPermissionManager         Registrator IRegistratorService
MenusAPI IMenuManagerAPI        CommandLine ICommandLine     GameFileSystem IGameFileSystem
PluginManager IPluginManager    StringTable IStringTableService
PluginPath / CSGODirectory / GameFilesDirectory / GameDirectory / PluginDataDirectory : string
IsGameThread : bool
```

Both `Translation` and `Localizer` exist; FR-CMD-05 specifies `Core.Localizer`.

> **This dump predates `Core.Menu`.** It lists `MenusAPI IMenuManagerAPI`
> because 1.4.13 had only the classic menu system, and omits
> `Menu IMenuService` because that arrived in 1.5.0. Read it as a record of
> 1.4.13, not as guidance \u2014 FR-CMD-06 requires `Core.Menu`.

### `IDatabaseService` (`SwiftlyS2.Shared.Database`)

```csharp
string                 GetConnectionString(string connectionName);
DatabaseConnectionInfo GetConnectionInfo(string connectionName);
IDbConnection          GetConnection(string connectionName);
```

### `DatabaseConnectionInfo` (`record struct` — copied by value)

```csharp
string Driver;   string Host;  string Database;  string User;
string Pass;     uint Timeout; ushort Port;      string RawUri;
```

`Driver` is a **string** — this is what §4.2's dispatch rule rests on. Stable
docs list a ninth field, `Options`, which 1.4.13 does not have (Appendix B).

### `ISchedulerService` (`SwiftlyS2.Shared.Scheduler`)

```csharp
void NextTick(Action);                 Task NextTickAsync(Action);
void NextWorldUpdate(Action);          Task NextWorldUpdateAsync(Action);
CancellationTokenSource Delay(int delayTick, Action);
CancellationTokenSource Repeat(int periodTick, Action);
CancellationTokenSource DelayAndRepeat(int delayTick, int periodTick, Action);
CancellationTokenSource DelayBySeconds(float, Action);
CancellationTokenSource RepeatBySeconds(float, Action);
CancellationTokenSource DelayAndRepeatBySeconds(float, float, Action);
CancellationTokenSource AddTimer(Func<...>);
void StopOnMapChange(CancellationTokenSource cts);
```

### `IPlayer` (`SwiftlyS2.Shared.Players`)

```
PlayerID int      SessionId ulong    UserID int        Slot int
Name string       IsFakeClient bool  IsAuthorized bool ConnectedTime uint
SteamID ulong     UnauthorizedSteamID ulong            IsAlive bool
Controller / RequiredController : CCSPlayerController
Pawn / RequiredPawn : CBasePlayerPawn    PlayerPawn / RequiredPlayerPawn : CCSPlayerPawn
PressedButtons GameButtonFlags   IPAddress string   VoiceFlags VoiceFlagValue
PlayerLanguage Language          IsFirstSpawn bool   IsValid bool
SendMessage / SendChat / SendCenter / SendCenterHTML / SendNotify / SendAlert / SendConsole (+Async)
Kick(string reason, ENetworkDisconnectionReason)
```

The property is **`Name`** — there is no `PlayerName`.

---

## Appendix B — Stable docs vs the pinned assembly

Where the published documentation and the resolved assembly disagree.
Audited 2026-10-12 against 1.4.13, **re-checked 2026-10-26 against the 1.5.0
prereleases** the `*-*` reference now resolves.

| Item | Stable docs | Assembly | Action |
|---|---|---|---|
| `DatabaseConnectionInfo` fields | 9 — adds **`Options`** (parsed driver keywords, included in `GetConnectionString`) | 1.4.13: 8. **1.5.0: 9 — `Options` is present** | **Resolved by the bump.** `Options` exists and is usable; §4.1 describes what an operator sets through it. |
| `DatabaseConnectionInfo` kind | `record struct` | `record struct` | Agreed. Appendix A previously said "record"; it is a struct, so it is copied by value. |
| Shared-interface key casing | Best practice is `PluginName.ServiceName.v1` — **lowercase `v`**, example `"Economy.Api.v1"` | n/a | **Resolved — docs win.** FR-API-01 is `Ranks.Api.v1`. |

### The key-casing question, settled

FR-API-01 is **`Ranks.Api.v1`**, lowercase. The evidence, for the record:

| Source | Key |
|---|---|
| Stable docs, *Shared API* → Best Practices | `PluginName.ServiceName.v1` |
| Stable docs, worked example | `"Economy.Api.v1"` |
| `SwiftlyS2-Plugins/VIPCore` | `"VIPCore.Api.v1"` |
| `SwiftlyS2-Plugins/Admins` | `"Admins.Admins.V1"`, `"Admins.Menu.V1"`, … |

The documentation and two of three real-world data points use lowercase;
`Admins` is the outlier. An earlier revision of this spec followed `Admins` —
if you find a capital `V` anywhere in this repo, it is stale.
