# Ranks_CustomCombos — features

Operator-defined kill combinations. Core prices each kill attribute separately and stacks
them; this module prices the **combination**, and can optionally take the kill over.

> Specified by [FR-MOD-06](../../../docs/functional-requirements.md). This document
> follows it except where §9 records a deviation. Seasons follow FR-DB-10.

---

## 1. What a combo is

A named match plus a point value, declared in the module's own config:

```jsonc
"Combos": {
  "noscope_hs": {
    "RequiresAll": [ "headshot", "noscope" ],
    "Points": 10
  },

  "nade_triple": {
    "RequiresAll": [ "weapon_class:grenade" ],
    "Count": 3,
    "Points": 100
  },

  "clean_knife": {
    "RequiresAll": [ "weapon_class:knife" ],
    "RequiresNone": [ "attackerblind" ],
    "Points": 50
  }
}
```

The config key is the combo's identity: it becomes the award reason `combo.noscope_hs`,
the translation key, and the database row key. Renaming a key starts a new counter.

The module ships **no combos by default** (FR-MOD-06). The vocabulary exists so servers
can build their own; shipping opinionated combinations is a balance decision that belongs
to the operator.

---

## 2. The attribute vocabulary

Combos are custom; attributes are not. Every kill produces an attribute set, and a combo
can only test what is in it.

### 2.1 Raw event flags

The attribute name **is** the `player_death` field name — one canonical spelling, and an
operator can confirm it against an event dump rather than against our documentation.

| Attribute | True when |
|---|---|
| `headshot` | `Headshot` |
| `noscope` | `NoScope` |
| `thrusmoke` | `ThruSmoke` |
| `attackerblind` | `AttackerBlind` |
| `attackerinair` | `AttackerInAir` |
| `assistedflash` | `AssistedFlash` |
| `penetrated` | `Penetrated > 0` — the field is a count, not a flag |
| `dominated` | `Dominated > 0` |
| `revenge` | `Revenge > 0` |

Four of these — `attackerinair`, `assistedflash`, `dominated`, `revenge` — are things
**core does not price at all**. A single-attribute combo is therefore legitimate and must
not be rejected as redundant; it is the only way to score them.

### 2.2 Parameterised attributes

Fields that carry a value rather than a flag take an argument after a colon:

| Attribute | Matches |
|---|---|
| `hitgroup:<n>` | `ActualHitGroup == n` |
| `weapon:<classname>` | `Weapon == classname`, e.g. `weapon:weapon_ak47` |
| `distance:<units>` | `Distance >= units`, e.g. `distance:2000` |

`distance:<units>` replaces FR-MOD-06's `long_distance` plus a module-level
`LongDistanceUnits` setting. One combo wanting 2000 and another wanting 4000 is a normal
thing to express, and a single module-wide threshold cannot. It also means this module
needs no distance configuration of its own.

### 2.3 Derived attributes

No raw field exists, so these are computed and named for what they mean:

| Attribute | True when |
|---|---|
| `weapon_class:<class>` | `knife` / `grenade` / `inferno` / `taser` / `pistol` / `smg` / `rifle` / `sniper` / `shotgun` / `heavy` |
| `team_kill` | attacker and victim on the same team |
| `suicide` | no attacker, or attacker is the victim |
| `first_blood` | first kill of the round, tracked by the module |

`weapon_class` is **the module's own mapping, not core's.** Core classifies only five
weapon kinds because those are the five it prices; the ten here exist so a combo can say
"any sniper".

### 2.4 Absent on purpose

There is deliberately **no `victim_blind` or `crouched`** (FR-MOD-06): `EventPlayerDeath`
carries neither, and both would need victim pawn state read at death — a separate and
racier mechanism. Add them only with an explicit decision about where the read happens.

---

## 3. Matching

```csharp
RanksKillMatch {
    string[] RequiresAll;    // every attribute must be present
    string[] RequiresNone;   // none of these may be present
    int      Count;          // 1 = a single kill; N = N kills on the same tick
}
```

