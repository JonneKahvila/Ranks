# Ranks_WeaponStats — features

> **Status:** design. Features and the core surface this module touches.
> Implementation lands once the shared API is settled.
> Specified by [FR-MOD-01](../../../docs/functional-requirements.md).

Per-weapon and per-hitbox tracking. The module owns its data end to end: its own
tables, its own commands, its own config. Its two screens are also listed under
core's **Statistics** menu as `Weapon stats` and `Hit stats` (FR-CMD-04) — the
command and the menu row open the same thing.

**It awards no points.** It is a statistics module, not a scoring one — nothing
here calls `AddPoints`, and core's ladder is unaffected by whether it is
installed.

---

## 1. What it tracks

### Per weapon, per player

| Counter | Source |
|---|---|
| Shots fired | `weapon_fire` |
| Hits | `player_hurt` (as attacker) |
| Damage dealt | `player_hurt.ActualDmgHealth` |
| Kills | `player_death` (as attacker) |
| Deaths | `player_death` (as victim) |
| Headshots | `player_death.Headshot` |

**Health damage only.** `player_hurt` also carries `ActualDmgArmor`; it is not
recorded and no column exists for it. Armour absorbed is not damage done, and
including it would make the figure disagree with CS2's own ADR.

### 1.1 What is excluded

**Everything recorded is the attacker's own work.** Three things are not counted
at all — not bucketed, not stored:

| Excluded | Configurable | Why |
|---|---|---|
| Friendly fire — hits, damage, hitgroups, **and kills** | **`Tracking.CountFriendlyFire`**, default `false` | not combat performance on a team server; but on FFA every kill looks like friendly fire, so the operator decides |
| Self damage — own grenade, own molotov | **no** | attacker and victim are the same player; never performance, in any mode |
| Anything on the victim's side — hitgroups taken, damage endured | **no** | the module answers "how do I shoot", not "how am I shot" |

**Deaths are the deliberate exception to the friendly-fire switch** and are never
filtered by it. Being killed by a teammate or by the world is still a death, and
`ranks_players.deaths` counts every non-bot death (FR-DB-04), so dropping any
source would break the reconciliation below.

**Kills follow the switch, and that mirrors core.** With it `false`, a team kill
is excluded here exactly as `team_kill` keeps it out of
`ranks_players.kills`. Counting the kill while excluding the hits that produced
it would be incoherent, and counting it on a team server makes a kills
leaderboard farmable.

> [!WARNING]
> **The switch must match the game mode, and the failure is silent.** Left
> `false` on an FFA server, where everyone shares a team, the module records
> almost nothing — which reads as a broken plugin, not a setting. Core carries
> the same distinction in `Rank.FfaMode`; the module reads it through
> FR-API-05 and **warns once at startup** when core says FFA and this says
> `false`. It warns rather than auto-corrects: an operator may legitimately run
> FFA and still not want friendly fire counted.

**Counters are raw event counts, recorded only while core counts play.** That is
core's rule, not this module inventing a policy: per FR-PTS-03 nothing is recorded
below `Rank.MinPlayers`, during a warmup `Rank.WarmupPoints` excludes, or — with
`Rank.BotsEnabled` off — while a team is made up only of bots, and an interaction
involving a bot is dropped. The module asks core's play gate per event
(`IsPlayCounted`, FR-API-05) and reads `BotsEnabled` through the API, so it carries no
switch of its own for any of them.

The payoff is a **reconciliation invariant** worth testing against:

```
SUM(ranks_weapon_stats.kills)  WHERE steam_id = X  ==  ranks_players.kills  for X
SUM(ranks_weapon_stats.deaths) WHERE steam_id = X  ==  ranks_players.deaths for X
```

**With seasons on (§8), compare like with like.** `ranks_players` then holds only the
current season, so its counterpart is `ranks_weapon_stats_period` at
`period = CurrentPeriod`; a closed season compares with its `ranks_players_history`
row. A reset keeps both sides in step — core zeroes its current season and this module
clears the same figures (§8.4) — where before 1.1.0 a reset left the module's side
untouched.

