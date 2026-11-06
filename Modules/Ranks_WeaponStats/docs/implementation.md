# Ranks_WeaponStats — implementation

> **Status:** design. Provisional on two open core-API items, both flagged
> inline. Everything else is settled against verified API surface.
> Companion to [features.md](features.md); core conventions it inherits live in
> the [implementation guide](../../../docs/implementation-guide.md).

This module is the **floor case for a table-owning module**: it writes to core's
database, obeys core's purge, and asks core for nothing else. If the shape below
is awkward, `RanksModuleBase` is wrong — not this module.

---

## 1. Layout

```
Modules/Ranks_WeaponStats/
├── README.md
├── docs/
│   ├── features.md
│   └── implementation.md
├── Ranks_WeaponStats.csproj
├── resources/
│   ├── templates/config.jsonc
│   ├── templates/commands.jsonc
│   └── translations/en.jsonc
└── src/
    ├── Ranks_WeaponStats.cs      # BasePlugin, lifecycle, contract isolation
    ├── Api/RanksBridge.cs        # the ONLY file naming IRanksApi
    ├── Config/Config.cs
    ├── Migrations/               # four, the last adding the per-period tables
    ├── Menus/WeaponStatsMenus.cs
    ├── Stats/Collector.cs        # game-event handlers, hot path
    ├── Stats/SessionStats.cs
    ├── Stats/Writer.cs           # off-thread, additive upserts, one ordered queue
    └── Commands/StatsCommands.cs
```

`Ranks_WeaponStats` — underscore, in the folder, the project and the
`[PluginMetadata] Id`. Core derives the migration ledger from that id.

---

## 2. Lifecycle

### 2.1 ⚠️ Contract isolation comes first

This is the one mistake that takes the **whole server** down, not just this
module. Per implementation guide §10.2, the CLR resolves every type a method
*names* when it JITs that method. On a box without `Ranks`, a field typed
`IRanksApi?` is enough to throw `FileNotFoundException` at the caller's frame,
and SwiftlyS2 reports `Hosting failed to start` for **every plugin installed**.

So exactly one file names the contract, and every member that touches it is
`[MethodImpl(MethodImplOptions.NoInlining)]`:

```csharp
internal sealed class RanksBridge
{
    private object? _api;                       // deliberately not IRanksApi
    public bool IsAvailable { get; private set; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryAcquire(ISwiftlyCore core) { /* names IRanksApi, nothing else does */ }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unwire() { }                    // Unload needs the type too — easy to miss
}
```

Everywhere else reads `bridge.IsAvailable`, a plain `bool`.

**Audit rule:** grep the *field name* across the whole plugin, not the type
name. `_api.PointsChanged -= handler` in `Unload` is the classic survivor.

### 2.2 Where things go

| Step | Work |
|---|---|
| Constructor | nothing |
| `Load` | config, game-event handlers, `Core.Registrator.Register(collector)`, core-event subscriptions, flush timer |
| `UseSharedInterface` | acquire the API through the bridge; `RegisterMigrations(assembly)` |
| `OnAllPluginsLoaded` | if `Ranks` is absent, log one ungated warning and stay idle |
| `Unload` | final flush, then unwire |

`RegisterMigrations` goes in the handshake, not `Load` — there is no API yet in
`Load` (guide §10.5). Core dedupes on `(assembly, pluginId)`, so the handshake
re-running on every other plugin's load is harmless.

### 2.3 Handlers outside the plugin class must be registered

`Collector` holds the `[GameEventHandler]` methods, so it is invisible to the
framework until:

```csharp
Core.Registrator.Register(_collector);   // exactly once
```

Registering twice double-counts every shot. Signatures must match the expected
delegate exactly or the method is silently skipped.

### 2.4 Hot reload

`Load(hotReload: true)` runs with players connected. Re-adopt them from
`Core.PlayerManager`, filtering `IsFakeClient`, rather than waiting for a
steam-authorize event that already fired.

---

## 3. Configuration

```csharp
Core.Configuration.InitializeJsonWithModel<Config>("config.jsonc", "WeaponStats");
Core.Configuration.InitializeJsonWithModel<CommandsConfig>("commands.jsonc", "Commands");
```

Validate at load, clamping rather than throwing:

