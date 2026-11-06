# Ranks_EloPoints — implementation

> **Status:** design, implemented. The restart-reset question is closed
> (confirmed on a live server, §5.3); remaining open items are in §10 and none
> of them block.
> Companion to [features.md](features.md); core conventions it inherits live in
> the [implementation guide](../../../docs/implementation-guide.md).

Where `Ranks_WeaponStats` is the floor case for a **table-owning** module, this
is the floor case for an **absolute scoring** module: it replaces core's scoring
entirely, reads core's counters as inputs, and writes the only number that
reaches the ladder. If the shape below is awkward, the FR-PTS-00 contract is
wrong — not this module.

---

## 1. Layout

```
Modules/Ranks_EloPoints/
├── README.md
├── docs/
│   ├── features.md
│   └── implementation.md
├── Ranks_EloPoints.csproj
├── resources/
│   ├── templates/config.jsonc
│   ├── translations/en.jsonc
│   └── examples/ranks-elo.jsonc     # 10-band ladder for core's config
└── src/
    ├── Ranks_EloPoints.cs           # BasePlugin, lifecycle, contract isolation
    ├── Api/RanksBridge.cs           # the ONLY file naming IRanksApi
    ├── Api/CoreTypes.cs             # CoreSettings, the flattened view of core's settings
    ├── Config/Config.cs
    ├── Migrations/M202610261200_Rating.cs
    ├── Migrations/M202611060100_Periods.cs  # period column + ranks_elopoints_history
    ├── Match/MatchTracker.cs        # game-event handlers, match state machine
    ├── Match/MatchSession.cs        # one player's accrual for one match
    ├── Rating/EloMath.cs            # pure, static, no engine types
    ├── Rating/Performance.cs        # PerfCounters, weights, percentile
    └── Rating/RatingStore.cs        # off-thread reads and writes
```

`Ranks_EloPoints` — underscore, in the folder, the project and the
`[PluginMetadata] Id`. Core derives the migration ledger from that id.

**`EloMath` names no SwiftlyS2 type and no contract type.** Everything it needs
arrives as `double`, `int` and arrays, so the formula can be read and reasoned
about without the framework in the way.

---

## 2. Lifecycle

### 2.1 ⚠️ Contract isolation comes first

Identical to `Ranks_WeaponStats`, and for the same reason: per implementation
guide §10.2 the CLR resolves every type a method *names* when it JITs that
method, so on a box without `Ranks` a field typed `IRanksApi?` throws
`FileNotFoundException` at the **caller's** frame, and SwiftlyS2 reports
`Hosting failed to start` for every plugin installed.

Exactly one file — `Api/RanksBridge.cs` — names a contract type, every member
touching one is `[MethodImpl(MethodImplOptions.NoInlining)]`, and the field is
`object?`:

```csharp
internal sealed class RanksBridge
{
    private object? _api;                       // deliberately not IRanksApi
    private object? _module;
    public bool IsAvailable { get; private set; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryAcquire(IInterfaceManager interfaceManager) { /* ... */ }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unwire() { }
}
```

This module needs **more** through the bridge than WeaponStats does, and each
accessor is a separate `NoInlining` member returning a primitive or a plain
record:

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
public int? GetPoints(ulong steamId) => _api is IRanksApi a ? a.GetPoints(steamId) : null;

[MethodImpl(MethodImplOptions.NoInlining)]
public void SetPoints(ulong steamId, int points, string reason) =>
    (_api as IRanksApi)?.SetPoints(steamId, points, reason);

// Flattened deliberately: returning RanksPlayerSnapshot would name a contract
// type in the caller's signature and defeat the isolation.
[MethodImpl(MethodImplOptions.NoInlining)]
public PerfCounters? GetCounters(ulong steamId)
{
    if (_api is not IRanksApi a || a.GetSnapshot(steamId) is not { } s) return null;
    return new PerfCounters(s.Kills, s.Deaths, s.Assists, s.RoundWins, s.BombsPlanted, s.BombsDefused);
}

[MethodImpl(MethodImplOptions.NoInlining)]
public CoreSettings? GetSettings() =>
    _api is IRanksApi a && a.GetPointSettings() is { } p
        ? new CoreSettings(p.UseCoreRankingLogic, p.StartPoints, p.MinPoints, p.MinPlayers, p.WarmupPoints, p.BotsEnabled)
        : null;
