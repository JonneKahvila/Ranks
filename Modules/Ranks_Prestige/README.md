# Ranks_Prestige

Prestige levels for the [Ranks](../../README.md) plugin, as a virtual point deduction.

---

## Features

- ♻️ **Virtual deduction** - the scoreboard restarts at zero each prestige; stored points keep climbing
- 🏷️ **Prestige tag** - a prefix on core's clan tag, composed by core rather than fought over
- 🎖️ **Per-level scoreboard mode** - a level can switch between Premier, Competitive, Wingman or Custom
- ⚡ **Follows every points change** - the display moves with the award, not on a timer
- 🗄️ **No database, no migration** - the level is derived from points the player already has
- 🔌 **Published API** - other plugins read each player's level and progress

> [!NOTE]
> **Nothing is deducted from the player's real total.** Points, stats and the database row are
> untouched; `!rank` and `!top` keep showing the true standing. Only the scoreboard changes.

---

## Requirements

- `Ranks` 1.1.0 or newer, installed and loaded

No database connection of its own and no tables — the prestige level is derived from the points
core already stores.

---

## Installation

1. Drop the module into your plugins directory alongside `Ranks`.
2. Start the server once. `config.jsonc` and `commands.jsonc` are created.
3. Edit the levels to match your server's scoring pace — the shipped thresholds assume a ladder
   where 40 000 points is a long grind.

---

## Commands

### Player Commands

| Command | Aliases | Permission | Description |
|---------|---------|------------|-------------|
| `!prestige` | `!myprestige` | None | Your prestige level and progress toward the next one |

Two lines: the level your current points reach, and those points against the next
level's threshold (`Next level: 1234/4000`). Both are absolute totals, not per-level —
the scoreboard overlay is the one place a threshold is subtracted.

Name, aliases and permission are editable in `commands.jsonc`. An empty `Command` removes it.

---

## Configuration

Config file: `addons/swiftlys2/plugins/Ranks_Prestige/config.jsonc`

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | bool | `true` | Turns the module off without uninstalling it; clears every overlay |
| `TagPrefix` | string | `"[P{0}] "` | Prepended to core's clan tag, `{0}` being the level. Empty writes no prefix |
| `Levels` | array | 5 levels | The prestige steps |
| `Levels[].Level` | int | — | The number shown to the player. Must be above zero |
| `Levels[].MinimumPoints` | int | — | Stored points this level opens at, **and** the amount deducted |
| `Levels[].RankMode` | string | `""` | `Premier`, `Competitive`, `Wingman` or `Custom`. Empty keeps core's mode |
| `DetailedLogging` | bool | `false` | Narrates expected flow; warnings and errors print regardless |

### Example Configuration

<details open>
<summary>Default <code>config.jsonc</code></summary>

```jsonc
{
  "Prestige": {
    "Enabled": true,
    "TagPrefix": "[P{0}] ",
    "Levels": [
      { "Level": 1, "MinimumPoints": 40000, "RankMode": "" },
      { "Level": 2, "MinimumPoints": 80000, "RankMode": "" },
      { "Level": 3, "MinimumPoints": 120000, "RankMode": "" },
      { "Level": 4, "MinimumPoints": 160000, "RankMode": "" },
      { "Level": 5, "MinimumPoints": 200000, "RankMode": "" }
    ],
    "DetailedLogging": false
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
      "prestige": { "Command": "prestige", "Aliases": [ "myprestige" ], "Permission": "" }
    }
  }
}
```

</details>

<details open>
<summary>Progressing through scoreboard modes</summary>

```jsonc
{
  "Prestige": {
    "Levels": [
      { "Level": 1, "MinimumPoints": 40000,  "RankMode": "" },
      { "Level": 2, "MinimumPoints": 80000,  "RankMode": "Competitive" },
      { "Level": 3, "MinimumPoints": 120000, "RankMode": "Wingman" },
      { "Level": 4, "MinimumPoints": 160000, "RankMode": "Custom" }
    ]
  }
}
```

