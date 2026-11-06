# Ranks_WeaponStats

Per-weapon and per-hitbox statistics for the [Ranks](../../README.md) plugin.

---

## Features

- 🔫 **Per-weapon totals** - shots, hits, damage, kills, deaths and headshots for every gun
- 🎯 **Hitbox breakdown** - where your bullets actually land, by hitgroup and per weapon
- 📅 **Seasons** - with core's seasons on, this season, all time and every past season
- 📊 **In-game command** - `!weaponstats`, configurable name and permission
- 🤖 **Bot and friendly-fire switches** - count them or not, per game mode
- 🔌 **Published API** - other plugins read the same totals

**It awards no points and does not affect ranks.** Installing or removing it leaves
core's ladder untouched.

---

## Requirements

- `Ranks` 1.1.0 or newer, installed and loaded — an older Ranks still works, but without
  seasons, and its resets do not reach weapon stats; the module says so once in the log
- A SwiftlyS2 database connection — the same one core uses, named in **core's**
  `config.jsonc`, not here

The module creates its own tables on that connection and never touches core's.

---

## Installation

1. Drop the module into your plugins directory alongside `Ranks`.
2. Start the server once. Core creates the tables on first load.
3. Check the console for `Ranks_WeaponStats` — a missing `Ranks` is reported there,
   and the module stays idle rather than failing the server.

---

## Commands

### Player Commands

| Command | Aliases | Permission | Description |
|---------|---------|------------|-------------|
| `!weaponstats` | `!ws` | None | Your per-weapon table, best weapon first |

Name, aliases and permission are editable in `commands.jsonc`. An empty `Command`
removes the command. It is module-owned and does not appear in core's `!rank` menu.
With core's seasons on it opens on the same season choice as the menu rows below.

> [!NOTE]
> **Hitgroup totals have no command.** They are reachable from the menu — **Overall
> Hit Stats** under `!rank` → Statistics, and per weapon by selecting a row in the
> weapon list — and through `GetHitGroupsAsync` / `GetWeaponHitGroupsAsync` on the
> published API, but nothing prints them in chat.

---

## Configuration