```

`PerfCounters` and `CoreSettings` are module-owned records. **Returning
`RanksPlayerSnapshot` or `RanksPointSettings` directly would put a contract type
in a signature outside the bridge**, which is the same JIT hazard one indirection
further out — and it is the exact mistake the `object?` field is there to
prevent.

**Audit rule:** grep the *field name* (`_api`, `_module`) across the whole
plugin, not the type name.

**The period is the one accessor that is not `NoInlining`.** `CurrentPeriod()`
names no contract type itself: it checks `IsAvailable`, then calls the
`NoInlining` `ReadCurrentPeriod()`. So the tracker, the published API and every
event path call it without a guard of their own, and it answers `null` while core
is unavailable. Against a Ranks older than 1.1.0 the inner read throws
`MissingMethodException`, which surfaces in `CurrentPeriod()`'s frame: it logs one
warning and answers `0` from then on. Every row of such an install is on period 0,
so the module behaves exactly as 1.0.1 did. Checked against the 1.0.4 contract:
`GetTypes()` still loads `EloPointsModule` — its `OnPeriodChanged` and
`OnPlayerReset` overrides simply have no base slot to fill — and the handshake,
`OnCoreReady` and `PlayerDataLoaded` all run.

### 2.2 Where things go

| Step | Work |
|---|---|
| Constructor | nothing |
| `Load` | config, `Core.Registrator.Register(_tracker)`, core-event subscriptions |
| `UseSharedInterface` / `OnSharedInterfaceInjected` | acquire through the bridge; `RegisterMigrations(assembly)` |
| `OnCoreReady` | resolve both table names, read core's settings, run the `UseCoreRankingLogic` check |
| `OnPeriodChanged` | drop the rating cache, leave a match the boundary cut through unrated, re-seed every session (§8.2) |
| `OnPlayerReset` | queue the player's rating delete, re-seed their session (§8.3) |
| `OnAllPluginsLoaded` | if `Ranks` is absent, log one ungated warning and stay idle |
| `Unload` | discard any open match, then unwire |

`RegisterMigrations` goes in the handshake, not `Load` — there is no API in
`Load` (guide §10.5). Core dedupes on `(assembly, pluginId)`, so the handshake
re-running on every other plugin's load is harmless.

Table names are resolved in `OnCoreReady` for the same reason.

### 2.3 Handlers outside the plugin class must be registered

`MatchTracker` holds the `[GameEventHandler]` methods, so it is invisible to the
framework until:

```csharp
Core.Registrator.Register(_tracker);   // exactly once
```

Registering twice double-counts every round. Signatures must match the expected
delegate exactly or the method is silently skipped.

### 2.3a ⚠️ No native access inside an event dispatch

**Never touch native entity accessors synchronously from inside a game-event
handler, even in `Post` mode.** CS2 can be mid entity-shuffle, and a managed
`try/catch` does **not** catch the resulting access violation — it bypasses the
CLR straight to the crash reporter, taking the host down.

So `OnRoundEnd` reads only `e.Winner` and defers; `OnWinPanelMatch` defers
outright:

```csharp
public HookResult OnRoundEnd(EventRoundEnd e)
{
    var winner = (int)e.Winner;
    _core.Scheduler.NextTick(() => AccrueRound(winner));
    return HookResult.Continue;
}
```

`OnPlayerTeam` is the deliberate exception: its only native read is the named
player's own SteamID, whose pointer is stable for the event that names them, and
deferring risks them being gone by the next tick.

Two further rules follow from the same hazard:

- Guard with `player.IsValid` **and** `player.Controller is { IsValid: true }`.
  A non-null controller wrapper can sit over a dead pointer, and reading a field
  through it faults.
- The match-end watchdog uses `DelayAndRepeatBySeconds(5, 5, …)`, not
  `RepeatBySeconds`. The latter fires its first callback on the *next* scheduler
  tick, which lands inside CS2's map-load shuffle — and that callback reads game
  state.

### 2.4 The `UseCoreRankingLogic` check

```csharp
protected override void OnCoreReady()
{
    if (_bridge.GetSettings() is { UseCoreRankingLogic: true })
        _logger.LogWarning(
            "Ranks_EloPoints: core's Rank.UseCoreRankingLogic is true, so core is still awarding " +
            "points per kill while this module writes a rating over the top. Neither number is " +
            "meaningful. Set Rank.UseCoreRankingLogic to false in core's config.jsonc.");
}
```

Ungated by `DetailedLogging` — it fires once, and it is the only signal between
a misconfigured server and a ladder full of nonsense. It **warns rather than
overriding**: core's config belongs to the operator, and silently flipping it
would be the worse surprise. Same call `Ranks_WeaponStats` makes on its FFA
mismatch.

### 2.5 Hot reload

`Load(hotReload: true)` runs mid-match with players connected. **Any match in
progress is abandoned, not adopted** — the per-round accrual it would need was
never recorded, and reconstructing it from the scoreline would mean inventing
the per-round expectations. A fresh match session starts at the next round, and
the partial one pays nobody.

That is the same rule as a map change (features.md §2.1), reached a different
way.

---

## 3. Configuration

```csharp
Core.Configuration
    .InitializeJsonWithModel<EloConfig>("config.jsonc", "EloPoints", true)
    .Configure(builder =>
        builder.AddJsonFile(Core.Configuration.GetConfigPath("config.jsonc"), optional: false, reloadOnChange: true));