| Key | Rule |
|---|---|
| `Tracking.CountFriendlyFire` | bool, no validation — default `false` |
| `Tracking.CountFriendlyFire` | bool, no validation — default `false` |
| `Database.FlushIntervalSeconds` | `>= 0`; `0` disables the timer |
| `Display.MinShotsToList` | `>= 0` |
| `Display.MinShotsToList` | `>= 0` |

**All three `Tracking` defaults are the binder's own `default(bool)`.** That is
correct for each of them now: `CountFriendlyFire` was always meant to be off,
and the two bot switches default off to match core's `Rank.BotsEnabled`, which
gates bot interactions out of core's counters (FR-PTS-03). An absent or
malformed key therefore lands on the reconciling value rather than away from it,
so none of them needs an explicit initialiser.

The two flags are read on the hit and kill paths, so they follow guide §3.2:
hold a `volatile` snapshot reference swapped in the reload handler, not
`CurrentValue` per access. `General.DetailedLogging` rides the same snapshot —
which keeps it live, since the snapshot is reassigned by `OnChange` rather than
captured at construction.

---

## 4. Database

### 4.1 Migration

One migration, one table pair. The base name **includes the `ranks_`
namespace** — FR-DB-03's resolver adds only the operator's prefix and suffix
around a base name that is already `ranks_players` for core, so a module writes
`ranks_weapon_stats` and gets `awp_ranks_weapon_stats` on a prefixed server:

```csharp
[Migration(20261012120000)]
public sealed class M0001_Tables : Migration
{
    public override void Up()
    {
        Create.Table("ranks_weapon_stats")
            .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
            .WithColumn("weapon").AsString(64).NotNullable().PrimaryKey()
            .WithColumn("shots").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("hits").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("damage").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("kills").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("deaths").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("headshots").AsInt64().NotNullable().WithDefaultValue(0);

        Create.Table("ranks_hit_stats")
            .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
            .WithColumn("hitgroup").AsInt16().NotNullable().PrimaryKey()
            .WithColumn("hits").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("damage").AsInt64().NotNullable().WithDefaultValue(0);
    }

    public override void Down()
    {
        Delete.Table("ranks_hit_stats");
        Delete.Table("ranks_weapon_stats");
    }
}
```

Four things here are deliberate:

- **The composite PK is declared inline**, which guide §4.5 authoring rule 3
  makes mandatory rather than stylistic: MySQL names every primary key
  `PRIMARY`, so a `Schema.Table(t).Constraint("pk_x").Exists()` guard is always
  false and the follow-up `ADD CONSTRAINT … PRIMARY KEY` fails with *Multiple
  primary key defined*. Inline is also the only portable composite key on
  SQLite. The PK itself is non-negotiable — without it the upsert in §4.2
  silently duplicates on MySQL and outright fails on PostgreSQL and SQLite.
- **Migration ids only need to be unique within this assembly.** The module has
  its own ledger (FR-DB-05), so unlike `Admins` — which hand-coordinates
  timestamps across four plugins sharing one stock `VersionInfo` — there is
  nothing to collide with. The `YYYYMMDDHHmm` form is still required by guide
  §4.5 rule 1, and the number is never reused or renumbered.
- **There is deliberately no `Schema.Table(...).Exists()` guard.** Guide §4.5
  rule 2 asks for one, and it is unsafe here until core's processor decorator
  rewrites existence checks as well as expressions — see §4.4. The per-module
  ledger already prevents a re-run, which is what the guard would otherwise buy.
- **No `Execute.Sql`.** It has no expression tree, so core cannot rewrite it and
  the tables would miss their affixes on a prefixed server.

### 4.2 The additive upsert

Core writes **absolute** values because its in-memory record is authoritative
(FR-DB-06). This module holds only **session deltas** and so writes additively.
That is the right shape for it, not a shortcut: there is no read on connect, and
two servers sharing a database accumulate instead of clobbering.

```sql
-- MySQL / MariaDB
INSERT INTO {t} (steam_id, weapon, shots, hits, damage, kills, deaths, headshots)
VALUES (@SteamId, @Weapon, @Shots, @Hits, @Damage, @Kills, @Deaths, @Headshots)
ON DUPLICATE KEY UPDATE
    shots = shots + VALUES(shots), hits = hits + VALUES(hits), ...;

-- PostgreSQL / SQLite
INSERT INTO {t} (...) VALUES (...)
ON CONFLICT (steam_id, weapon) DO UPDATE SET
    shots = {t}.shots + EXCLUDED.shots, hits = {t}.hits + EXCLUDED.hits, ...;
```