Matching is declarative rather than a predicate so a combo is expressible in config at all.

### 3.1 Count is same-tick only

`Count: 3` means three matching kills **from the same tick** — one grenade detonation
killing three players. It is not a killstreak.

Streaks and spread-over-time multi-kills are **core's**, which already owns eleven streak
tiers and `Points.SecondsBetweenKills`. This module never competes with that; a combo that
wanted "three kills in five seconds" is a core streak, not a combo.

> This drops FR-MOD-06's `Within` field. With same-tick as the only window, `Within` has
> exactly one legal value and a config field whose only valid setting is `0` is noise.

### 3.2 Closest match wins

A combo matches when `RequiresAll` is a subset of the kill's attributes and no
`RequiresNone` attribute is present. Several can match at once; **only one pays** — the one
with the most `RequiresAll` entries.

Given a no-scope wallbang headshot and

| Combo | RequiresAll | Points |
|---|---|---|
| `noscope_hs` | headshot, noscope | 10 |
| `noscope_hs_wb` | headshot, noscope, penetrated | 25 |

the award is **25, not 35**. Operators write combos as escalating tiers, and paying every
match compounds them into numbers nobody configured.

Ties between equally specific combos resolve by **config order**, first wins. Resolving by
value would let an operator reorder behaviour by editing a number.

### 3.3 Validation at load

Reported once at load with the offending combo named, and the combo skipped — a rule that
silently never matches is indistinguishable from one that is merely hard to trigger:

- an unknown attribute, or a malformed parameterised one
- an attribute in both `RequiresAll` and `RequiresNone`
- `Count > 1` together with suppression (§4.2)

---

## 4. Awarding

### 4.1 Additive by default

A matched combo pays **on top of** core's flat catalogue: a no-scope headshot scores
`kill + headshot + noscope + combo.noscope_hs`. The configured number is a
bonus, not a total — an operator prices a combo knowing the components have already paid.

### 4.2 Suppression

One global switch:

```jsonc
"SuppressCoreAwards": true
```

On a kill where a combo matched, core awards nothing for that kill and the combo's value is
the whole award. Where no combo matched, core behaves exactly as always — otherwise an
ordinary kill would pay nothing.

Global rather than per-combo: one rule an operator can hold in their head beats a per-combo
flag whose effect depends on which combo happened to win §3.2.

**`Count > 1` combos cannot suppress.** The arbiter is called per kill, so on a three-kill
combo the module cannot know during kills one and two that a third is coming; it can only
claim on the third, by which point core has already paid for the first two. Suppressing
only the last would produce "two kills paid, one suppressed, plus the combo" — a number no
operator would predict. Configuring both is a load-time error.

Suppression is scoped deliberately:

| | Suppressed? |
|---|---|
| `kill`, attribute awards, weapon class | yes |
| Killstreak | **no** — a streak is a property of the round, not of this kill |
| `kills` / `deaths` / `assists` counters | **never** |

That last row holds because a claim only replaces an award: points are a judgement,
counters are facts. Whether a kill counts at all is decided earlier, by FR-PTS-03 —
below `MinPlayers`, during a warmup `WarmupPoints` excludes, or with `BotsEnabled` off
when a bot is involved or a team is made up only of bots, core drops the death before
this module is consulted, for everyone.

---

## 5. What it needs from core

Suppression cannot be implemented from outside core. Core awards the kill in its own
handler; a module hooking `player_death` separately cannot stop that, and plugin ordering
is not guaranteed. Core must ask before it awards.

```csharp
public sealed record RanksKillContext(
    ulong AttackerSteamId, ulong? VictimSteamId, bool VictimIsBot, bool FriendlyFire,
    string Weapon, float Distance, int HitGroup, IReadOnlySet<string> Attributes);

public sealed record RanksKillClaim(
    string Reason, int Points, string? DisplayText = null, bool SuppressCoreAwards = false);

public interface IRanksKillArbiter { RanksKillClaim? ClaimKill(RanksKillContext context); }

// IRanksApi
void SetKillArbiter(IRanksKillArbiter? arbiter);
```