_config = BindConfig();

_reload = ChangeToken.OnChange(Core.Configuration.Manager.GetReloadToken, () =>
{
    var incoming = BindConfig();

    _config = incoming;
    _tracker?.UpdateConfig(incoming);
});
```

Bound off `Core.Configuration.Manager` — the root the framework already owns and
watches. No `ServiceCollection`, no `IOptionsMonitor` (guide §3).

Validation clamps rather than throwing:

| Key | Rule |
|---|---|
| `Rating.Mode` | `Classic` or `RoundAccumulation`; anything else falls back to `RoundAccumulation` with a warning |
| `Rating.K` | `1..200` |
| `Rating.PerformanceBonus` | `0..100`; `0` disables the bonus |
| `Rating.ConfidenceRounds` | `0..100`; `0` disables shrinkage |
| `Rating.ExpectedRounds` | `1..200` |
| `Abandon.Penalty` | `>= 0`; `0` disables |

**`Mode` falls back loudly.** An unrecognised string binding to
`default(RatingMode)` would silently pick whichever member is zero, and the
operator's typo would look like working software.

### 3.1 Settings read from core, not configured here

`StartPoints` and `MinPoints` come from `GetPointSettings()` on every match settle,
never cached at load — `GetPointSettings` returns a fresh snapshot per call precisely
so it tracks live reloads (FR-API-05). `MinPlayers`, `WarmupPoints` and `BotsEnabled`
reach the rating through core's play gate instead (§5.2), which core applies to its
statistics in every mode; the snapshot copies are read only against a Ranks older than
1.0.4, which has no gate to ask.

This is FR-MOD-05 rules 3 and 7: the operator states each intent once, in core, and the
rating moves on exactly the play core's statistics count.

---

## 4. Database

### 4.1 Migrations

```csharp
[Migration(202610261200)]
public sealed class M202610261200_Rating : Migration
{
    public override void Up()
    {
        if (!Schema.Table("ranks_elopoints").Exists())
            Create.Table("ranks_elopoints")
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("rating").AsDecimal(10, 4).NotNullable()
                .WithColumn("matches").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("abandons").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("updated_at").AsDateTime().NotNullable();
    }

    public override void Down() { }   // the table holds player data
}

[Migration(202611060100)]
public sealed class M202611060100_Periods : Migration
{
    public override void Up()
    {
        if (!Schema.Table("ranks_elopoints").Column("period").Exists())
            Alter.Table("ranks_elopoints").AddColumn("period").AsInt64().NotNullable().WithDefaultValue(0);

        if (!Schema.Table("ranks_elopoints_history").Exists())
            Create.Table("ranks_elopoints_history")
                .WithColumn("steam_id").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("period").AsInt64().NotNullable().PrimaryKey()
                .WithColumn("rating").AsDecimal(10, 4).NotNullable()
                .WithColumn("matches").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("abandons").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("updated_at").AsDateTime().NotNullable();

        if (!Schema.Table("ranks_elopoints_history").Index("ix_ranks_elopoints_history_period").Exists())
            Create.Index("ix_ranks_elopoints_history_period").OnTable("ranks_elopoints_history")
                .OnColumn("period").Ascending();
    }