Config file: `addons/swiftlys2/plugins/Ranks_WeaponStats/config.jsonc`

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Tracking.CountFriendlyFire` | bool | `false` | Count hits, damage, hitgroups and kills against players on your own team |
| `Database.FlushIntervalSeconds` | int | `120` | How often counters are written; `0` flushes only on disconnect, map change and unload |
| `Display.MinShotsToList` | int | `10` | Hide weapons fired fewer times than this |
| `Display.MinShotsToList` | int | `10` | Hides weapons barely touched, so one stray pistol shot does not fill the table |
| `General.DetailedLogging` | bool | `false` | Narrates expected flow; warnings and errors print regardless |

### Example Configuration

<details open>
<summary>Default <code>config.jsonc</code></summary>

```jsonc
{
  "WeaponStats": {
    "Tracking": {
      "CountFriendlyFire": false
    },
    "Database": {
      "FlushIntervalSeconds": 120
    },
    "Display": {
      "MinShotsToList": 10
    },
    "General": {
      "DetailedLogging": false
    }
  }
}
```

</details>

<details open>
<summary>Default <code>commands.jsonc</code></summary>

```jsonc
{
  "Commands": {
    "All": {
      "weaponstats": { "Command": "weaponstats", "Aliases": [ "ws" ], "Permission": "" }
    }
  }
}
```

</details>

### Friendly fire — match this to your game mode

**On a normal team server leave it `false`.** Shooting a teammate is not combat
performance, and counting it lets someone pad damage and accuracy on their own side.

**On an FFA or deathmatch server set it `true`.** Everyone is typically on one team
there, so with it `false` the module sees nearly every kill as friendly fire and
records **almost nothing**. Core has the matching switch in `Rank.FfaMode`; set both
the same way.

> [!NOTE]
> The module warns once at startup if core reports FFA mode while this is still
> `false` — the symptom otherwise looks like a broken plugin rather than a setting.

**Self damage is never counted** under any mode, and **deaths are never filtered by
this switch**: being killed by a teammate is still a death.

### Bots are core's switch, not this module's

This module has **no bot setting**. It reads core's `Rank.BotsEnabled` through the API,
so one flip covers core, this module and every other one — and the totals cannot be
configured into disagreeing.

With it off (the default) an interaction is dropped whenever the counterparty is a bot:
hits, damage, hitgroups, kills and headshots against one, and deaths caused by one. While
a team is made up only of bots, nothing is recorded at all, shots included.
At the shipped defaults the totals reconcile:

```
SUM(ranks_weapon_stats.kills) per player  ==  ranks_players.kills
SUM(ranks_weapon_stats.hits)  per player  ==  ranks_players.hits
```

With core's seasons on, `ranks_players` holds the current season only, so compare it
with `ranks_weapon_stats_period` for the current period instead.

Core keeps its own overall `hits`, `misses` and `headshots` on the player row, counted
from the same events and now gated by the same switch.

> [!NOTE]
> **`Rank.MinPlayers` and `Rank.WarmupPoints` are core's switches too.** Below the
> minimum, or during a warmup core does not count, this module records nothing either.
> That needs Ranks 1.0.4 or newer. Against an older Ranks the module follows that Ranks'
> points instead, so it records nothing while `Rank.UseCoreRankingLogic` is off, and it
> logs a warning once.

> [!WARNING]
> **With bots on the same teams as humans, `BotsEnabled = false` makes accuracy read
> low.** Shots come from `weapon_fire`, which has no victim and therefore cannot be
> filtered — shots at bots count while the resulting hits do not. Nothing can fix this;
> the event carries no target. Against a team made up only of bots nothing is recorded,
> so this only bites when both teams have humans. Turn `Rank.BotsEnabled` **on** if you
> run bots that way and care about accuracy.

The switch does not affect bots' *own* rows: a bot never gets stats of its own, because
it has no SteamID64 to key them to.

---

## How It Works

**Overview**

Every shot, hit and death is accumulated in memory per player, then written to the
database in batches.

**Collection**

`weapon_fire` increments shots, `player_hurt` adds hits, damage and the hitgroup, and
`player_death` records kills, headshots and deaths. The tracking switches decide which
of those count; the shot counter is the one that cannot be filtered, since the fire
event names no victim.

**Persistence**

Counters flush on the timer, on disconnect, on map change and on unload. Writes are
**additive** rather than absolute, so two servers sharing one database accumulate
instead of overwriting each other, and nothing has to be read back when a player
connects.

**Seasons**

This module has no season setting of its own: it follows core's
`Database.VirtualPurgeDays`. While that is above zero, every write lands twice, in the
same transaction — once in the all-time tables, exactly as before, and once in the
per-season tables (`ranks_weapon_stats_period`, `ranks_hit_stats_period`,
`ranks_weapon_hit_stats_period`) under the season it was earned in. When core starts a
new season, everything not yet written is flushed into the season that just ended.
While seasons are off nothing is written per season, and the menus are the ones this
module always had.

Per-season figures start when this version is installed with seasons on. Seasons that
closed before that, and **Before seasons**, open empty here; those figures are part of
**All time**.

**Resets and purges**

`!resetrank`, `!resetmyrank` and the reset in core's Settings menu clear weapon
statistics too. With seasons off they delete the player's all-time rows. With seasons on
they take the current season's figures off the all-time totals and delete that season's
rows; past seasons stay, as they do in core. A purge removes everything the player has,
all time and every season.

**Reading**

`!weaponstats` and the API both merge the stored rows with the caller's unflushed
session delta, so a figure never lags a flush interval behind what just happened. A past
season is read as stored, since nothing still in memory belongs to it.
`Display.MinShotsToList` shapes the menu only; the API
returns every row.

---

## For plugin developers

The module publishes `Ranks.WeaponStats.Api.v1`. Reference
`Ranks_WeaponStats.Contract.dll` from this module's `resources/exports/` — **do not
copy it next to your plugin**, or the host loads a second one, the interface gets two
type identities, and binding silently stops matching. (The copy beside
`Ranks_WeaponStats.dll` is this module's own load-time fallback — ignore it.)

```csharp
public interface IWeaponStatsApi
{
    const string InterfaceKey = "Ranks.WeaponStats.Api.v1";
    const long AllTime = -1;