**Flag-shaped facts arrive in `Attributes`; valued ones stay as fields.** An arbiter matching
"past 2000 units" or "hitgroup 5" needs the number, and core cannot pre-expand a set entry
for every threshold an operator might configure — so `Weapon`, `Distance` and `HitGroup`
travel as values and the comparison happens module-side.

Core calls the arbiter inside its kill award path with the attribute set it already
computed. A returned claim is awarded **through core's own pipeline**, which is the point of
this shape rather than a plain `AddPoints` call:

| Comes free from core | Would otherwise be reimplemented here |
|---|---|
| `MinPlayers`, `WarmupPoints`, `BotsEnabled`, `FfaMode` | all four, from `GetPointSettings` |
| VIP multiplier and flags | `VipMultiplier`, `VipFlags` |
| Single away-from-zero rounding | the whole rounding rule |
| `Points.ShowPointMessages` **and** the per-player XP-messages toggle | impossible — the per-player flag is not on `IRanksApi` |
| `RoundEndSummary` batching | impossible — the buffer is core-internal |

The last two are why the module must not print its own chat line: it would spam players who
switched point messages off, and bypass round-end batching.

One arbiter at a time, the same operator constraint as the absolute rating module
(FR-PTS-00).

### 5.1 Why the reason is not the message

`reason` is not display-only — it is written to the detail log and carried on
`RaisePointsChanged`, which other modules subscribe to. Translated prose there would make a
module-facing identity change with the server's language, so the reason stays `combo.<key>`
and `DisplayText` carries the text.

### 5.2 Attributes come from core

The module does not read `player_death` itself. Core computes the flag set once and hands it
over, so there is one implementation of `penetrated > 0` rather than two free to drift.

The valued comparisons are the other way round — core ships `Weapon`, `Distance` and
`HitGroup` raw and the module compares them, because the thresholds live in this module's
config and core has no way to know them.

---

## 6. Messages

Each combo resolves a translation key, defaulting to `combo.<key>`; an explicit `Message`
overrides it when two combos should share wording.

Because combos are operator-defined, **a missing translation is the normal case**, not an
edge case — and SwiftlyS2's localizer answers a miss with `{lang}.{key}`, so a forgotten key
puts `en.combo.noscope_hs` in chat. A miss falls back to the bare combo key: ugly, but
readable.

Resolution uses the **server** localizer, matching core — `ReportPoints` uses
`_core.Localizer`, not the per-player one, so per-player combo text would be the only
message on the server that differs between viewers.

---

## 7. Table and command

`ranks_combos` holds lifetime counts and `ranks_combos_period` the same counts per season
(§7.1). Both are named through `IRanksApi.ResolveTableName`, so they inherit core's prefix and
suffix, and migrated through `RegisterMigrations` (FR-API-06).

| Column | Type | |
|---|---|---|
| `steam_id` | BIGINT | PK with `combo_key` |
| `combo_key` | varchar(64) | PK with `steam_id` |
| `count` | BIGINT | |
| `last_at` | DATETIME | |

`ranks_combos_period` has the same columns plus `period` (BIGINT), which joins the key:
`(steam_id, combo_key, period)`. It got a migration of its own, `202611060100`, so the
lifetime table keeps exactly the shape other plugins already read.

Tall rather than a column per combo: combos are operator-defined, so a wide table cannot
have a column for a key that does not exist yet.

Counts accumulate in memory and flush on a timer, on map change, on unload and when core opens
a new period — `ClaimKill` runs inside core's award path and must never touch the database
there. Same Collector/Writer split `Ranks_WeaponStats` already uses.

There is deliberately **no flush on disconnect**, unlike WeaponStats. Its session stats live
in a per-player slot that is cleared on disconnect, so the delta has to be taken first;
these counts are keyed by SteamID in a plain map and simply wait for the next tick of the
timer.