    public override void Down() { /* drops the index only */ }
}
```

The same four rules as WeaponStats §4.1 apply: the base name carries the
`ranks_` namespace so FR-DB-03's resolver only adds the operator's affixes; the
PK is declared inline because MySQL names every primary key `PRIMARY` and a
`Constraint(...).Exists()` guard is therefore always false; migration ids are
unique within this assembly only, since the module has its own ledger; and no
`Execute.Sql`, which has no expression tree for core to rewrite. Every object is
guarded on its own, so a half-applied run finishes on the next boot.

**The `period` column defaults to 0**, `RanksPeriods.Initial` — core's current
period on every install that never enabled periods — so existing ratings stay
current and nothing changes until periods are switched on. `ranks_elopoints`
keeps `steam_id` as its only key, as core keeps it on `ranks_players`: SQLite
cannot alter a primary key in place, so closed periods get a table of their own
rather than a wider key on this one. The history index serves the past-season
leaderboard, which filters on `period` alone and so cannot use the
`(steam_id, period)` key.

**`DECIMAL(10,4)`, not `FLOAT`.** Binary floating point makes the stored value
depend on accumulation order, so two servers writing the same sequence of deltas
can disagree in the last digits — and the value is then compared and rounded for
display. Four decimal places is more than the formula needs and costs nothing.

### 4.2 The upsert is absolute, not additive

The opposite of `Ranks_WeaponStats`, and for a reason worth stating: that module
holds session *deltas* with no read on connect, so additive is correct there.
This module reads the current rating, computes a new one, and writes it.

```sql
-- MySQL / MariaDB
INSERT INTO {t} (steam_id, period, rating, matches, abandons, updated_at)
VALUES (@steam_id, @period, @rating, 1, @abandons, @updated_at)
ON DUPLICATE KEY UPDATE
    rating = VALUES(rating),
    matches = matches + 1,
    abandons = abandons + VALUES(abandons),
    updated_at = VALUES(updated_at);

-- PostgreSQL / SQLite
INSERT INTO {t} (...) VALUES (...)
ON CONFLICT (steam_id) DO UPDATE SET
    rating = EXCLUDED.rating,
    matches = {t}.matches + 1,
    abandons = {t}.abandons + EXCLUDED.abandons,
    updated_at = EXCLUDED.updated_at;