**It holds at the shipped defaults**, because both sides count the same events under
the same rules — nothing below `MinPlayers` or in an excluded warmup on either (this
module asks core's play gate rather than judging it), team kills excluded on both
(§1.1), bot interactions excluded on both (this module reads core's `Rank.BotsEnabled`
rather than carrying its own switch), every other death counted on both, and bots
given rows on neither (FR-DB-07).

**Against a Ranks older than 1.0.4 it does not hold.** That core cannot report its
play gate, so the module follows its points gate (`ArePointsEnabled`) instead and logs
a warning the first time it asks. The module then still records nothing points would
refuse, but that core's own counters move below `MinPlayers` regardless, and with
`Rank.UseCoreRankingLogic` off the module records nothing at all.

One setting also breaks it, a deliberate operator choice:

| Setting | Effect on the invariant |
|---|---|
| `Tracking.CountFriendlyFire = true` | module counts team kills; core never does, so module `kills` runs **high** |

Neither the bot axis nor the play gate can break it: there is nothing here to disagree
with core.

A **bug** also breaks it, and that is the one to care about: any event the
module drops without being told to. §1.3 exists for exactly that reason.

`headshots` has no counterpart — core's nine counters (FR-DB-04) do not include
one. `headshot` is a priced *event*, not a stored stat, so per-weapon
headshots are new data with nothing to reconcile against.

### 1.2 Bots are core's switch

This module has **no bot setting**. It reads core's `Rank.BotsEnabled` through the
API and applies it to both counterparties: with it off, hits, damage, kills,
headshots and hitgroups against a bot are dropped, and so are deaths caused by one.

It replaced a pair of independent switches here (`CountAgainstBots` /
`CountFromBots`). The split was expressive — a practice server could keep bot kills
while dropping deaths to bots — but it was also the only way to configure this
module into disagreeing with core, which is the invariant above. One switch for the
whole plugin set is worth more than the axis.

The value is polled rather than read per event: `GetPointSettings` builds a record
and a dictionary, and `weapon_fire` runs at roughly 200/second. It refreshes when
core becomes ready and on each flush tick, so an operator hot-reloading core's config
is picked up within one flush interval.

Two consequences that are not obvious from the setting name:

- **Shots can never be filtered.** `weapon_fire` carries no victim, so with
  `BotsEnabled = false` the shots at a bot still count while the hits do
  not, and accuracy reads low. The event does not carry a target, so there is no
  version of this that works.
- **The switch does not affect a bot's own rows.** A bot never has stats of its own
  under any setting — it has no SteamID64 to key them to (FR-DB-07).

### 1.3 Deaths with no weapon to attribute

Falls, world damage and console suicides fire `player_death` with no attacker.
Core increments the victim's `deaths`; this module needs a weapon to file it
under, and has none.

**Those rows are bucketed, never dropped** — dropping them silently breaks the
invariant above, and "my deaths don't add up" is a bug report nobody can
diagnose from the data.

CS2 supplies real values for most of these — `world` for falls and console
kills, `planted_c4` for the bomb — and they are kept as-is, because "died to the
bomb 14 times" is a statistic a player would want. Only a genuinely empty value
falls back to a reserved `unknown` bucket.

### Per hitgroup, per player

**Two counters per hitgroup — hits and health damage** — recorded from the
attacker's side only, keyed on `player_hurt.ActualHitgroup`. "I have landed 412
headshots for 38 000 damage" is the question this table answers.

The victim's side is not recorded: nothing tracks where a player *gets* hit.
That halves the table and matches what the feature is for.

`HitGroup_t` is a typed enum on the event, so no magic numbers — but its values
are **not a dense 0..N range**, and that is the trap:

```
INVALID = -1    GENERIC = 0    HEAD = 1    CHEST = 2    STOMACH = 3
LEFTARM = 4     RIGHTARM = 5   LEFTLEG = 6 RIGHTLEG = 7 NECK = 8
UNUSED  = 9     GEAR = 10      SPECIAL = 11            COUNT = 12
```

Eleven groups are tracked (`GENERIC` through `SPECIAL`), but they occupy values
**0–11 with a hole at 9**. An eleven-element array indexed by the raw value
overflows on `SPECIAL`. `INVALID = -1` is reachable — world damage, falls — and
would index out of bounds the other way. `COUNT = 12` is a sentinel, never a
group.

**CS2 has no "feet" hitgroup.** The legacy LVL Ranks / ExStats column of that
name has no engine equivalent and is not reproduced.

---

## 2. Game events it hooks

Its own, directly — core does not relay game events.

| Event | Declared in | Carries |
|---|---|---|
| `weapon_fire` | `mod.gameevents` **only** | `UserId`, `Weapon`, `Silenced` |
| `player_hurt` | `core` + `mod` | attacker/victim, `Weapon`, `ActualDmgHealth`, `ActualHitgroup` |
| `player_death` | `core` + `mod` | attacker/victim/assister, `Weapon`, `Headshot` |

Two things to carry into implementation:

- **Prefer the `Actual*` properties.** `DmgHealth` is a `short` and `Health` /
  `Armor` / `Hitgroup` are `byte`; the `Actual*` variants are the untruncated
  values. A server granting >255 HP silently corrupts the byte ones.
- **`weapon_fire` is the highest-frequency event in the plugin** — once per
  bullet, so roughly 200/second on a busy 20-player server. It must not touch
  the database (NFR-01), and it should hold itself to the allocation-free bar
  NFR-11 sets for tick callbacks even though it is not one.

> `weapon_fire` is declared in `mod.gameevents` only, not `core.gameevents` —
> the FR-PTS-01 deprecation risk applies. Confirm it still fires before relying
> on shot counts.

---

## 3. What it needs from core

| Need | Surface | Status |
|---|---|---|
| Shared connection | `ConnectionName` | exists (FR-API-06) |
| Affixed table names | `ResolveTableName(baseName)` | exists (FR-API-06) |
| Schema creation | `RegisterMigrations(assembly, pluginId)` | exists (FR-API-06) |
| Sweep its rows on purge | `PlayersPurged` | exists (FR-API-04) |
| Which season a count belongs to | `ArePeriodsEnabled`, `CurrentPeriod` | exists (FR-API-13) |
| Close a season's pending counts | `PeriodChanged` | exists (FR-API-13) |
| Clear what a reset cleared | `PlayerReset` | exists (FR-API-13) |
| List past seasons in its menus | `GetPeriodsAsync` | exists (FR-API-13) |
| Know when to flush | — | **see §6** |
| Know which thread handlers run on | — | **game thread, guaranteed** (FR-API-04) |

Nothing else. It does not read points, ranks, snapshots or stats from core — it
is a pure producer that happens to share core's database, purge lifecycle and
season clock (§8).

**That makes it the cleanest test of the floor for a table-owning module:** if
these four members plus a flush signal are enough, the database half of
`RanksModuleBase` is correctly scoped.

---

## 4. Tables

`ranks_weapon_stats`, `ranks_hit_stats` and `ranks_weapon_hit_stats`, all affixed
via FR-API-06 so they sit alongside core's, all migrated by core against this
module's ledger (FR-DB-05). They hold **all-time** totals, and that meaning does not
change with seasons: CustomHUD and the importer read them as lifetime figures.

Each has a per-season twin with the same columns plus `period BIGINT` —
`ranks_weapon_stats_period`, `ranks_hit_stats_period` and
`ranks_weapon_hit_stats_period` — written only while core's seasons are on (§8).

**Each needs a real PK on its natural key** — `(steam_id, weapon)`,
`(steam_id, hitgroup)` and `(steam_id, weapon, hitgroup)`, and the same plus
`period` on the twins. Without one, MySQL's `ON DUPLICATE KEY UPDATE` silently
inserts duplicates forever while PostgreSQL and SQLite reject the statement outright.

**Only landed hits appear here.** A miss has no hitgroup, so it cannot be a row on
a table keyed by one — misses are counted per weapon on `ranks_weapon_stats.misses`
and overall on core's `ranks_players.misses`. Summing `ranks_weapon_hit_stats` over
weapons reproduces `ranks_hit_stats` exactly, because both are written from the same
per-weapon counters in one flush.

Weapon is stored as a name rather than a defindex: the events give a name, and
resolving it to a defindex would add a lookup that earns nothing.

**The stored form is the entity designer name — `weapon_ak47`.** That is what
`entities.json`, the entity system and `weapon_fire` all use, so it is the one
spelling nothing else has to translate. `player_death` reports the short form
(`ak47`) instead, so names are normalised on ingest; stored verbatim, every gun
would get two rows and accuracy would be permanently wrong.

Non-weapon death sources — `world`, `planted_c4`, `inferno`, `trigger_hurt` —
keep their own names, because there is no `weapon_world` entity to point at.

---

## 5. Commands

`!weaponstats` / `!ws`, owned by the module. Names, aliases and permissions come
from the module's own `commands.jsonc`, following core's FR-CMD-01 shape, so the
module registers them at runtime through `Core.Command.RegisterCommand` rather
than by attribute.

**Hitgroup totals have no command.** They are collected, stored in
`ranks_hit_stats` and `ranks_weapon_hit_stats`, and exposed through
`IWeaponStatsApi.GetHitGroupsAsync` and `GetWeaponHitGroupsAsync`, but nothing
prints them in chat — `StatsCommands.RegisterAll` registers only the
`WeaponStats` key.

---

## 6. Timing — the open design question

Counters increment on every shot and every hit, so a write-through design is
impossible: that is hundreds of statements per second. The module accumulates in
memory and flushes periodically.

**It should accumulate *deltas* and write additively** (`counter = counter +
delta`), not load-then-overwrite:

- no read on connect, so a map change costs zero queries instead of 64
- two servers sharing one database cannot clobber each other's counters

This is a deliberate divergence from core, which writes **absolute** values
because its in-memory record is authoritative (FR-DB-06). A module holding only
session deltas has no such claim, and additive is the correct shape for it.

The open part is **what triggers a flush**:

| Moment | Why it matters |
|---|---|
| Periodic | bounds data loss on a crash |
| Player disconnect | their session ends; deltas must not be carried |
| Map end | same, in bulk |
| Plugin unload | last chance; the module's own `Unload` covers it |

**Resolved — the module needs nothing from core here.** The obvious move was to
piggyback core's `PlayerDataSaved`, on the reasoning that FR-DB-07 already has
core saving at those moments. That would have been wrong: it couples this
module's write timing to another plugin's save cadence, and it asks core for a
guarantee it has never made.

SwiftlyS2 gives **every** plugin the triggers directly — `OnClientDisconnected`,
`OnMapUnload`, and `ISchedulerService` for the timer. They are also the better
signals: they describe the player's session rather than core's bookkeeping, so
they stay correct if core's save policy ever changes.

---

## 7. API requests raised

| # | Request | Also wanted by | Status |
|---|---|---|---|
| 1 | Documented thread affinity for every core event | every module | **granted** — all events are game-thread (FR-API-04) |
| 2 | `PlayerDataSaved` guaranteed on disconnect and map end | — | **cut** — §6 |

Request 2 is recorded because it was raised and rejected, not because it is
pending. The framework already serves it, and a core member that duplicates a
framework one is a maintenance liability with no consumer.

Request 1 was granted as **documentation of existing behaviour**, not new API
surface — the outcome a module audit should be hoping for.

---

## 8. Seasons (FR-DB-10)

Core's `Database.VirtualPurgeDays` turns on **seasons** — *periods* in the code. This
module has no season setting of its own: core is the only clock, and the module follows
`CurrentPeriod`, `PeriodChanged` and `PlayerReset` (FR-API-13). With seasons off it
behaves exactly as it did before they existed — no per-season writes, the same menus,
the same API answers.

### 8.1 Storage: written twice

Every delta lands in the all-time tables exactly as before and, while seasons are on,
again in the per-season twin under the season it was earned in — one transaction, so the
two can never disagree. The season is stamped when a batch is **cut** on the game thread,
not when it is written: the retry buffer mixes batches, and one retried after a boundary
must still land in its own season.

When core closes a season it raises `PeriodChanged` after `CurrentPeriod` has already
moved on, so the module cuts everything still pending at once and stamps it with the
**old** period. Nothing earned before the boundary counts in the new season.

Nothing is backfilled. A season holds only what was recorded while seasons were on and
this version was installed, so **Before seasons** (period `0`) is always empty here —
those figures are part of all time.

### 8.2 Reading

`period = AllTime (-1)` reads the all-time tables, which is what every pre-existing API
member still does. Any other value reads that season's rows. The unflushed session delta
is added to an all-time read, and to a read of the season being written now — never to a
closed season, and to no season at all while seasons are off.

### 8.3 Menus

While seasons are on, **Weapon stats** and **Overall Hit Stats** open on **This season**,
**All time** and **Previous seasons** (core's period list without the current one). With
them off, both open their lists directly.

CustomHUD keys designed pages on menu ids. It draws `weaponstats.weapons` from the rows,
so every view keeps that id. It fetches all-time figures itself for
`weaponstats.hitgroups` and `weaponstats.weaponhitgroups`, so those two are all time's
alone and a season's view is `weaponstats.hitgroups.period` /
`weaponstats.weaponhitgroups.period`. The choice screens are
`weaponstats.seasons.weapons` / `weaponstats.seasons.hitgroups`, and the season list is
`weaponstats.periods`.

### 8.4 Resets

`!resetrank`, `!resetmyrank` and the Settings reset raise `PlayerReset` after core wiped
the player's current season. The module drops the player's unwritten session delta, then
clears its own figures:

| Seasons | What is cleared |
|---|---|
| off | every all-time row the player has |
| on | the current season's rows, subtracted from all time (clamped at zero) and then deleted; past seasons stay |

A batch already handed to the writer must not put the old figures back after the delete.
Every database operation the writer performs runs from one queue, in the order it was
handed over, so a reset runs after every batch cut before it — and drops the player's
entries for the wiped season from the retry buffer if one of those batches failed.

### 8.5 Purges

`PlayersPurged` deletes the players from all six tables: a purge takes a player's past
seasons too, as it does in core (FR-DB-08). The cold-start orphan reconcile (§8 of the
implementation notes) covers the per-season tables as well.