> **Keep `VALUES()` on the MySQL branch.** MySQL 8.0.20 deprecates it in favour
> of the `AS new` row alias — which MariaDB does not support at all. `VALUES()`
> works on both; the alias works on one.

Qualify with the table name on the PostgreSQL branch (`{t}.shots`, not `shots`)
or the reference is ambiguous against `EXCLUDED`.

**`ranks_hit_stats` takes the identical shape**, keyed `(steam_id, hitgroup)`
with `hits` and `damage` accumulating the same way, and `ranks_weapon_hit_stats`
again keyed `(steam_id, weapon, hitgroup)`. They are further statements, not
special cases.

All three are written from the same per-weapon counters inside one transaction,
which is what makes `ranks_weapon_hit_stats` summed over weapons equal
`ranks_hit_stats` rather than two code paths counting the same events.

Table names come from `ResolveTableName` at query-build time, never string
concatenation.

### 4.3 Failure handling

A failed flush must not lose the deltas. The writer owns a retry buffer and
merges the failed batch back into it **off-thread** — never into the live
session objects, which belong to the game thread (§5).

Log at `Error` with the exception object; this is a data-loss path and is not
gated behind `DetailedLogging`.

### 4.4 Existence checks must be affixed — resolved core-side

`Schema.Table("x").Exists()` calls `IMigrationProcessor` directly through
`IQuerySchema`; it is **not** an expression, so a decorator that rewrites only
expressions answers it for the unaffixed name.

The failure is silent and permanent in exactly the setup FR-DB-03 exists for.
One database, a default install plus an `awp_` one: the `awp_` ledger is
separate so the migration runs, the guard asks whether `ranks_weapon_stats`
exists, finds the **default install's** table, skips creation, and records the
migration as applied. `awp_ranks_weapon_stats` is never created and
`MigrateUp()` never retries.

**Core's decorator now covers the whole `IQuerySchema` surface** — guide §4.5
requires it, because the same trap applies to core's own migrations. So the
guards in §4.1 are correct as written, and a module should use them normally.

> If you are reading this while writing core's decorator: covering the
> expressions but not the queries is a half-decorator, and the asymmetry is
> invisible until a second affixed install appears on one database.

### 4.5 Per-period tables and the dual write (FR-DB-10)

`M202611060100_PeriodTables` adds `ranks_weapon_stats_period`,
`ranks_hit_stats_period` and `ranks_weapon_hit_stats_period`: the same columns as
their all-time tables plus `period BIGINT NOT NULL`, keyed by the all-time key plus
`period`, inline, each table guarded on its own. It is a new migration rather than an
edit to an applied one, and nothing is backfilled.

`StatsDelta` carries `long? Period`, stamped when the batch is cut on the game thread:
core's `CurrentPeriod` while `ArePeriodsEnabled`, else null. The collector stays
contract-free — it asks through a `Func<long?>` whose target checks
`RanksBridge.IsAvailable` before the `[NoInlining]` bridge member, exactly as it asks
for core's play gate. Against a Ranks older than 1.1.0 that member fails at JIT with
`MissingMethodException`; the plugin catches it once, logs a warning, and answers null
from then on, which is 1.0.2's behaviour.

`Writer.Write` upserts each delta into the all-time tables with the statements above,
unchanged, and — when `Period` is set — into the per-period twin with the same
statement and `period` added to the key, in the same transaction. A null `Period` never
touches the per-period tables, so with seasons off nothing new is written.

Reads take a `long? period`: null reads the all-time tables with the original
statements, a value reads `WHERE steam_id = @steam_id AND period = @period`.
`WeaponStatsApi` adds the live session only for `AllTime` or the period being written
now.

### 4.6 One ordered queue for every write

Every database operation the writer performs — flush, reset, purge, orphan reconcile —
is queued on a single `ContinueWith` chain and runs one at a time, in the order it was
handed over. A semaphore taken inside `Task.Run` would not do: two tasks started in
order can still reach it out of order, and a reset overtaking an earlier flush is
exactly the resurrection §8.2 exists to prevent. The retry buffer is touched only from
queued operations, so it needs no lock of its own. Reads are not queued.