```

`rating` is replaced; the two counters accumulate. Keep `VALUES()` on the MySQL
branch — 8.0.20 deprecates it for the `AS new` alias, which MariaDB does not
support at all. The update leaves `period` alone: by the time it runs, the only
row it can hit already holds the write's period.

**Every write carries the period it was settled in**, captured on the game thread
at settle time, and lands in that period however late the store gets to it. Per
write, inside the batch's transaction:

1. `SELECT period, rating FROM {t} WHERE steam_id = @steam_id`.
2. A row on an older period is closed first (§4.3).
3. A row on a **newer** period means this write is a retry that crossed a
   rollover: it goes to `{history}`'s row for its own period — the same upsert
   with `ON CONFLICT (steam_id, period)` — and the current row is left alone.
4. No row at all, but `{history}` already holds `(steam_id, @period)`: the
   period has closed for this player since the write was settled (a read moved
   the row on the way in), so the write goes to history as well. Without this
   check it would re-open a row on a closed period, which the next read would
   close again — dropping the write.
5. Otherwise the upsert above, into `{t}`.

Table names come from `ResolveTableName` at query-build time, never string
concatenation.

### 4.3 Reading the rating in

A player's rating is needed the moment they enter a match, which may be before
they have a row. Resolution order:

1. this module's table, for the **current period**, read once per player per
   session and cached on the session;
2. core's `GetPoints` — covers a server migrating from core scoring, where the
   accumulated points are the only starting signal there is;
3. `Rank.StartPoints`.

The period is captured on the game thread and passed in. A row on an older
period is that period's final rating: in one transaction it is copied to
`{history}` (`INSERT IGNORE … SELECT` on MySQL/MariaDB, `INSERT … SELECT … WHERE
… ON CONFLICT (steam_id, period) DO NOTHING` elsewhere — SQLite needs the `WHERE`
to parse it) and deleted from `{t}`, both guarded by `period = @stale`, and the
read answers null so the player seeds as a new one. A row on a newer period —
another server sharing the database crossed the boundary first — comes back as
it is, as core treats its own rows.

**The cache is keyed by period**: an entry is `(period, rating)`, and a lookup for
any other period misses. It is also cleared on `PeriodChanged`, so it never holds
more than one period's worth. A reset leaves an entry with **no** rating for its
period, which answers "none" without a query while the delete is still queued.

The read is off-thread and must not block a round boundary. A read still in
flight when a round ends leaves the session unresolved, and an unresolved player
is simply left out of the team average rather than averaged in at a guessed
value. That is deliberate rather than tolerated: a blocking read on the game
thread is a stall, and one round of a slightly narrower average is invisible
after shrinkage. An unresolved session is never *settled*, either — see §7.3.

**The continuation marshals back through `Core.Scheduler.NextTick` before it
touches anything.** The session, core's API and the session registry are all
game-thread state, and the `await` resumes on the thread pool — applying the
result inline would race the round handler and could publish `RatingResolved`
ahead of `Rating`. It also checks the session's `RatingGeneration`: a new period
or a reset re-seeds the session and bumps it, so a read begun before either is
discarded rather than landing the wiped rating on top of the new seed.

### 4.4 The queue, and failure handling

**Every database operation the store makes runs on one queue**, in the order the
game thread asked for it: settles, resets, purges and reads alike. That is the
whole of the ordering argument — a reset always lands after the settle before it
and before the settle after it, and a read can never close a player's row while a
write for that player is still in flight.

A failed write must not lose the rating. A failed batch stays at the head of the
queue's retry list and is applied again, ahead of the next change and in the
same transaction, never reordered — so a reset still lands between the two
settles it came between. It never merges back into a live session; those belong
to the game thread (§6).

Log at `Error` with the exception object. This is a data-loss path and is not
gated behind `DetailedLogging`.

---

## 5. The match state machine

### 5.1 Reading match state safely

```csharp
// GameRules only exists after world spawn; reading MatchData between maps, during
// bootup, or once the rules entity has been torn down throws — and some SwiftlyS2
// builds throw NullReferenceException rather than anything specific.
private bool TryReadMatch(out MatchSnapshot snapshot)
{
    try
    {
        ref var m = ref Core.Game.MatchData;
        snapshot = new MatchSnapshot(m.ActualRoundsPlayed, m.Phase, m.CTScoreTotal, m.TerroristScoreTotal);
        return true;
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "MatchData unavailable.");
        snapshot = default;
        return false;
    }
}
```

**The broad catch is not defensive padding — it is a production finding** from
`rampage-plugins-ServerReporter`, which reads the same struct on a heartbeat and
carries the identical comment. It matters more here than there: this module
reads at round end and map unload, which is precisely when the rules entity is
being torn down.

A failed read at a round boundary skips that round's accrual rather than
guessing. A failed read at settle time falls back to the session's own round
count.

### 5.2 Boundaries

| Boundary | Signal |
|---|---|
| match start | first live round after `Phase` leaves `GAMEPHASE_WARMUP_ROUND`, or `ActualRoundsPlayed` moving backwards |
| round end | `EventRoundEnd`, filtered by core's play gate |
| match end | `EventCsWinPanelMatch`, **or** `Phase == GAMEPHASE_MATCH_ENDED` |
| abandoned | `OnMapUnload`, or `Unload`, with a match still open |
| cut by a season | `PeriodChanged` with a round already accrued — the rest of the match is not rated (§8.2) |

Match end is deliberately double-sourced. The event is primary; the phase poll
is a backstop, and settling is idempotent per match so firing both is harmless.
The failure it guards is that *nothing is ever written for any match*, which is
the one outcome that must not be silent.

A round is rated only when core counted it (FR-PTS-03). `OnRoundEnd` asks core's
`IsPlayCounted` inside the same dispatch; core decides it once per tick, so the answer
is the one core used for its own round result, and it covers `Rank.MinPlayers`,
warmup and a team made up only of bots at once. A draw is skipped without asking, as
core skips it. Adding a gate of its own here would rate play core's statistics left
out, or leave unrated play they kept.

Against a Ranks older than 1.0.4 the call fails at JIT and the module falls back to its
own rules: warmup rounds are filtered on `Phase == GAMEPHASE_WARMUP_ROUND` and gated by
core's `Rank.WarmupPoints`, and settling applies the match-level `MinPlayers` and
no-rated-humans checks.

### 5.3 Restart detection

```csharp
// Source-agnostic: a knife-round plugin, an admin, or a match plugin all look
// identical from here, and none of them has to be hooked.
if (snapshot.RoundsPlayed < _lastRoundsPlayed) DiscardMatch("restart");