An empty `RankMode` keeps core's configured mode, so level 1 above stays on whatever
`Scoreboard.RankMode` is set to. `Custom` additionally reads core's
`Scoreboard.CustomRankBase` / `CustomRankMax` / `CustomRankMargin`.

</details>

### `MinimumPoints` is both the threshold and the deduction

That is what makes the visible ladder restart. A player on 85 000 points with the example above is
prestige 2, and the scoreboard shows `85 000 − 80 000 = 5 000` — back at the bottom of core's
ladder, with the icon and clan tag to match.

Set the thresholds evenly if you want each prestige to cost the same; the module does not require
it, and uneven steps work.

> [!WARNING]
> **Lowering a threshold promotes players instantly and resets their visible rank**, because the
> level is derived from current points rather than stored on redemption. Raising one demotes them
> the same way. Change thresholds on a live server deliberately.

---

## How It Works

**Overview**

The module never draws anything. It tells core what to display, and core draws it.

**Resolving**

On player load, after every points change, when core starts a new season and when a player is
reset, the module asks core for the player's stored total, finds the highest level whose
`MinimumPoints` it reaches, and subtracts that threshold.

**Displaying**

The result goes to core as a display overlay — displayed points, an optional scoreboard mode, and
the tag prefix. Core resolves the ladder tier from the **displayed** total, so the number, the rank
icon and the clan tag all move together. Core also skips the write when nothing changed, so a kill
that does not move a player between levels costs a comparison.

**Prestige Flow**

```
Points change → resolve level → displayed = points − threshold → core draws tier, icon, tag
```

**Key Mechanics**

- The level is **derived, never stored** — there is nothing to migrate and nothing to get out of sync.
- With core's seasons on (`Database.VirtualPurgeMode` or `VirtualPurgeDays`), a new season
  resets everyone's points, so every prestige restarts with it; past seasons stay in core's
  leaderboard history.
- Below the first threshold the overlay is cleared, so an unpromoted player is drawn exactly as core would.
- Overlays are session-scoped: they vanish on disconnect and on unload.
- Config reloads re-resolve everyone present immediately.

---

## For plugin developers

The module publishes `Ranks.Prestige.Api.v1`. Reference `Ranks_Prestige.Contract.dll` from this
module's `resources/exports/` — **do not copy it next to your plugin**, or the host loads a second
one, the interface gets two type identities, and binding silently stops matching. (The copy beside
`Ranks_Prestige.dll` is this module's own load-time fallback — ignore it.)

```csharp
public sealed record PrestigeProgress(int Level, int Points, int? NextLevelPoints);

public interface IPrestigeApi
{
    const string InterfaceKey = "Ranks.Prestige.Api.v1";

    bool IsEnabled { get; }
    PrestigeProgress? GetProgress(ulong steamId);
}
```

`GetProgress` is what `!prestige` shows. `Level` is `0` below the first threshold; `Points` and
`NextLevelPoints` are absolute totals — the player's real points, and the next level's
`MinimumPoints` or `null` at the top level. It is synchronous because it reads core's resident
record, so it answers only for a loaded player, and it returns `null` while `Enabled` is `false` or
Ranks is absent. `IsEnabled` follows `Enabled` across config reloads.

---

## Troubleshooting

| Problem | Possible Cause | Solution |
|---------|----------------|----------|
| "this module is idle" at startup | Ranks is not installed | Install Ranks alongside it |
| Nobody reaches a prestige | Thresholds far above your scoring pace | Lower `MinimumPoints`, or check `!rank` for real totals |
| The tag shows `[P{0}]` literally | `TagPrefix` lost its `{0}` placeholder | Restore `{0}`, which is replaced by the level |
| No tag prefix appears | Core's `Scoreboard.Clantags` is `false` | Core owns the tag; enable it there |
| The scoreboard mode never changes | `Levels[].RankMode` is empty or misspelled | Use an exact name; an unknown one falls back to core's mode |
| A level was dropped at startup | Two levels share a `MinimumPoints`, or a value is at or below zero | The log names the reason; thresholds must be unique and positive |