---

## 5. The hot path

All three game events fire on the game thread, so the accumulation structures
need no synchronisation at all — provided nothing else touches them.

```csharp
private readonly SessionStats?[] _slots = new SessionStats?[64];
```

Core measured the slot array holding the **record reference** at 2.8–3.3 ns
against 11.6 ns for slot-to-identity-then-dictionary (guide §5). The same
applies here. `IPlayer.Slot` is an `int`; the slot is cleared on disconnect,
which is the invariant that makes the array safe.

```csharp
internal sealed class SessionStats
{
    public ulong SteamId;
    public readonly Dictionary<string, WeaponCounters> Weapons = new();
    public readonly long[] HitGroupHits   = new long[12];   // see below
    public readonly long[] HitGroupDamage = new long[12];
}
```

**The hitgroup array is 12 long, not 11.** `HitGroup_t` runs `GENERIC = 0`
through `SPECIAL = 11` with a hole at `UNUSED = 9`, so indexing by the raw enum
value needs 12 slots — an 11-element array overflows on `SPECIAL`. `INVALID` is
`-1` and the value arrives from the engine, so it must be assumed reachable
whether or not it is observed in practice:

```csharp
var hg = (int)e.ActualHitgroup;
if ((uint)hg < 12) { stats.HitGroupHits[hg]++; stats.HitGroupDamage[hg] += e.ActualDmgHealth; }
```

Slot 9 stays zero forever and is skipped at flush time.

Unlike a death with no weapon (§5.1), an out-of-range hitgroup is simply **not
counted** — there is no hitgroup to attribute it to, and the hit-stats table has
no reconciliation invariant against core, which stores no hitbox data at all.
The weapon row for the same event is still recorded.

**Read the `Actual*` properties throughout.** `DmgHealth` is a `short`;
`Health`, `Armor` and `Hitgroup` are `byte`. A server granting more than 255 HP
silently corrupts the narrow ones.

### 5.1 Never drop an event

The module's counters must reconcile against core's (features.md §1), so the
only correct response to an awkward event is to file it somewhere — never to
skip it.

```csharp
// weapon_fire reports the entity designer name (weapon_ak47); player_death reports the short form (ak47).
private const string UnknownWeapon = "unknown";

// Death sources that are not weapon entities, so they must never be given the prefix.
private static readonly HashSet<string> NonWeaponSources =
    new(StringComparer.Ordinal) { "world", "planted_c4", "inferno", "trigger_hurt" };

private static string Normalize(string? weapon)
{
    if (string.IsNullOrWhiteSpace(weapon)) return UnknownWeapon;

    var w = weapon.Trim();
    if (!w.StartsWith("weapon_", StringComparison.Ordinal) && !NonWeaponSources.Contains(w))
        w = "weapon_" + w;

    return w.Length <= 64 ? w : UnknownWeapon;   // length checked after prefixing, not before
}
```

**The stored form is the designer name** — `weapon_ak47`, as `entities.json`
spells it (`CAK47` → `designer_name: "weapon_ak47"`). It is the form the entity
system, the dumps and `weapon_fire` all agree on, so anything joining against a
weapon list later needs no translation.

**Normalising is what keeps one gun in one row.** The two events disagree:
`weapon_fire` carries the designer name, `player_death` the short form. Store
them verbatim and every gun gets **two rows**, shots never line up with kills,
and accuracy is permanently wrong with nothing in the data hinting why.

**`NonWeaponSources` exists because prefixing blindly would be wrong.** `world`
(falls, console kills), `planted_c4` (the bomb), `inferno` (molotov fire) and
`trigger_hurt` (map hazards) are not weapon entities — there is no
`weapon_world`. They only ever arrive from `player_death` / `player_hurt`, never
from `weapon_fire`, so they cannot collide with a real weapon's row.

> **An incomplete `NonWeaponSources` list is cosmetic, not corrupting.** A death
> source that should have been in it gets stored as `weapon_<source>` — an ugly
> label on its own single row. No weapon's counters are split or merged by it,
> which is why the list ships as-is and is refined from real data rather than
> blocking on a live check.

`UnknownWeapon` is `"unknown"`, carrying no prefix — it is not a weapon, and the
absence of `weapon_` makes it impossible to collide with one. An over-length
value becomes `unknown` rather than being cut, because truncating would merge
two weapons into one row.