_lastRoundsPlayed = snapshot.RoundsPlayed;
```

> **Confirmed on a live server (2026-10-26): `ActualRoundsPlayed` resets on
> `mp_restartgame`.** The branch above is the whole of restart detection, and a
> knife round is discarded rather than counted.

Still not covered: a knife round followed by `mp_swapteams` and a continue, with
no restart at all. That counts one phantom round, accepted deliberately — one
round in ~24, which shrinkage renders negligible.

`Nm` comes from `ActualRoundsPlayed` at settle time. Mid-match — for a
participation factor that needs a denominator before the match is over — it
comes from the `mp_maxrounds` convar, falling back to `Rating.ExpectedRounds`
only when the convar cannot be read.

---

## 6. Round accumulation

### 6.1 The session

```csharp
private readonly Dictionary<ulong, MatchSession> _sessions = new();
```

**A dictionary keyed on SteamID, not the 64-slot array WeaponStats uses.** The
slot array is faster, and it is wrong here: a session must survive a disconnect
so a reconnect resumes it (features.md §2.2), and the slot is cleared and
reissued on disconnect. The access rate justifies it — once per player per round
rather than once per bullet.

```csharp
internal sealed class MatchSession
{
    public required ulong SteamId { get; init; }
    public double Rating;            // resolved per §4.3, for the current period
    public bool RatingResolved;
    public int RatingGeneration;     // bumped on every re-seed, so a stale read is discarded
    public int RoundsPresent;
    public double SumActual;         // Σ S_r
    public double SumExpected;       // Σ E_r
    public PerfCounters? StartCounters;
    public int LastTeam;             // so a departed player can still be attributed at settle
    public bool Present = true;      // false once disconnected or moved to spectator
}
```

All of it is touched only on the game thread. A match boundary resets each
session in place rather than clearing the dictionary — see §6.3.

### 6.3 Resetting between matches

**Clearing `_sessions` at a match boundary is wrong, and it looks right.**
Nothing re-adds a session for an already-connected player: `Adopt` runs on
connect and on hot reload only. So wiping the dictionary after a knife-round
restart, or after any settle, leaves every connected player sessionless, and the
entire following match accrues nothing and settles nobody.

`StartFreshMatch()` instead walks the existing sessions and:

- drops the ones marked absent (they have just been settled or have left);
- zeroes `RoundsPresent`, `SumActual`, `SumExpected`, `LastTeam`;
- keeps `Rating` / `RatingResolved`, so no database re-read is needed;
- **re-captures `StartCounters`.**

That last point is a second bug in the same place. `StartCounters` is a *match*
baseline, and it is first taken on `PlayerDataLoaded` — i.e. at connect. Left
alone, a player's second match would be scored on counters spanning both, and
the performance percentile would be meaningless for everyone who stayed.

It deliberately does not touch `_settled`: `Settle` sets that flag and calls
`StartFreshMatch`, so resetting it there would let the 5-second backstop settle
the same match repeatedly. `_settled` is cleared by the first accrued round of
the next match instead.

The per-session half is `Rebaseline`, which a reset also runs for one player
(§8.3). A new period runs `StartFreshMatch` even between matches: core has just
zeroed every counter the baselines were taken from, and a baseline from before
would score the next match's play as negative (§8.2).

### 6.2 Per round

```csharp
[GameEventHandler(HookMode.Post)]
public HookResult OnRoundEnd(EventRoundEnd e)
{
    if (!TryReadMatch(out var m)) return HookResult.Continue;
    if (m.RoundsPlayed < _lastRoundsPlayed) { DiscardMatch("restart"); return HookResult.Continue; }
    _lastRoundsPlayed = m.RoundsPlayed;

    // Only the rounds core counted (FR-PTS-03). A draw is nobody's result.
    if (e.Winner is not ((int)Team.T or (int)Team.CT) || !_core.IsPlayCounted) return HookResult.Continue;

    var (avgT, avgCt) = TeamAverages();

    foreach (var player in Core.PlayerManager.GetAllPlayers())
    {
        if (_sessions.GetValueOrDefault(player.SteamID) is not { Present: true } s) continue;

        var own = player.Controller.TeamNum == (int)Team.T ? avgT : avgCt;
        var opp = player.Controller.TeamNum == (int)Team.T ? avgCt : avgT;

        s.SumExpected += EloMath.Expected(own, opp);
        s.SumActual += player.Controller.TeamNum == e.Winner ? 1.0 : 0.0;
        s.RoundsPresent++;
    }

    return HookResult.Continue;
}
```

**Team averages are recomputed every round**, which is what makes a team that
loses a player mid-match be measured against its current strength. It is a pass
over at most 10 players once per round.

A spectator has no session marked `Present`, so the loop skips them without a
team check.

### 6.3 Imputing bots and unrated players

```csharp
// Order matters: own team, then whole match, then the configured seed. Each
// fallback is only reached when the one above it has no rated player at all.
private double ImputedRating(int teamNum) =>
    MeanRatedOn(teamNum) ?? MeanRatedInMatch() ?? _core.StartPoints;
