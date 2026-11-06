# Ranks_EloPoints

Faceit-style Elo rating for the [Ranks](../../README.md) plugin.

---

## Features

- 🏅 **Rating from match outcome** - beating a stronger team gains more; losing to one costs less
- 📉 **Shrinkage** - a short session is not worth a full result
- 💪 **Performance bonus** - rewards carrying a losing team
- 🚪 **Abandon penalty** - leaving, or going spectator, has a cost
- 📅 **Seasons** - follows core's seasons: everyone restarts, and past seasons' final ratings are kept
- 🔌 **Published API** - standings and leaderboard for other plugins, per season

**This is an absolute module.** While installed it is the sole scoring authority and
core stops scoring entirely. At most one absolute module may be active at a time.

---

## Requirements

- `Ranks` 1.1.0 or newer, installed and loaded
- **`Rank.UseCoreRankingLogic` set to `false` in core's `config.jsonc`**
- A SwiftlyS2 database connection — the same one core uses, named in **core's** config
- A competitive match configuration (`mp_maxrounds`, halftime, win conditions)

> [!WARNING]
> **Leaving `UseCoreRankingLogic` on is the one misconfiguration that silently
> produces nonsense.** Core keeps awarding per kill while this module overwrites the
> same value with a rating, so the number means neither thing. The module warns once at
> startup when it sees that combination — it warns rather than overriding, because
> core's config is the operator's to set.

### Scope

**Built for competitive 5v5 and nothing else.** It assumes two teams, a round
structure, and a match that ends. On FFA, deathmatch, arenas or surf it will produce
numbers, and those numbers will be meaningless. Matching players into sensible teams is
the operator's job — this module rates the result, it does not decide who may play.

---

## Installation

1. Set `Rank.UseCoreRankingLogic` to `false` in **core's** `config.jsonc`.
2. Drop the module into your plugins directory alongside `Ranks`.
3. Start the server once. Core creates the tables on first load.
4. Check the console for `Ranks_EloPoints` — a missing `Ranks` is reported there, and
   the module stays idle rather than failing the server.
5. Replace core's rank ladder with a rating-shaped one. Copy this module's
   `resources/examples/ranks-elo.jsonc` over **core's** `ranks.jsonc` (a 10-band
   Faceit-style ladder). Core's default ladder is built for accumulating points and will
   put everyone in the bottom tier.

---

## Commands

**None.** This module ships no commands of its own.