This is also the one place a per-player row count could grow beyond the ~40
weapons CS2 ships: the column is populated from an event string, so a modded
server with many custom weapon entities produces one row per distinct name. That
is still bounded by the weapons that exist on the server, and purge follows core
(§8), so no cap is imposed — but it is the reason the base name is validated
rather than trusted.

`weapon_fire` is the highest-frequency handler in the plugin — roughly 200/second
on a busy 20-player server. It does one slot lookup, one dictionary lookup and
one increment. No allocation, no logging on the success path, no database.

A bot never *owns* a row: it gets no `SessionStats` at adoption, so `_slots[slot]`
is null and every handler returns on the null check it already has. A bot as the
*counterparty* is a separate question, handled per event below.

### 5.2 The guards

Two independent filters. **Friendly fire and self damage are dropped before the
bot switches are even consulted** — they are not configurable, because neither
is combat performance under any setting.

```csharp
// Same slot is self damage and never counts; same team is the operator's call.
private static bool IsExcluded(TrackingConfig c, IPlayer? attacker, IPlayer? victim)
{
    if (attacker is null || victim is null) return false;        // world damage is neither
    if (attacker.Slot == victim.Slot) return true;               // self damage, in every mode
    return !c.CountFriendlyFire && attacker.Controller.TeamNum == victim.Controller.TeamNum;
}

// A null counterparty is world damage, not a bot, and must keep counting.
private static bool IsBot(IPlayer? p) => p is { IsFakeClient: true };

private bool CountsAgainst(IPlayer? counterparty) => BotsEnabled || !IsBot(counterparty);
```

The config snapshot is read **once at the top of each event**, per guide §3.2
rule 3 — a reload landing between the kill half and the death half of one
`player_death` would otherwise mix old and new settings within a single event.

| Event | Side recorded | Guards |
|---|---|---|
| `weapon_fire` | shooter's shots | play gate only — no victim exists to test |
| `player_hurt` | attacker's hits, damage, hitgroup | play gate, `!IsExcluded` **and** `CountAgainst(victim)` |
| `player_death` | attacker's kills, headshots | play gate, `!IsExcluded` **and** `CountAgainst(victim)` |
| `player_death` | victim's deaths | play gate and `CountFrom(attacker)` only |

**The play gate is core's**, `IsPlayCounted` (FR-API-05): `Rank.MinPlayers` met, not
a warmup `Rank.WarmupPoints` excludes, and no team made up only of bots while
`Rank.BotsEnabled` is off. The collector asks it per event through a `Func<bool>`, so
it still names no contract type, and core decides it once per tick, so the collector
gets the answer core used for the same event. It is closed while core is absent or
reloading, because core counts nothing then either. Against a core older than the
member, that core's points gate stands in, which agrees whenever it is scoring. A shot
fired while the gate is closed is never latched, so it cannot surface as a miss once it
opens.

**Deaths deliberately skip `IsExcluded`.** Being killed by a teammate is still a
death, `ranks_players.deaths` counts every death (FR-DB-04), and the
reconciliation in features.md §1 depends on the two agreeing.

**Kills do apply it**, matching the call core makes: with friendly fire off, a
team kill is `team_kill` rather than `kill` and does not increment
`ranks_players.kills` either.

#### The FFA mismatch warning

```csharp
// In OnCoreReady, not Load: Api does not exist yet in Load.
protected override void OnCoreReady()
{
    if (Api.GetPointSettings().FfaMode && !_cfg.Tracking.CountFriendlyFire)
        _logger.LogWarning(
            "Core reports FFA mode but Tracking.CountFriendlyFire is false — same-team hits " +
            "and kills will not be recorded, which on an FFA server is nearly all of them. " +
            "Set Tracking.CountFriendlyFire to true.");
}
```

Ungated by `DetailedLogging`: it fires once, and it is the only signal between a
misconfigured server and a module that looks broken. It **warns rather than
auto-corrects** — an operator may run FFA and still not want friendly fire
counted, and silently overriding their config would be the worse surprise. There
is no warning for the reverse (friendly fire on, FFA off); counting total damage
output on a team server is a legitimate choice.

The bot guard tests the **counterparty**, never the owner of the row.

