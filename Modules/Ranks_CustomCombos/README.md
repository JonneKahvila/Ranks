# Ranks_CustomCombos

Operator-defined kill combinations for the [Ranks](../../README.md) plugin.

---

## Features

- 🎯 **Combine attributes** - "a no-scope headshot is worth 10", "a knife headshot is worth 50"
- 💥 **Same-tick multi-kills** - one grenade killing three players
- 🥇 **Closest match wins** - escalating tiers pay the most specific, never the sum
- ➕ **Additive, or replacing** - add a bonus on top, or take the kill over entirely
- 📊 **Per-combo counters** - `!combos` shows what a player has landed
- 📅 **Seasons** - follows Ranks' seasons: this season's counts first, all time and past seasons a row away
- 🔌 **Published API** - other plugins read the same counts and combo names

---

## Requirements

- `Ranks` 1.1.0 or newer, installed and loaded
- A SwiftlyS2 database connection — the same one core uses, named in **core's** config

Without core this module is idle: no awards, no tables.

> [!CAUTION]
> **Do not enable `SuppressCoreAwards` alongside an absolute rating module** such as
> `Ranks_EloPoints`. Both would be deciding the same number, and nothing detects it.
> Additive mode (the default) is safe with either.

---

## Installation

1. Drop the module into your plugins directory alongside `Ranks`.
2. Start the server once. `config.jsonc` and `commands.jsonc` are created, and core
   runs the migrations — `ranks_combos` and `ranks_combos_period`.
3. **No combos ship by default.** Copy what you want from
   `resources/examples/combos.jsonc` into the `Rules` object — what a combination is
   worth is a balance decision, not a default.

Upgrading from 1.0.x creates `ranks_combos_period` on first boot and leaves `ranks_combos`
as it is. On a host that does not allow schema changes, create it by hand first:
`ranks_combos`'s columns plus `period BIGINT NOT NULL`, with the primary key
`(steam_id, combo_key, period)` — prefixed and suffixed like every other Ranks table.

---

## Commands

### Player Commands

| Command | Aliases | Permission | Description |
|---------|---------|------------|-------------|
| `!combos` | `!mycombos` | None | The combos you have landed, highest first — this season's while Ranks runs seasons |

Name, aliases and permission are editable in `commands.jsonc`. An empty `Command`
removes the command.

---

## Attributes

A combo lists attributes that must **all** hold on one kill. Combos are custom; the
attribute vocabulary is not.

### Flags

Named for the `player_death` field they read, so a name can be checked against an event
dump rather than against this table.

| Attribute | True when |
|---|---|
| `assistedflash` | someone else's flash set the kill up |
| `attackerblind` | the attacker was flashed |
| `attackerinair` | the attacker's feet were off the ground |
| `dominated` | the kill started a domination |
| `headshot` | the kill was a headshot |
| `noscope` | fired without scoping |
| `penetrated` | the bullet went through a surface — a wallbang |
| `revenge` | the kill was revenge on a dominator |
| `team_kill` | attacker and victim on the same team |
| `thrusmoke` | the bullet passed through smoke |

Four of these — `attackerinair`, `assistedflash`, `dominated`, `revenge` — **core does
not price at all**, so a one-attribute combo on them is the only way to score them.

### Valued

These take a value after a colon, because the field is a number rather than a flag.

| Attribute | Matches |
|---|---|
| `distance:<units>` | at or past that many source units — `2000` is roughly 50 m |
| `hitgroup:<n>` | that exact hitgroup — `1` head, `2` chest, `3` stomach, `4`/`5` arms, `6`/`7` legs |
| `weapon:<classname>` | that exact weapon, e.g. `weapon:weapon_ak47` |

### Derived

| Attribute | True when |
|---|---|
| `first_blood` | the first kill of the round |
| `weapon_class:<class>` | one of `knife` `grenade` `inferno` `taser` `pistol` `smg` `rifle` `sniper` `shotgun` `heavy` |

An unknown attribute is reported at load with the combo named and skipped, rather than
quietly never matching.

---

## Configuration

