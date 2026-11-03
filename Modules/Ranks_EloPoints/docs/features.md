# Ranks_EloPoints — features

> **Status:** design, implemented. The restart-reset question is closed
> (confirmed on a live server, §2.1).
> Specified by [FR-MOD-05](../../../docs/functional-requirements.md).

An Elo rating that replaces core's scoring entirely. Rating moves on **outcome
against expectation**: who you beat matters more than how many you killed.

**It is an absolute module** (FR-PTS-00) and pairs with
`Rank.UseCoreRankingLogic = false`. Core keeps persistence, the ladder, the
scoreboard, the commands and the menus; this module supplies the only number
that goes into them.

---

## 1. The model

### 1.1 Expected score

Standard Elo. A player's expected score against an opposing team is derived from
the two team averages:

```
E = 1 / (1 + 10 ^ ((Ropp - Rown) / 400))
```

A 400-point gap means the favourite is expected to win about 91% of the time.
The team average is taken over **rated participants only** — see §3.

### 1.2 The two modes

Both produce an actual score `S` and an expected score `E`, then run through the
identical delta calculation. They differ only in how those two are produced.

| | `Classic` | `RoundAccumulation` *(default)* |
|---|---|---|
| `S` | `1` / `0.5` / `0` from the match result | mean of per-round `S` over rounds the player was present for |
| `E` | one snapshot of the team averages | mean of per-round `E`, recomputed each round |
| Substitutes | rated on a result they barely influenced | rated on the rounds they actually played |

`RoundAccumulation` is the default because mid-match joins and leaves are normal
on a community server, and `Classic` handles them badly — a player who connects
at round 18 of a 21-round match gets the same `S` as someone who played all of
it.

Recomputing `E` per round also means a team that loses two players mid-match is
measured against its *current* strength, not the strength it started with.

### 1.3 Shrinkage — why a short session is not worth a full result

A four-round sample is not evidence of the same quality as a twenty-round one,
and treating it as such is the single easiest way to make a ladder farmable:
join, win three rounds, leave, repeat.

So the actual score is pulled toward the expected one in proportion to how
little evidence there is:

```
S_adj = (n * S_mean + c * E_mean) / (n + c)
```

where `n` is rounds played and `c` is `Rating.ConfidenceRounds`. With no
evidence the result *is* the expectation and the delta is zero; with `n >> c`
the adjustment vanishes.

The full delta, before the bonus and the clamps:

```
base = K * (S_adj - E_mean) * (n / Nm)
```

`n / Nm` is participation — a player present for 4 of 21 rounds moves by roughly
a fifth of what a full participant does. **Shrinkage and participation are not
redundant.** Participation scales the magnitude; shrinkage scales the
*confidence*. Without the second, the per-round rate of a short winning cameo
dwarfs that of a full match:

| Scenario (`K=50`, even teams, 21-round match) | unshrunk | shrunk, `c=10` |
|---|---|---|
| played all 21, won 13-8 | `+5.95` | `+4.03` |
| joined round 18, won all 4 | `+4.76` | `+1.36` |
| **gain per round played** | **4.2× in the cameo's favour** | **1.8×** |

The residual 1.8× is deliberate, not leftover error: winning every round you are
present for *should* out-rate a scrappy 13-8, just not by enough to make
quitting profitable. A two-round cameo lands at `+0.40`.

The same damping applies to blowouts. A 13-0 between even teams is `+25`
unshrunk and `+14.1` shrunk — 13 rounds is still a short match.

### 1.4 The performance bonus

`Rating.PerformanceBonus` adds up to ±`B/2` based on how the player performed
**relative to the others in that same match**:

```
bonus = B * (p - 0.5)
```

where `p` is the player's percentile within the match on a combined measure of
kills, assists, deaths, round wins, bombs planted and bombs defused.