    Task<IReadOnlyList<WeaponStatRow>> GetWeaponsAsync(ulong steamId);
    Task<IReadOnlyList<HitGroupStatRow>> GetHitGroupsAsync(ulong steamId);
    Task<IReadOnlyList<WeaponHitGroupStatRow>> GetWeaponHitGroupsAsync(ulong steamId);
    Task<WeaponStatRow?> GetWeaponAsync(ulong steamId, string weapon);

    Task<IReadOnlyList<WeaponStatRow>> GetWeaponsAsync(ulong steamId, long period);
    Task<IReadOnlyList<HitGroupStatRow>> GetHitGroupsAsync(ulong steamId, long period);
    Task<IReadOnlyList<WeaponHitGroupStatRow>> GetWeaponHitGroupsAsync(ulong steamId, long period);
    Task<WeaponStatRow?> GetWeaponAsync(ulong steamId, string weapon, long period);
}
```

The overloads without a `period` return **all-time** totals, exactly as before seasons
existed. A `period` is one of core's period ids — `IRanksApi.CurrentPeriod` and
`GetPeriodsAsync()` hand them out — or `IWeaponStatsApi.AllTime`. A season holds only what
was recorded while core's seasons were on, and includes the unflushed session delta only
while it is core's current season.

`GetWeaponHitGroupsAsync` is the same distribution split by weapon — summing its rows
over weapons reproduces `GetHitGroupsAsync` exactly.

**Only landed hits appear in either.** A miss has no hitgroup; misses are on the weapon
row (`Misses`) and, overall, on core's `ranks_players.misses`.

`Weapon` is the entity designer name (`weapon_ak47`, not `ak47`); `GetWeaponAsync`
normalises either spelling. `HitGroup` is the raw `HitGroup_t` value, which is not a
dense range — `0` through `11` with a hole at `9`.

---

## Troubleshooting

| Problem | Possible Cause | Solution |
|---------|----------------|----------|
| "this module is idle" at startup | Ranks is not installed | Install Ranks alongside it |
| Almost nothing is recorded | FFA server with `CountFriendlyFire` off | Set it `true`, and core's `Rank.FfaMode` to match |
| Accuracy looks far too low | Bots on human teams with core's `Rank.BotsEnabled` off | Turn `Rank.BotsEnabled` on |
| Nothing is recorded on a quiet server, in warmup, or against bots | Fewer humans than core's `Rank.MinPlayers`, warmup with `Rank.WarmupPoints` off, or a team made up only of bots with `Rank.BotsEnabled` off | Expected; core counts nothing then either |
| `!weaponstats` disagrees with `!stats`, or nothing is recorded with `Rank.UseCoreRankingLogic` off | Ranks older than 1.0.4, which cannot report its play gate (the log says so once) | Update Ranks |
| Stats lost after a crash | Flush interval too long | Lower `Database.FlushIntervalSeconds` |
| No tables created | Core could not reach the database | Check core's connection name and the startup log |
| A past season shows no weapon stats | It closed before this module's 1.1.0 was installed, or it is **Before seasons** | Expected; those figures are under **All time** |
| Log: "this Ranks version has no seasons" | Ranks is older than 1.1.0 | Update Ranks. Until then weapon stats work as in 1.0.2: all time only, and resets do not clear them |
| `!hitstats` does nothing | It does not exist | Hitgroup totals are API-only; see the note under Commands |