Config file: `addons/swiftlys2/plugins/Ranks_CustomCombos/config.jsonc`

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `SuppressCoreAwards` | bool | `false` | Matched combo replaces core's kill award instead of adding to it |
| `Rules` | object | `{}` | The combos, keyed by name |
| `Database.FlushIntervalSeconds` | int | `120` | How often counts are written; minimum 15 |
| `General.DetailedLogging` | bool | `false` | Narrates expected flow; warnings and errors print regardless |
| `Rules.<combo>.RequiresAll` | array | — | Every attribute must be present. Required |
| `Rules.<combo>.RequiresNone` | array | `[]` | None of these may be present |
| `Rules.<combo>.Count` | int | `1` | Matching kills needed **on the same tick** |
| `Rules.<combo>.Points` | int | — | What it pays. Required, non-zero |
| `Rules.<combo>.Message` | string | `combo.<name>` | Translation key for the chat line |

### Example Configuration

<details open>
<summary>Default <code>config.jsonc</code></summary>

```jsonc
{
  "Combos": {
    "SuppressCoreAwards": false,

    // Empty on purpose. What a combination is worth is a balance decision, not a default.
    "Rules": {},

    "Database": {
      "FlushIntervalSeconds": 120
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
      "combos": { "Command": "combos", "Aliases": [ "mycombos" ], "Permission": "" }
    }
  }
}
```

</details>

<details open>
<summary>Two combos, for reference</summary>

```jsonc
{
  "Combos": {
    "SuppressCoreAwards": false,
    "Rules": {
      "noscope_hs": {
        "RequiresAll": [ "headshot", "noscope" ],
        "Points": 10
      },
      "nade_double": {
        "RequiresAll": [ "weapon_class:grenade" ],
        "Count": 2,
        "Points": 75
      }
    }
  }
}
```

</details>

Add a `combo.<name>` entry to `resources/translations/en.jsonc` for each combo. A
missing key falls back to the bare combo name rather than printing a `{lang}.{key}`
marker.

---

## How It Works

**Overview**

Core asks this module what to do with a kill before it awards anything, and awards the
answer through its own pipeline.

**Matching**

Core computes the kill's attributes once and hands them over. Rules are tried
most-specific first; the first whose `Count` is satisfied wins, and the rest still have
their same-tick run updated. Several combos can match one kill, but **only one pays** —
given `[headshot, noscope] = 10` and `[headshot, noscope, penetrated] = 25`, a no-scope
wallbang headshot pays **25, not 35**. Ties go to config order.

**Awarding**

Because the claim runs through core, it gets core's gates, the VIP multiplier, one
away-from-zero rounding, the per-player XP-messages preference and round-end
batching for free — rather than this module reimplementing five things and drifting.

That ordering is also what makes `SuppressCoreAwards` possible at all: a plugin
watching `player_death` on its own cannot stop core, and plugin order is not guaranteed.

**Counting**

A combo is counted whenever it matches and core counts the kill, even if no points
follow — a combo worth `0`, a cancelled award, or core scoring switched off. Core does
not count a kill below its `Rank.MinPlayers`, during a warmup its `WarmupPoints`
excludes, or with `BotsEnabled` off when a bot is involved or a team is made up only of
bots; such a kill never reaches this module. With a Ranks older than 1.0.4, combos count
only while that Ranks awards points.

**Award Flow**

```
Kill → Core computes attributes → Module matches → Claim → Core gates, multiplies, rounds → Points + chat
```

### `Count` is same tick only

It is for one grenade killing three players, not a streak. Streaks and timed
multi-kills are core's, which already has eleven tiers and a configurable window.

`Count` above 1 **cannot** be combined with `SuppressCoreAwards`. Core asks per kill, so
the first two kills of a three-kill run are already paid by the time the third arrives,
and suppressing only the last would be unpredictable. It is rejected at load.

### What suppression covers

| | Suppressed? |
|---|---|
| `kill`, attribute awards, weapon class | yes |
| Killstreak | **no** — a streak belongs to the round, not this kill |
| `kills` / `deaths` / `assists` counters | **never** |

A kill matching no combo is unaffected, or an ordinary kill would pay nothing.

### Seasons

With Ranks' `Database.VirtualPurgeDays` set, every count is stored twice: added to its
lifetime total in `ranks_combos`, as always, and to its season's total in
`ranks_combos_period`. The module has no season setting of its own — it follows Ranks'
calendar, and counts still waiting to be written when a season ends are filed under the
season they were landed in.