```

With core's `Rank.BotsEnabled` off, a round against a team made up only of bots is
never accrued, because core does not count it (features.md §3). Against a Ranks older
than 1.0.4, which cannot report that, a settle-time check stands in: a match where one
side had no rated humans is unrated. It runs at settle rather than per round because a
team can legitimately have no rated humans for a few rounds and then gain one.

---

## 7. Settling

### 7.1 Order of operations

Fixed, and each step depends on the one before it:

```
1.  base   = K * (S_adj - E_mean) * (n / Nm)
2.  base   = clamp(base, -K * E_mean, K * (1 - E_mean))     # envelope
3.  delta  = base + B * (percentile - 0.5)                  # bonus
4.  delta  = SignFloor(base, delta)                         # bonus cannot flip base's sign
5.  delta -= Abandon.Penalty                                # only if absent at match end
6.  rating = max(rating + delta, MinPoints)
```

Steps 2 and 4 are both required and neither subsumes the other — features.md
§1.5 has the worked case the envelope misses.

Step 5 is outside the sign floor on purpose: the penalty is not part of the
result, and it is allowed to turn a win into a net loss. That is the entire
point of it.

```csharp
private static double SignFloor(double base_, double delta) => base_ switch
{
    > 0 when delta < Epsilon  => Epsilon,
    < 0 when delta > -Epsilon => -Epsilon,
    _ => delta,
};
```

`Epsilon` is a constant, not a config key — it is a correctness guard, not a
tuning knob.

### 7.2 The performance percentile

```
raw = kills + 0.5*assists - 0.7*deaths + 0.3*roundWins + 2*bombsPlanted + 2*bombsDefused
```

…over the **match delta** of each counter, then ranked within the match and
mapped to `[0, 1]`.

Every counter comes from `GetSnapshot` diffed between match start and match end.
`GetStat` exposes the same numbers but they are lifetime totals, so a long-tenured
player would score higher on their history rather than on this match.

**Mid-match joiners are snapshotted on `PlayerDataLoaded`**, not at match start —
that event is the only moment their record is guaranteed resident. A player whose
start counters were never captured scores at the median (`p = 0.5`), contributing
no bonus either way, rather than being scored against a zero baseline that would
read as a perfect game.

Objectives are weighted in because a pure K/D measure pays players to stop
defusing (features.md §1.4). The coefficients are a starting point and are
expected to move once there is real data; they live in one `const` block for
that reason.

### 7.3 Writing

Settling runs on the game thread. Core is updated inline; the database write is
queued and leaves the thread:

```csharp
var period = _bridge.CurrentPeriod();             // captured here, travels with every write
// ...
_bridge.SetPoints(steamId, (int)Math.Round(updated), "elopoints.match");
_bridge.SavePlayer(steamId);
writes.Add(new RatingWrite(steamId, period, updated, abandoned));
// ...
_ = _store.CommitAsync(writes);     // off-thread from here on
StartFreshMatch();
```

`SetPoints` has to be inline because core's API is game-thread state; the DB
write must not be, because a synchronous write at match end is a stall.

**Only a session whose rating resolved is settled.** One that never resolved
holds either nothing — a read that did not land in a whole match — or, after a
re-seed, the rating a new period or a reset just wiped; settling it would write
`MinPoints + delta` or the wiped rating straight back. Such a player is left
unrated for the match, with one ungated warning, because a read that never lands
is a database problem worth seeing.

**The module's table is still the authoritative value** (features.md §5) even
though core is updated first. `CommitAsync` populates the store's cache before
it queues anything, so a player who had already disconnected — and whom core may
no longer track, making `SetPoints` a no-op — is reconciled on their next
`PlayerDataLoaded`, as long as the cached rating belongs to the period core has
just put them in:

```csharp
if (_bridge.CurrentPeriod() is { } period
    && _store.TryGetCached(player.SteamId, period, out var rating)
    && (int)Math.Round(rating) != player.Points)
    Api.SetPoints(player.SteamId, (int)Math.Round(rating), "elopoints.resync");