It writes the rounded rating into core's `points`, so `!rank`, `!top`, the scoreboard
and the clan tag all show it already. Adding an `!elo` would be a second way to read
the same number. Core's `!resetrank` and `!resetmyrank` clear the rating as well — see
[Seasons and resets](#seasons-and-resets).

---

## Configuration

Config file: `addons/swiftlys2/plugins/Ranks_EloPoints/config.jsonc`

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Rating.Mode` | string | `"RoundAccumulation"` | `RoundAccumulation` scores every round and averages; `Classic` scores the match result once |
| `Rating.K` | int | `50` | Maximum swing between two evenly matched teams — 50 gives ±25 on a clean result |
| `Rating.PerformanceBonus` | int | `10` | Maximum swing from individual performance; `0` makes rating depend on the result alone |
| `Rating.ConfidenceRounds` | int | `10` | Rounds of evidence a result is worth before it is believed in full; `0` disables shrinkage |
| `Rating.ExpectedRounds` | int | `24` | Fallback match length, used only when `mp_maxrounds` cannot be read |
| `Abandon.Penalty` | int | `25` | Deducted on top of the match result when a player leaves and never returns; `0` disables |
| `General.DetailedLogging` | bool | `false` | Narrates expected flow; warnings and errors print regardless |

### Example Configuration

<details open>
<summary>Default <code>config.jsonc</code></summary>

```jsonc
{
  "EloPoints": {
    "Rating": {
      "Mode": "RoundAccumulation",
      "K": 50,
      "PerformanceBonus": 10,
      "ConfidenceRounds": 10,
      "ExpectedRounds": 24
    },
    "Abandon": {
      "Penalty": 25
    },
    "General": {
      "DetailedLogging": false
    }
  }
}
```

</details>

This module ships no `commands.jsonc` — it registers no commands of its own.

### Read from core instead of configured here

Six settings already exist in core's config, and a rating module reads them rather
than making the operator set the same thing twice:

| Core setting | Used as |
|---|---|
| `Rank.StartPoints` | the rating an unrated player enters their first match with |
| `Rank.MinPoints` | the floor a rating cannot fall below |
| `Rank.MinPlayers` | a round played with fewer humans connected is not rated |
| `Rank.WarmupPoints` | whether warmup rounds are rated |
| `Rank.BotsEnabled` | with it off, a round against a team made up only of bots is not rated |
| `Database.VirtualPurgeDays` | how long a season lasts; every rating restarts when one ends |

Core still applies `MinPlayers`, `WarmupPoints` and `BotsEnabled` to its statistics with
`UseCoreRankingLogic` off, and this module rates exactly the rounds core counted, so a
rating never moves on play core's statistics left out, and never stays still on play they
kept. A drawn round is rated for nobody, as core records it for nobody.

> [!NOTE]
> That needs Ranks 1.0.4 or newer. Against an older Ranks the module rates under its own
> rules and logs a warning once: a match needs `Rank.MinPlayers` humans who played in it,
> warmup is read from the match phase, and with `Rank.BotsEnabled` off a match where one
> side had no rated humans is unrated.

### The two modes

**`RoundAccumulation` (default)** scores each round against the expectation at that
moment, then averages across the match. A player who joins at round 18 is rated on the
four rounds they played, not on a result they had almost no part in.

**`Classic`** is textbook Elo: one win/draw/loss against one expectation, scaled by how
much of the match the player was present for. Simpler, and much blunter about
substitutes.

Both run through the same code path and the same clamps; they differ only in how the
actual and expected scores are produced.

### Tuning `K` and `ConfidenceRounds` together

They pull in opposite directions and are easy to set contradictorily.

`K` sets the ceiling. `ConfidenceRounds` decides how much of that ceiling a given
result actually earns — a 13-0 stomp between even teams is worth `+25` unshrunk, but
`+14.1` at the shipped `ConfidenceRounds = 10`.

If ratings feel sluggish, **raise `K` before lowering `ConfidenceRounds`**. Lowering
confidence makes short sessions swing as hard as full matches, which is the thing it
exists to prevent.

---

## How It Works

**Overview**

Rating is settled once per match, from the result relative to expectation — not per
kill.

**During the match**

Each player gets a session the moment they join a live match, capturing the counters
they started from. Rounds are scored as they end. Joining late or leaving early changes
how much of the match a player is credited with, rather than excluding them.

**At match end**

Expected score comes from the two teams' average ratings. Actual score comes from the
result — per round and averaged in `RoundAccumulation`, once in `Classic`. The
difference is scaled by `K`, shrunk toward zero when the player saw fewer rounds than
`ConfidenceRounds`, adjusted by the performance bonus, then clamped and written.

**Rating Flow**

```
Match Start → Session Capture → Round Scoring → Match End → Shrinkage → Bonus → Clamp → Write
```

### The abandon penalty

A player who disconnects, **or moves to the spectator team**, and is still absent when
the match ends takes `Abandon.Penalty` on top of whatever the match itself cost or paid
them. Coming back clears it — the penalty is decided at match end, not when they leave,
so a reconnect inside the same match is invisible to the rating.

Three consequences worth knowing before the first bug report:

- **Going to spectator counts as leaving.** Without that, the correct play when losing
  would be to spectate, and the ladder would be worthless.
- **AFK managers move idle players to spectator.** On a server running one, an AFK
  player is abandoning by this definition and will be penalised.
- **A late filler pays the same 25 as a full-match abandoner.** Someone who joins at
  round 17 and leaves at round 19 earns about `+0.4` and loses 25. That mildly
  discourages filling an empty slot; set `Abandon.Penalty` to `0` if that matters more
  on your server than deterring quitters.

**How large the penalty needs to be.** Leaving early accrues almost no loss, so the
penalty has to cover everything staying would still have cost. The threshold that
always deters is **`K × E`**. The shipped `25` is `K / 2`, which fully covers
**balanced** matches — `E = 0.5` caps the loss there at 25. On a lopsided match a
determined quitter can still beat it by leaving in the first third; closing that needs
roughly `K × 0.9` (`45` at the defaults), which is harsh on genuine disconnects
everywhere else.

`docs/verify-figures.ps1` reproduces the full table for whatever values you are
considering.

### Seasons and resets

Seasons are core's: set `Database.VirtualPurgeDays` in **core's** `config.jsonc`. This
module has no season setting of its own — it follows core's clock, so the two can never
disagree about when a season ends.

- **When a season ends, every rating restarts.** Core puts everyone back on
  `Rank.StartPoints`, and each player's next match is rated from there, as a new
  player's would be.
- **Past seasons are kept.** A player's final rating for a season moves to
  `ranks_elopoints_history` at the boundary if they are online, or the next time they
  join. Until then it stays in `ranks_elopoints`, tagged with that season's `period` —
  anything reading the table directly should filter on `period`.
- **A match in progress when a season ends goes unrated.** Its early rounds were rated
  against last season's ratings, and the rest is not a whole match.
- **`!resetrank` and `!resetmyrank` clear the rating** for the current season; past
  seasons stay. A player reset mid-match is rated on the rounds after the reset only,
  as if they had just joined.

With `Database.VirtualPurgeDays` at `0` there is only ever one season: ratings never
restart, and nothing above changes how the module behaves — except that a reset now
sticks.

---

## For plugin developers

The module publishes `Ranks.EloPoints.Api.v1`. Reference
`Ranks_EloPoints.Contract.dll` from this module's `resources/exports/` — **do not copy
it next to your plugin**, or the host loads a second one, the interface gets two type
identities, and binding silently stops matching. (The copy beside
`Ranks_EloPoints.dll` is this module's own load-time fallback — ignore it.)

```csharp
public interface IEloPointsApi
{
    const string InterfaceKey = "Ranks.EloPoints.Api.v1";
    const long AllTime = -1;

    Task<EloStanding?> GetStandingAsync(ulong steamId);
    Task<EloStanding?> GetStandingAsync(ulong steamId, long period);
    Task<IReadOnlyList<EloStanding>> GetTopAsync(int limit = 10);
    Task<IReadOnlyList<EloStanding>> GetTopAsync(int limit, long period);
    bool IsInRatedMatch(ulong steamId);
}
```

`Rating` is fractional and authoritative; core's `points` holds the rounded form for
the ladder and the scoreboard, so the two legitimately differ by up to half a point.

The calls without a `period` answer for the **current season**: a player who has not
finished a match since the season started has no standing and is not on the
leaderboard. Pass a period id from core's `IRanksApi.CurrentPeriod` or
`GetPeriodsAsync()` for a past season's final ratings. `AllTime` answers `null` and an
empty list — a rating is a level, not a running total, so there is nothing to add up
across seasons.

`IsInRatedMatch` reports that a match is running, not what it is currently worth — a
match has no result until it ends, and a provisional one would be treated as settled.
`GetStandingAsync` therefore returns the last **settled** rating throughout a match.

---

## Troubleshooting

| Problem | Possible Cause | Solution |
|---------|----------------|----------|
| "this module is idle" at startup | Ranks is not installed | Install Ranks alongside it |
| Warning "this Ranks version has no seasons" | Ranks is older than 1.1.0 | Update Ranks; until then ratings never restart and resets do not clear them |
| Warning about `UseCoreRankingLogic` | Core is still scoring too | Set it to `false` in core's config |
| Everyone sits in the bottom tier | Core's point-shaped ladder is still in use | Copy `resources/examples/ranks-elo.jsonc` over core's `ranks.jsonc` |
| Everyone's rating dropped to the start value | A new season started (`Database.VirtualPurgeDays`) | Expected — last season's final ratings are kept |
| One match was not rated | It was in progress when a season ended | Expected — the next match is rated normally |
| Ratings barely move | `ConfidenceRounds` shrinking short sessions | Raise `K` before lowering `ConfidenceRounds` |
| No match is ever rated | Every round played below `Rank.MinPlayers`, in warmup, or against a team of only bots, or no match end | Check core's `MinPlayers` and `BotsEnabled`, and the match config |
| AFK players losing rating | AFK manager moves them to spectator | Lower or disable `Abandon.Penalty` |
| Numbers look meaningless | Running on FFA, DM, arenas or surf | Unsupported — this module is 5v5 only |