**A combo counts whenever core consults the arbiter, whether or not points follow** — with
`UseCoreRankingLogic` off, or a subscriber cancelling the award, the count still moves.
Core consults it only for a death it counts (FR-PTS-03): below `MinPlayers`, during a
warmup `WarmupPoints` excludes, or with `BotsEnabled` off when a bot is involved or a team
is made up only of bots, there is nothing to count. The arbiter also asks core's play gate
itself before matching, which only matters against a Ranks older than 1.0.4: that core
consulted it below `MinPlayers`, so its points gate stands in, and a kill it would not pay
for is neither counted nor allowed to take `first_blood`.

`!combos`, alias `!mycombos`, name and permission configurable in `commands.jsonc` like
core's. Lists the caller's combos and counts, ordered by count.

### 7.1 Seasons

Core owns the calendar (FR-DB-10): with seasons on (`Database.VirtualPurgeMode` `Monthly`, or `VirtualPurgeDays` above zero) it closes one
period and opens the next, and a module follows its events rather than keeping a clock or a
setting of its own. Every write then lands twice, in one transaction — added to the lifetime
row exactly as before, and to the row of the period the count was earned in. Both writes are
additive, so two servers sharing a database still accumulate rather than clobber.

**The period travels with the count.** The counters hold none; each drain on the game thread
stamps what it takes with core's current period, or with nothing while periods are off. Core
changes period only on the game thread, and when it does the module drains at once, stamped
with the period just closed, so a count earned before the boundary cannot be filed after it.
A write that fails and is retried keeps its stamp.

| Screen | Periods off | Periods on |
|---|---|---|
| `!combos` / Statistics → Combos | lifetime counts, as in 1.0 | this period's counts, under **All time** and **Previous seasons** rows |
| **All time** | — | lifetime counts |
| **Previous seasons** | — | core's period list minus the current one; each opens that period's counts |

Every list keeps the `customcombos.list` id, because a renderer draws it from its
`ComboStatRow` items whatever period they belong to; the period picker is
`customcombos.periods`. Labels and dates match core's own: the month (`November 2026`) for a
period that ran exactly one UTC calendar month, `Season from yyyy-MM-dd` in UTC for any other,
and `Before seasons` for period 0.

**Before seasons stays empty here.** Core's period 0 holds everything it recorded before periods
were switched on, but this module keeps counts per period only from that moment on — earlier
combos exist only as lifetime totals. The screen says so rather than reading as lost data.

With periods off nothing is written to `ranks_combos_period` at all, and every screen and API
answer is what 1.0 gave. Against a Ranks older than 1.1.0, which has no periods, the module
logs one warning and behaves the same way — lifetime counts only, and `!resetrank` does not
clear them.

### 7.2 Resets and purges

**Reset** (`PlayerReset`, raised by `!resetrank`, `!resetmyrank` and Settings → Reset my rank).
Core wipes only the player's current period and keeps the rest, so the module matches it:

| | Lifetime row | Period rows |
|---|---|---|
| Periods off | deleted | untouched |
| Periods on | the current period's count subtracted, clamped at 0; a row brought to 0 is deleted | the current period's deleted; earlier periods kept |

The clamp is there because a reset made while periods were off already emptied the lifetime row
and left the period rows standing.

A write already under way must not bring back what the reset removed. Every database change —
flush, reset, purge, reconcile — runs through one queue in the writer, in the order the game
thread issued it, so the reset lands after every flush cut before it. The player's unflushed
counts are dropped on the game thread, and retry entries the reset covers are dropped inside the
queue.

**Purge** (`PlayersPurged`, FR-DB-08). Both tables lose every row of each purged player, and the
retry buffer drops theirs. Before 1.1.0 this module did not sweep at all.

**Orphan reconcile.** `PlayersPurged` is a live event, so a purge that runs while this module is
not loaded is never seen. On a cold start, once core is ready, both tables lose every row whose
`steam_id` is not in core's `ranks_players` — the same pass `Ranks_WeaponStats` runs, and skipped
on hot reload for the same reason (FR-DB-09).