```

`SavePlayerAsync` is called explicitly rather than waiting for core's periodic
pass, per FR-MOD-05 rule 5 — the record is evicted once saved, and the next pass
may be minutes away.

**Rounding, never truncation.** `(int)rating` would bias every write downward and
the error would accumulate in one direction forever, which is the whole reason
the `DECIMAL` column exists.

---

## 8. Purge, periods and resets

### 8.1 Purge

```csharp
protected override void OnPlayersPurged(IReadOnlyList<ulong> steamIds) => _ = _store.PurgeAsync(steamIds);
```

Core has already removed its own rows by the time this fires, so the module must
not expect to look the player up. The purge deletes from **both** tables — a
purged player loses their history too, as in core (FR-DB-10) — and drops
anything of theirs still waiting in the retry list, which would otherwise
re-create a row for a player core no longer has.

### 8.2 A new period

Core raises `PeriodChanged` after it has moved `CurrentPeriod` and put every
resident player back on `StartPoints` (FR-API-13). The module's own rating would
undo that at the next match end, so `OnPeriodChanged`:

1. **clears the rating cache** — every entry belongs to the period that closed;
2. **leaves a match the boundary cut through unrated.** If any session has
   accrued a round, the match is discarded and marked void: later rounds of it
   accrue nothing, and the void clears when that match settles, restarts or the
   map changes. Its rounds were measured against last period's ratings, and what
   is left after the boundary is not a whole match;
3. **re-baselines every session's counters**, match or no match — core has just
   zeroed them;
4. **re-seeds every session**: `RatingResolved` goes false, `RatingGeneration` is
   bumped, and the rating is read again for the new period. That read closes the
   player's old row on the way in (§4.3), so an online player's final rating for
   the closed period reaches history at the boundary, and they start the new one
   from core's points — `StartPoints`, which core has just set.

Step 4 is what the discard alone does not do: `StartFreshMatch` keeps each
session's rating on purpose (§6.3), so without a re-seed the next match would
settle last period's rating into this one.

Core can raise `PeriodChanged` from inside its own `round_end` dispatch — the
rollover runs at round end — so the handler reads no native state (§2.3a). The
discard is logged at `Information` behind `DetailedLogging`, like every other
discard: a season boundary is expected behaviour, and core already announces it
to the server.

### 8.3 A reset

`!resetrank`, `!resetmyrank` and the Settings reset wipe the player's current
period in core, and core raises `PlayerReset` afterwards. `OnPlayerReset`:

1. **queues a delete of the player's rating for the current period** behind every
   write already queued (§4.4), from `ranks_elopoints` and — should the period
   have closed before the delete got its turn — from `ranks_elopoints_history`.
   Past periods stay;
2. **marks the cache "no rating" for that period**, so a read while the delete
   waits cannot find the old row;
3. **re-seeds the player's session in place** from core's points — 0 after a
   reset, so `StartPoints` — without a read, and drops the rounds it had accrued.

The session is re-seeded rather than removed because nothing re-adds a session for
a player who is still connected (§6.3): removing it would leave them unrated until
they reconnected. Its accrued rounds go because they were measured against the
rating the reset wiped; the player is rated on the rest of the match as if they had
joined at that moment, which participation and shrinkage already handle.

---

## 9. Rating invariants

Nothing asserts these at runtime. Every one of them is **silent when broken** —
the module keeps running and the numbers stay plausible — so check them by reading
whenever `EloMath` changes.

| Invariant | What breaking it looks like |
|---|---|
| `Expected(r, r) == 0.5`; a 400-point gap gives ≈ `0.9091`; `Expected(a,b) + Expected(b,a) == 1` | an inverted sign in the exponent, invisible on even teams |
| Shrinkage with `n = 0` yields `S_adj == E`, giving zero delta | a division that produces `NaN` on an empty session |
| Shrinkage is monotonic in `n` toward the unshrunk value | an inverted weighting that damps *long* sessions instead of short ones |
| 13-0 even teams, `c = 10` → `+14.13`; full 13-8 → `+4.03`; join-at-18 4-0 → `+1.36`; 2-round cameo → `+0.40` | coefficient drift away from the figures published in features.md |
| A double-counted accrual (`S̄ = 2.0`) still respects the envelope ceiling | the envelope clamp removed or mis-bounded |
| `E = 0.95`, win, worst percentile → delta is positive | the sign floor missing, which the envelope does not catch |
| Abandon penalty always reduces the delta by exactly `Penalty` | the penalty folded inside the sign floor |
| Four identical performances all land on the median | ties not sharing a rank, ordering identical play arbitrarily |

> **The envelope invariant only shows up on corrupted input.** With an actual
> score in `[0, 1]`, `K(S_adj − E)` already lands inside the envelope
> algebraically, so the clamp is unreachable on valid data. It exists to bound an
> upstream accrual bug — a round credited twice. Deleting it passed a 20 000
> iteration randomised sweep, which is why reading beats sampling here.

---

## 10. Open items

| Item | Blocking? | Note |
|---|---|---|
| `EventCsWinPanelMatch` firing on every match configuration | no | phase backstop covers it (§5.2) |
| Performance coefficients (§7.2) | no | starting values; expected to move on real data |
| Placement matches | no | deliberately out of scope (features.md §6) |
| `Abandon.Penalty` does not deter abandonment in the first third of a lopsided match | no | bounded and documented (features.md §2.3); closing it needs `Penalty ≈ K × 0.9` |
| A hot reload of this module adopts nobody | no | `Load` calls `Adopt` before the handshake, so `IsAvailable` is still false and every connected player stays sessionless — unrated — until they reconnect. Predates periods; §2.5 describes the intent |
| A hot reload of core leaves this module deaf to core's events | no | `Attach` keeps the first `EloPointsModule` (`_module ??=`), still subscribed to the unloaded core's cleared hub, so `PeriodChanged`, `PlayerReset`, `PlayerDataLoaded` and `PlayersPurged` stop arriving until this module reloads too. Every official module binds this way |