**A skipped event is skipped entirely, not bucketed.** This is the one exception
to §5.1's never-drop rule, and it is either an operator instruction or a
not-performance event — which is why features.md documents what the exclusions
do to reconciliation.

---

## 6. Flush

### 6.1 Snapshot on the game thread, write off it

This is what makes the whole design race-free:

1. On the game thread, walk the live sessions and **detach** each one's deltas,
   zeroing the counters in place.
2. Hand the detached batch to the writer.
3. The writer runs off-thread and never touches a live session again.

No lock, no concurrent collection, no torn read — because the only mutator and
the only snapshotter are the same thread.

### 6.2 Triggers

All four are available to any plugin directly from the framework. **This module
needs nothing from core to flush**, which is worth stating because the obvious
alternative — piggybacking core's `PlayerDataSaved` — would couple two plugins'
write timing for no gain:

| Trigger | Source |
|---|---|
| Periodic | `Core.Scheduler.RepeatBySeconds(interval, …)` |
| Player disconnect | `Core.Event.OnClientDisconnected` |
| Map change | `Core.Event.OnMapUnload` |
| Plugin unload | the module's own `Unload` |

Two notes on the timer: `RepeatBySeconds` **fires its first callback
immediately**, not after one period, and the returned
`CancellationTokenSource` should *not* be passed to `StopOnMapChange` — the
flush must outlive a map change, and `OnMapUnload` is a flush point rather than
a teardown.

On disconnect, take the final delta **before** clearing the slot, in that order.

### 6.3 Season boundaries

Core raises `PeriodChanged` on the game thread after `CurrentPeriod` has already moved
to the new period. The module cuts every live session there and then, stamped with
`OldPeriod` rather than with the current period, and hands the batch to the writer. A
cut made before the event — the timer, a disconnect — was already stamped with the old
period, because core's `CurrentPeriod` only changes inside its rollover.

---

## 7. Commands

Registered at runtime so the names can come from `commands.jsonc`
(guide §8.1) — `[Command]` only supports compile-time constants:

```csharp
foreach (var (key, cmd) in _commands.CurrentValue)
    Core.Command.RegisterCommand(cmd.Name, Handle, false, cmd.Permission,
                                 Core.Localizer[$"command.{key}.help"]);
```

Help text comes from the localizer, not a config field. Keep the returned
`Guid`s if commands ever need unregistering; renames are otherwise restart-only.

Output goes through `Core.Localizer` — every string shown in chat or a menu
lives in `translations/en.jsonc`. Sorting is "best weapon first" by kills, with
`Display.MinShotsToList` filtering the long tail.

A player with no rows yet gets a localized "no stats" line, not an empty table.

---

## 8. Purge

```csharp
// PlayersPurged is the only notice this module will ever get. Nothing else sweeps its rows.
api.PlayersPurged += ids => DeleteRowsAsync(ids);
```

Delete from all six tables — all time and every period — in one queued transaction.
Core has already removed its own rows by the time this fires, so the module must not
expect to look the player up. A purge takes past periods too, as it does in core
(FR-DB-08).

### 8.1 Orphans from a purge the module missed

`PlayersPurged` is a live event. If core purges while this module is unloaded —
an operator removing it for a map cycle, a failed load, a staged upgrade — those
rows are never swept and the table grows without bound. Nothing else will ever
collect them.

So the module reconciles once at `Load(hotReload: false)`, matching FR-DB-09's
"heavy work on a cold start only" rule:

```sql
DELETE FROM {weapon_stats}
 WHERE steam_id NOT IN (SELECT steam_id FROM {ranks_players});
```

Both names come from `ResolveTableName`, so this works under any affix. It is
safe to depend on core's table here: FR-DB-04 fixes `ranks_players` as a closed,
known schema, which makes it a contract rather than an implementation detail.
The same statement runs against all six tables; every player core keeps has a
`ranks_players` row whatever periods they hold.

Skipped on hot reload, and skipped entirely if the probe in guide §4.6 reported
core's tables unreadable — deleting against a half-broken schema is worse than
leaving orphans.

### 8.2 Resets

`PlayerReset` (FR-API-13) arrives on the game thread after core wiped the player's
current period. The module drops the player's live session delta right there, then
queues a reset behind every write already handed over (§4.6). The queued reset first
drops the player's retry entries for what it wipes, then, in one transaction:

| Seasons | Statements |
|---|---|
| off | `DELETE` the player's rows from the three all-time tables |
| on | read the player's rows for that period from the three per-period tables; `UPDATE` each matching all-time row by `CASE WHEN col > @col THEN col - @col ELSE 0 END`; `DELETE` all-time rows the subtraction left at zero; `DELETE` the period's rows |

Retry entries stamped with a closed period stay — core keeps past periods through a
reset. A failed reset is logged and not retried: a later attempt would also take away
whatever the player has earned since.

---

## 9. Threading

- Game-event handlers and the snapshot pass: **game thread**.
- Writer, retry buffer, purge deletes, resets: **off-thread**, one at a time from a
  single ordered queue (§4.6).
- Nothing crosses back. The module touches no pawns or entities, so it needs no
  `NextWorldUpdate` hop at all.

> Core raises **every** API event on the game thread (FR-API-04), so
> `PlayersPurged` arriving here is safe to act on directly — and a module that
> wanted to touch game state from one could, which is why the guarantee is in
> the contract rather than left to each caller.

Never pass an async callback to `NextTick` / `NextWorldUpdate`; those overloads
are `[Obsolete]` and throw at runtime.

---

## 10. Release checklist

- [ ] Grep the contract **field name** across every file; nothing outside
      `RanksBridge` names `IRanksApi`, including `Unload`
- [ ] Loads cleanly on a server with **no** `Ranks` installed — and so does
      every other plugin on that box
- [ ] Migrations run on MySQL, PostgreSQL and SQLite, fresh and existing
- [ ] Three different core table prefixes yield three isolated table sets and
      three ledger rows
- [ ] `Core.Registrator.Register` called exactly once — fire 10 shots, confirm
      the row says 10
- [ ] Hitgroup `SPECIAL` (11) records without an index exception; an out-of-range
      hitgroup increments **no hitgroup counter** and still records its weapon row
- [ ] `SUM(kills)` and `SUM(deaths)` per player reconcile against
      `ranks_players` **at default `Tracking` settings** — including after a
      fall death and a bot kill with `BotsEnabled = false`
- [ ] An absent `Tracking` bot key binds to `true`, not `false`
- [ ] core's `Rank.BotsEnabled = false` stops bot kills, bot hitgroups and deaths
  to bots alike; turning it on starts all of them, with no module-side setting involved
- [ ] Below core's `Rank.MinPlayers`, and in warmup with `Rank.WarmupPoints` off,
      nothing is recorded; reaching the minimum starts shots, hits, kills and deaths
      together, and a shot fired below it never becomes a miss
- [ ] `CountFriendlyFire = false` drops team hits, damage and kills while a
      death to a teammate still counts; `= true` records all four
- [ ] Firing an AK and killing with it land on **one** row named `weapon_ak47`,
      not two — the prefix normalisation is what makes the two events agree
- [ ] A fall death stores `world`, not `weapon_world`
- [ ] Self damage is never recorded under either value of `CountFriendlyFire`
- [ ] Starting with core in FFA mode and `CountFriendlyFire = false` logs the
      mismatch warning exactly once
- [ ] Deltas survive a failed flush and land on the retry
- [ ] Command names grepped against sibling plugins
- [ ] No identifier named `rank`
- [ ] With core's `Database.VirtualPurgeDays = 0`, the three `_period` tables stay
      empty and both Statistics rows open their lists directly
- [ ] With seasons on, a rollover writes the counts still pending into the season
      that closed, and the next shot lands in the new one
- [ ] `!resetrank` with seasons off deletes the player's all-time rows; with seasons
      on it takes the current season off all time and keeps past seasons
- [ ] A purge removes the player from all six tables
- [ ] Against a Ranks older than 1.1.0 the module records all time only and warns
      once

---

## Open items

None. Every core-side and module-side question is settled.

Core-side: handler signatures carry `RanksPlayerSnapshot`, every event is
game-thread, and the `IQuerySchema` rewrite is mandated by guide §4.5.
Module-side: attacker-only hitgroups with hits **and** damage, health damage
only, friendly fire behind `Tracking.CountFriendlyFire`, self damage never
counted, deaths counted from every source, and weapon names normalised on
ingest so the two events cannot disagree.