---

## 8. Published API

`Ranks.CustomCombos.Api.v1`, shipped as `Ranks_CustomCombos.Contract.dll` from this module's
own `resources/exports/` — the module owns the contract, so nothing else is guaranteed to be
installed to ship it.

```csharp
public sealed record ComboStatRow(string ComboKey, long Count, DateTime LastAt);

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

The async methods merge stored rows with the caller's **unflushed** counts, so a figure read
here matches what `!combos` prints rather than lagging a flush interval behind. The peek
hops to the game thread and copies, because the counter map belongs to that thread and a
consumer calls from wherever it likes; a failed peek degrades to stored-only rather than
failing the query.

The overloads without a period are the 1.0 members, unchanged: lifetime counts. `AllTime`
(`-1`, the same value as core's `RanksPeriods.AllTime`, declared again because this contract
references nothing) asks for the same. Any other period reads that period's rows. Unflushed
counts join a period read only when the next flush will stamp them with that period — the
peek reads the counters and the current period in the same tick — so they never show a
figure the database will not end up holding.

`ConfiguredCombos` is synchronous because it reads the loaded rule set rather than the
database. It is the only way to render a combo an operator has configured but nobody has
landed — no counter row exists for those. It returns **resolution order** (most specific
first), not config order.

`GetComboName` is §6's resolution as `!combos` uses it — the `combo.<key>` translation, or
the bare key on a miss — with chat colour tokens such as `{lime}` stripped. A menu row is
HTML, where a token left in prints literally, and the names are operator-authored and shared
with the chat line, so they routinely carry one. It always reads `combo.<key>`: a rule's
`Message` overrides only the chat line. Synchronous, for the same reason as
`ConfiguredCombos` — it reads loaded translations, not the database.

Combo keys are operator-defined, so the set is server-specific: a consumer must treat an
unfamiliar key as ordinary data rather than an error.

Unlike the Ranks API this is named without JIT isolation, which is correct: a provider hard‑
requires the assembly it publishes from, exactly as core does with `Ranks.Contract`.

---

## 9. Changed from the original specification

FR-MOD-06 has been updated to match everything below, so the two no longer disagree.
Kept here as the record of what moved and why.

| # | Originally | Now | Why |
|---|---|---|---|
| 1 | No `Suppresses` field; additive only (Q10) | Global `SuppressCoreAwards` | The objection — "core has already awarded with multipliers applied, so the module cannot compute an exact offset" — assumed the module sees the kill *after* core. FR-API-11 inverts that, so nothing is retracted. **Cost: with it on, the module is no longer purely additive and gains an ordering dependency on core.** |
| 2 | `through_smoke`, `attacker_blind`, `attacker_airborne`, `wallbang`, `domination` | raw field names | Checkable against an event dump rather than against a table in this document |
| 3 | `Within` in seconds | dropped; `Count` is same-tick | Core owns streaks and timed multi-kills, leaving same-tick as the only window, and a field with one legal value is noise |
| 4 | Per-combo counters optional | required, and readable through a published API (§8) | Nothing else reads the data otherwise |
| 5 | `!combostats` | `!combos` / `!mycombos` | — |
| 6 | `long_distance` + module `LongDistanceUnits` | `distance:<units>` | Two combos wanting different thresholds is ordinary; one module-wide value cannot express it |

Deviation 3 leaves a gap neither core nor this module fills: a streak **restricted to a
weapon or placement** and spread over a round. Core's streaks are weapon-agnostic and
`Count` here is same-tick. FR-PTS-07 records it.

---

## 10. What it deliberately does not do

**Streaks and timed multi-kills.** Core's, and standard there.

**Anything spanning more than one tick.** No "plant and defuse the same round".

**Guard against double scoring.** Running this with `SuppressCoreAwards` alongside an
absolute rating module such as `Ranks_EloPoints` means two authorities deciding the same
number. The module does not detect it; the README carries the warning.