**It is match-relative, not absolute.** An absolute threshold ("25 kills is
good") inflates the whole ladder over time, because the average player on a
quiet server clears it. A percentile within the match is approximately zero-sum,
which preserves Elo's conservation property.

**Objectives are in the measure on purpose.** A pure K/D bonus pays players to
stop defusing the bomb, and the module would then be actively damaging the
matches it rates.

Set `PerformanceBonus` to `0` for pure outcome-based Elo.

### 1.5 The two clamps

Applied in this order, and both are required:

1. **Envelope.** `base` is clamped to `[-K*E, K*(1-E)]`. This is the range a
   single result can legitimately move a rating in standard Elo, and shrinkage
   can otherwise push slightly outside it at small `n`.
2. **Sign floor.** The bonus may not flip the sign of the base. If `base > 0`
   the final delta is at least `+ε`; if `base < 0` it is at most `-ε`.

The second exists for a case the envelope does not catch. A heavy favourite
(`E = 0.95`) who wins earns `base = +2.5`; a `B = 10` bonus at the bottom
percentile subtracts `5`, giving `-2.5` — a **loss of rating for winning**, and
it sits comfortably *inside* the envelope. The clamp is defined on the sign of
`base` rather than on the match result, which is what makes it correct in
`RoundAccumulation`: a player who joined late and lost every round they played
should still go down even if their team went on to win.

Finally the rating floors at core's `Rank.MinPoints`.

---

## 2. The match lifecycle

### 2.1 Where a match starts and ends

Both boundaries come from `Core.Game.MatchData`, a `CCSMatch` the framework
exposes directly — **not** from `CCSGameRules`, whose generated binding carries
two same-named round counters (`TotalRoundsPlayed` and `TotalRoundsPlayed1`)
with nothing to tell them apart.

| Signal | Field |
|---|---|
| rounds played so far | `ActualRoundsPlayed` |
| warmup / live / halftime / ended | `Phase` |
| score, including per half and overtime | `CTScoreTotal`, `TerroristScoreTotal`, … |

**Restarts are detected by the counter moving backwards.** That is
source-agnostic: it catches `mp_restartgame` from a knife-round plugin, an
admin, or a match plugin, without the module knowing which fired it, and without
a list of events to keep current. Everything accrued before the restart is
discarded.

`ActualRoundsPlayed` is also better than summing the two scores, which is what
the `ServerReporter` plugin in the Rampage repository does for its own purposes:
a drawn round increments rounds played but neither score.

Match end is `EventCsWinPanelMatch`, with `Phase == GAMEPHASE_MATCH_ENDED` as a
second route to the same event. The backstop is not redundancy for its own sake
— if the event ever stops firing, the failure is that *nothing is ever written
for any match*, which is the one outcome that must not be silent.

> **Confirmed on a live server (2026-10-26): `ActualRoundsPlayed` resets on
> `mp_restartgame`.** So a knife round is discarded cleanly rather than counting
> as a phantom round, and the backward-movement test is the whole of restart
> detection.

A match that never reaches an end — map change, server restart — **pays nobody**.
Nothing is written mid-match, so there is nothing to undo.

### 2.2 Joining and leaving

A match session is keyed on **SteamID, not slot**, and survives a disconnect.
That is what makes a reconnect invisible: the same session resumes accruing, and
the player is settled at match end as though they had never left.

**Nothing is written to the database before match end.** Whether a player
abandoned is only knowable once the match is over, so there is no earlier moment
at which a correct decision could be made. One write per player per match.

### 2.3 Abandonment

A player who is absent when the match ends — disconnected, or moved to the
spectator team — takes `Abandon.Penalty` **on top of** their match result.

On top, rather than instead of, because a flat replacement can be *cheaper* than
playing on. Against a heavy favourite (`E = 0.9`) in a 24-round match, losing
0-24 costs `-31.76` if they stay:

| leaves at | accrued | penalty **replaces** | penalty **on top** |
|---|---|---|---|
| round 14, down 3-11 | `-11.67` | `-25.00` ← **better than staying** | `-36.67` |

Under *replace*, the flat amount would have to exceed the largest possible loss
to deter at all, and would then be disproportionate everywhere else.

### Where the penalty is and is not sufficient

**On top is the right shape, but 25 is not a universal deterrent**, and the
model says exactly where the line is. Leaving early accrues little loss, so the
penalty has to cover everything staying would still have cost:

| leaves at | accrued | with `Penalty = 25` | vs staying `-31.76` |
|---|---|---|---|
| round 4, down 1-3 | `-1.55` | `-26.55` | **not deterred** |
| round 8, down 2-6 | `-4.81` | `-29.81` | **not deterred** |
| round 14, down 3-11 | `-11.67` | `-36.67` | deterred |
| round 20, down 5-15 | `-18.06` | `-43.06` | deterred |

The threshold that always deters is **`K × E`** — the envelope bound on how much
a single match can cost. So:

- `Penalty = K / 2` (the shipped `25` at `K = 50`) fully covers **balanced**
  matches, where `E = 0.5` caps the loss at 25. That is the common case.
- Lopsided matches leave a window in the first third. Closing it needs
  `Penalty` near `K × 0.9` — `45` at the defaults — which is harsh on genuine
  disconnects in every other match.

The default takes the first trade. An operator who sees early quitting on
one-sided matches should raise it toward 45 knowing what it costs elsewhere.

> Early abandonment also requires predicting a loss from a 1-3 start, which is
> a much weaker read than quitting at 3-11. The window is real, bounded and
> documented rather than hidden — [`verify-figures.ps1`](verify-figures.ps1)
> reproduces this table for any `K`, `c` and `Penalty`.

Spectator detection is a team change to `Team.Spectator` (1) specifically —
never `Team.None` (0), which every player passes through while connecting. A
dead player watching is observer mode, not a team change, and halftime is T↔CT.
Neither produces a false positive.

---

## 3. Bots and unrated players

Team averages are built from **rated humans**. Two populations need a rule:

| Population | Rule |
|---|---|
| Bots | imputed at the mean rating of the rated humans on their own team |
| Players whose record failed to load | excluded, exactly like a bot with no team |
| Spectators | accrue nothing — they are not in the match |

An all-bot team falls back to the mean of every rated player in the match, and
then to `Rank.StartPoints` if there are none.

Whether a round against bots can be rated is core's `Rank.BotsEnabled`:

- `false` — a round in which either team is made up only of bots is not rated. Core
  does not count it (FR-PTS-03), and this module rates exactly the rounds core counted.
- `true` — impute and rate normally.

Against a Ranks older than 1.0.4, which cannot report which rounds it counted, the
module falls back to a match-level rule: with `BotsEnabled` off, a match in which one
side had no rated humans is unrated for everyone, and nothing is written.

Reading core's setting rather than adding one here is FR-MOD-05 rule 3: the
operator has already stated whether bots count.

---

## 4. What it needs from core

| Need | Surface | Status |
|---|---|---|
| Current rating / write rating | `GetPoints`, `SetPoints` | exists (FR-API-02) |
| Per-match performance inputs | `GetSnapshot` | exists (FR-API-02) |
| Operator intent (`StartPoints`, `MinPoints`, `MinPlayers`, `WarmupPoints`, `BotsEnabled`) | `GetPointSettings` | exists (FR-API-05) |
| Adopt a mid-match joiner | `PlayerDataLoaded` | exists (FR-API-04) |
| Shared connection, affixed names, schema | `ConnectionName`, `ResolveTableName`, `RegisterMigrations` | exists (FR-API-06) |
| Durability at match end | `SavePlayerAsync`, `SaveAllAsync` | exists (FR-API-09) |
| Sweep its rows on purge | `PlayersPurged` | exists (FR-API-04) |

**Performance inputs come from diffing `GetSnapshot`, not from `GetStat`.** Both
expose the same counters, but they are lifetime totals — the module needs the
delta across one match, so it snapshots at match start and again at match end.
Mid-match joiners are snapshotted on `PlayerDataLoaded` instead, which is the
only moment their record is guaranteed resident.

Core's counters keep updating with `UseCoreRankingLogic` off (FR-PTS-00), which
is what makes this work at all.

### Deviation from FR-MOD-05

Rule 4 says a rating module writes "at round end, match end and on disconnect".
This module writes **at match end only**. Round-end writes would be thrown away
by a restart, and a disconnect write cannot be correct because abandonment is
not yet decided. The durability rule 5 exists to protect is satisfied by the
module's own table being authoritative (§5).

---

## 5. Table

`ranks_elopoints`, affixed via FR-API-06, migrated by core against this module's
ledger (FR-DB-05).

| Column | Type | Why |
|---|---|---|
| `steam_id` | `BIGINT` PK | |
| `rating` | `DECIMAL(10,4)` | **the authoritative value** |
| `matches` | `INT` | played to completion |
| `abandons` | `INT` | for operator visibility, not used in the formula |
| `updated_at` | `DATETIME` | |

**The `DECIMAL` is the point of the table.** Core's `points` is an `INT` (Q15),
and Elo deltas are fractional. Truncating each delta into core would accumulate
error in one direction, because truncation never rounds back — a player gaining
`+4.03` twenty times would be credited 80 rather than 81, and the drift is
permanent.

So the module keeps the exact rating and writes the **rounded** value to core
via `SetPoints`. Core's `points` is the display and ladder value; this column is
the rating.

A player evicted from core's registry before the write still has their rating
committed here, and it is pushed to core on their next `PlayerDataLoaded`.

---

## 6. What it deliberately does not do

| Not done | Why |
|---|---|
| Placement matches (higher `K` for a new player's first N) | needs a per-player match counter and interacts with shrinkage in ways worth modelling first; purely additive later |
| Decide who may join or queue | this module rates, it does not gate — matchmaking is the operator's system |
| Adjust for a 5v4 after a disconnect | Faceit does not either; the expected score already reflects the remaining team's average |
| Handle FFA, deathmatch or arenas | no meaningful notion of two teams or a match result |
| Register itself as the active scorer | core deliberately tracks no such thing (FR-PTS-00); two absolute modules at once is an operator constraint, not a detectable error |
| Award anything per kill | that is core's model, and this module exists to replace it |