| Screen | Shows |
|---|---|
| `!combos` | This season's counts, under an **All time** row and a **Previous seasons** row |
| **All time** | Lifetime counts — the list `!combos` shows with seasons off |
| **Previous seasons** | Ranks' finished seasons; pick one for its counts |

Counts are kept per season only from the moment seasons are switched on, and only from
1.1.0 on. Everything landed before that is in **All time**, so **Before seasons** shows
none of it.

With seasons off nothing is written to `ranks_combos_period`, and `!combos` is the
lifetime list it has always been.

### Resets and purges

| Ranks action | What happens to combo counts |
|---|---|
| `!resetrank`, `!resetmyrank`, **Settings → Reset my rank**, seasons off | Every count the player has is deleted |
| The same, seasons on | This season's counts are deleted and taken off the lifetime total. Earlier seasons stay, as they do in Ranks |
| A `Database.PurgeDays` purge | Every row of the player, lifetime and per season, is deleted |
| Server start | Rows of players Ranks no longer has — left by a purge while this module was not loaded — are deleted |

---

## For plugin developers

The module publishes `Ranks.CustomCombos.Api.v1`. Reference
`Ranks_CustomCombos.Contract.dll` from this module's `resources/exports/` — **do not
copy it next to your plugin**, or the host loads a second one, the interface gets two
type identities, and binding silently stops matching. (The copy beside
`Ranks_CustomCombos.dll` is this module's own load-time fallback — ignore it.)

```csharp
public interface ICustomCombosApi
{
    const string InterfaceKey = "Ranks.CustomCombos.Api.v1";
    const long AllTime = -1;

    Task<IReadOnlyList<ComboStatRow>> GetCombosAsync(ulong steamId);
    Task<IReadOnlyList<ComboStatRow>> GetCombosAsync(ulong steamId, long period);
    Task<ComboStatRow?> GetComboAsync(ulong steamId, string comboKey);
    Task<ComboStatRow?> GetComboAsync(ulong steamId, string comboKey, long period);
    IReadOnlyList<string> ConfiguredCombos { get; }
    string GetComboName(string comboKey);
}
```

The async methods include the player's **unflushed** counts, so a figure read here
matches what `!combos` prints. `ConfiguredCombos` is synchronous because it reads the
loaded rule set rather than the database — it is the only way to render a combo an
operator has configured but nobody has landed yet.

The overloads without a period read lifetime counts, exactly as in 1.0; `AllTime` asks for
the same. Any other `period` is a season id as Ranks hands them out — `CurrentPeriod`, or an
entry of `GetPeriodsAsync()` on `IRanksApi`: the Unix time, in seconds, of the season's UTC
start. Unflushed counts join a season read only for the current season, the one they will be
written to. With seasons off nothing is stored per season, so only lifetime reads move.

`GetComboName` is the name `!combos` prints for a combo: its `combo.<key>` translation
with chat colour tokens such as `{lime}` stripped, or the bare key when there is none.
It is synchronous too, reading the loaded translations in the server's language. It
always reads `combo.<key>` — a rule's `Message` names only the chat line.

Combo keys are operator-defined, so the set is server-specific: treat an unfamiliar key
as ordinary data, not an error.

---

## Troubleshooting

| Problem | Possible Cause | Solution |
|---------|----------------|----------|
| Nothing is awarded | No combos configured | Copy from `resources/examples/combos.jsonc` |
| "this module is idle" at startup | Ranks is not installed | Install Ranks alongside it |
| A combo was skipped at load | Unknown attribute, empty `RequiresAll`, zero `Points`, or `Count` with suppression | The log names the combo and the reason |
| Chat shows `noscope_hs` instead of words | No `combo.noscope_hs` translation | Add it to `resources/translations/en.jsonc` |
| A combo never fires | A more specific combo is winning | Check for an overlapping combo with more attributes |
| Points look doubled | Additive mode is intended — the combo adds to core's kill award | Enable `SuppressCoreAwards` to replace instead |
| **Before seasons** lists no combos | Counts are kept per season only from when seasons were switched on | Expected — they are in **All time** |
| No combo is recorded at all once seasons are on | `ranks_combos_period` is missing, and every write needs it with seasons on | Check the startup log for the migration error, then create the table — see Installation |
