# Ranks — Plugin Development Guidelines

> Copilot instructions for **Ranks**, the official SwiftlyS2 point-based
> progression and statistics plugin for Counter-Strike 2.

## Scope

This file governs **this repository only**: the `Ranks` core plugin, the
`Ranks.Contract` assembly, and the official module plugins that consume it
(WeaponStats, EloPoints, CustomCombos, Prestige).

The functional specification lives in [`docs/functional-requirements.md`](../docs/functional-requirements.md).
**That document is the authority on *what* to build; this one covers *how* to
build it on SwiftlyS2.** When the two disagree about behaviour, the
requirements doc wins — fix this file rather than diverging.

Framework rules below are general SwiftlyS2 facts. Anything specific to one
module belongs in that module's own `.github/copilot-instructions.md`, and must
never restate the framework rules here — a second copy goes stale and then
silently overrides the real guide.

## Code comments

**A method body contains no comments.** Everything a reader needs comes from the
method name, the structure of the code, and the XML doc above it. If a body
needs a comment to be understood, the body is wrong — not under-commented.

### Make the code carry it

| Instead of | Do this |
|---|---|
| A comment labelling a block | Extract that block into a named method |
| A comment explaining what a value holds | Name the value |
| A comment explaining a condition | Name the condition — `if (IsEligibleForAward(e))` |
| Nested `if`s with a comment per branch | Guard clauses, early return |
| A comment marking sections of a long method | Split the method |

The test is mechanical: **if you are about to write a comment inside a method,
extract instead.** The comment you wanted becomes the extracted method's name or
its `<summary>`, which is both closer to the thing and visible at every call
site.

### The doc comment above it

```csharp
/// <summary>
/// Awards <paramref name="eventKey"/> to the player, applying gates and core's
/// multipliers. No-op when core ranking logic is disabled.
/// </summary>
/// <remarks>
/// Must run on the game thread — reads <c>CCSPlayerController</c> to resolve the team.
/// </remarks>
private void Award(ulong steamId, string eventKey, int count = 1)
```

- `<summary>` — 1–2 lines. What it does, and why it exists if that is not
  obvious. Never a paragraph; a doc comment that needs paragraphs is a design
  note filed in the wrong place.
- `<param>` / `<returns>` — only when the name does not already say it.
  `<param name="count">The count.</param>` is noise.
- `<remarks>` — **the one escape hatch**, below.

### The one escape hatch

Some facts cannot be expressed by renaming anything, because they are not in the
code at all:

- framework behaviour (`Handled` is a no-op in a post hook; `GetConnection`
  falls back to the default on an unknown name)
- engine behaviour (a networked write needs its `*Updated()` notifier)
- cross-plugin constraints (Dommel's resolver is process-wide)
- a chosen value (why *this* interval, cap or bound)
- a deliberate omission (why something expected is **absent** — the highest-value
  kind, because absence cannot be read)

These go in **`<remarks>` above the method, never inline.** One or two lines,
stating the fact and its consequence.

If such a fact genuinely belongs to one statement buried in a method, that is
the signal to extract that statement into its own method so the fact can live in
*its* summary. The rule holds all the way down.

### Never

- A comment restating the next line.
- Narrating the change — `// changed this to fix X`. That is a commit message.
- Re-deriving rationale that already lives in `functional-requirements.md` or
  `implementation-guide.md`. Cite the requirement id instead: `<remarks>FR-DB-06.</remarks>`
- Commented-out code.
- `TODO` without a tracking issue.

> **The code samples in `docs/` are deliberately annotated and do not follow
> this rule.** They are teaching material, where the inline comment *is* the
> point being made. Production code under `src/` follows the rule above.

---

## Table of Contents

Numbering is stable across the split, so a `§8` reference in the docs or in a code
comment still resolves. Gaps are sections that moved.

**Here, always loaded:**

1. [Plugin Structure & Base Variables](#1-plugin-structure--base-variables)
4. [Logging](#4-logging)
7. [Events, Listeners & Hooks](#7-events-listeners--hooks)
9. [Thread Safety & Async Data Access](#9-thread-safety--async-data-access)
10. [Data Management & Optimization](#10-data-management--optimization)
12. [Player Validation](#12-player-validation)
13. [Plugin Lifecycle](#13-plugin-lifecycle)
14. [Auditing & Bug Prevention](#14-auditing--bug-prevention) — **the invariants table**
15. [SwiftlyS2 Docs & CS2 Schema Dumps via @sd MCP](#15-swiftlys2-docs--cs2-schema-dumps-via-sd-mcp)

**In `.github/instructions/`, loaded only when you touch the matching files.** Read
the file directly when you need the rules without having one of those files open:

| § | Topic | File | Loads on |
|---|-------|------|----------|
| 2 | Configuration | `configuration.instructions.md` | `**/Config/*.cs`, `**/*.jsonc` |
| 3 | Database & migrations | `database.instructions.md` | `**/{Database,Migrations}/*.cs` |
| 5 | Commands | `commands.instructions.md` | `**/Commands/*.cs` |
| 6 | Translations | `translations.instructions.md` | `**/translations/*.jsonc` |
| 8 | Shared interfaces & the bridge | `shared-interfaces.instructions.md` | `**/Api/*.cs` |
| 11 | Menus & UI | `menus.instructions.md` | `**/Menus/*.cs` |
| 16 | Build & release | `release.instructions.md` | `.github/workflows/*.yml` |
| — | CHANGELOG style | `changelog.instructions.md` | `CHANGELOG.md` |
| — | README style | `readme.instructions.md` | `**/README.md` |

**Not instructions, and not loaded — reference material to open on demand:**

| Document | What it answers |
|----------|-----------------|
| [`docs/functional-requirements.md`](../docs/functional-requirements.md) | What the plugin must do, as numbered `FR-*` requirements, plus the open questions and the decisions behind them |
| [`docs/implementation-guide.md`](../docs/implementation-guide.md) | Why the architecture is shaped this way, the verified API surface, and the release checklist |
| `Modules/<name>/docs/` | Per-module features and implementation notes |
| `Ranks.wiki/` | Operator-facing docs; a separate repo, so changes there need their own commit |

Reach for `docs/` when changing behaviour, not when changing code: a `FR-*` id in a
comment or a commit is the link back to the requirement that made the code that way.

---

## 1. Plugin Structure & Base Variables

### Plugin Class Declaration

```csharp
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;

[PluginMetadata(
    Id = "Ranks",
    Version = "1.0.0",
    Name = "Ranks",
    Author = "Shmitzas",
    Description = "Point-based progression and statistics system."
)]
public partial class Ranks : BasePlugin
{
    // Static so partial classes and services can reach it without threading it through.
    public static new ISwiftlyCore Core { get; private set; } = null!;

    private ILogger<Ranks> logger = null!;
    private IOptionsMonitor<Config> cfg = null!;

    public Ranks(ISwiftlyCore core) : base(core) { }
}
```

### ⚠️ Keep the Constructor Empty

`base.Core` is **not** safely usable inside the `BasePlugin` constructor.
SwiftlyS2's `PluginManager` activates the type via
`Activator.CreateInstance(pluginType, core)` **before** wiring the DI, logger
and config pipeline. Touching `Core.Configuration`, `Core.Scheduler`,
`Core.PluginDataDirectory`, `Core.PlayerManager` or `Core.Database` in the ctor
NREs with:

```
TargetInvocationException -> NullReferenceException
  at Ranks.LoadConfiguration() in Ranks.cs:LINE
  at Ranks..ctor(ISwiftlyCore core)
  at PluginManager.InstantiatePlugin(...)
```

The log only says `Failed to load plugin: (swRoot)/plugins/Ranks/Ranks.dll`, so
the cause is not obvious from the symptom.

```csharp
public Ranks(ISwiftlyCore core) : base(core)
{
    // Empty. Nothing here can safely touch Core.
}

public override void Load(bool hotReload)
{
    Core = base.Core;
    LoadConfiguration();     // config files, DI container
    InitializeDatabase();    // schema creation / migration
    RegisterCoreEvents();    // populate the event registry (see §8)
    RegisterListeners();
    RegisterCommands();
}
```

**Rule of thumb:** if it touches `Core.*`, DI or config, it belongs in
`Load(bool hotReload)`.

### `Core.Engine.GlobalVars` throws during `Load()`

`EngineService.GlobalVars` is `ref CGlobalVars` and throws
`InvalidOperationException("GlobalVars is null.")` when the engine is not yet
up. `Core.Engine` itself is already non-null, so an `if (Core.Engine is not { }
engine)` guard does **not** protect you. The throw is fatal — it aborts plugin
loading.

Any `GlobalVars` read reachable from `Load()` must be inside `NextTick` / a
scheduler callback / an event handler, and wrapped in try/catch if the first
tick can still be too early.

### Key `Core` Sub-Services

| Service | Purpose |
|---------|---------|
| `Core.PlayerManager` | `GetAllPlayers()`, `GetAllValidPlayers()`, `SendChat()` |
| `Core.Command` | `RegisterCommand()`, `RegisterCommandAlias()`, `UnregisterCommand()` |
| `Core.GameEvent` | `HookPre<T>()`, `HookPost<T>()`, `FireToPlayerAsync<T>()` |
| `Core.Event` | Framework lifecycle: `OnClientPutInServer`, `OnMapLoad`, `OnTick`, … |
| `Core.Scheduler` | `NextTick()`, `NextWorldUpdate()`, `DelayAndRepeatBySeconds()` |
| `Core.Configuration` | `InitializeJsonWithModel<T>()`, `GetConfigPath()` |
| `Core.Database` | `GetConnection(name)`, `GetConnectionInfo(name)` |
| `Core.Localizer` | Translations — see §6 |
| `Core.Menu` | `CreateMenu(id)` — see §11. (`Core.MenusAPI` is the `[Obsolete]` classic API) |
| `Core.Permission` | `PlayerHasPermissions(steamId, perms)` |
| `Core.Registrator` | `Register(instance)` — attribute discovery for non-plugin classes |
| `Core.CSGODirectory` | Path to the `csgo/` directory |

### Project File

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="SwiftlyS2.CS2" Version="1.4.13"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.0"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="10.0.0"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.0"
                      ExcludeAssets="runtime" PrivateAssets="all" />

    <!-- Migrations ship with the plugin; the host does not provide them. -->
    <PackageReference Include="FluentMigrator" Version="8.*" />
    <PackageReference Include="FluentMigrator.Runner" Version="8.*" />
    <PackageReference Include="FluentMigrator.Runner.MySql" Version="8.*" />
    <PackageReference Include="FluentMigrator.Runner.Postgres" Version="8.*" />
    <PackageReference Include="FluentMigrator.Runner.SQLite" Version="8.*" />
  </ItemGroup>
</Project>
```

> `SwiftlyS2.CS2` and every `Microsoft.Extensions.*` package must carry
> `ExcludeAssets="runtime" PrivateAssets="all"` — the host already provides them,
> and shipping a second copy produces duplicate type identities. The
> FluentMigrator packages are the opposite case: nothing provides them, so they
> must ship normally — beside the DLL, never in `resources/exports`, and without
> `PrivateAssets` (which silently drops them from publish). Modules ship their own
> copy too; why, and how core still runs their migrations, is in
> `instructions/database.instructions.md`.

---

## 4. Logging

### DetailedLogging

All verbose logging is gated behind `General.DetailedLogging` in
`config.jsonc`, default **`false`**:

| Level | Gated by `DetailedLogging`? | When |
|-------|---------------------------|------|
| `LogInformation` | **yes** | Flow tracing, data values |
| `LogDebug` | **yes** | Detailed internal state |
| `LogWarning` | **no** | Unexpected but recoverable |
| `LogError` | **no** | Failures, exceptions |
| thrown exceptions | **no** | Never swallowed to honour the flag |

**Read the flag from `CurrentValue` at the call site.** It lives in a
hot-reloaded file, so a cached copy pins the value at construction and the
symptom is "I turned logging on and nothing happened".

```csharp
// Gated — the check short-circuits before any string work
if (cfg.CurrentValue.General.DetailedLogging)
    logger.LogInformation("Awarding {Points} for {EventKey} to {SteamId}", points, eventKey, steamId);

// Never gated, and always pass the exception object
logger.LogError(ex, "Failed to load record for {SteamId}", steamId);
logger.LogWarning("Core ranking logic is disabled; no module is writing points.");

// Structured placeholders, not interpolation
logger.LogInformation($"Awarding {points}");   // BAD — builds the string regardless
```

`DetailedLogging` decides whether *successful, expected* flow is narrated. It
never downgrades or silences a failure.

### Level choices specific to this plugin

- **`UseCoreRankingLogic = false`** logs one ungated `Warning` at
  `OnAllPluginsLoaded`. Core has no registry and therefore cannot tell whether a
  rating module is present, so the warning is unconditional and says so.
- A **scoring decision** (gate rejected, multiplier applied) is `Debug`.
- A **recoverable persistence failure** is `Warning`; anything that risks data
  loss is `Error`.
- Anything on the **award path** is gated behind `DetailedLogging` without
  exception — it runs at kill rate, and an ungated line there is a log flood.

---

## 7. Events, Listeners & Hooks

Five distinct mechanisms. Pick deliberately.

> **Game events are a deprecated Source 2 feature and many of them do not fire.**
> The SwiftlyS2 documentation says so outright. Since nearly all of this
> plugin's scoring hangs off them, confirm an event actually fires on a live
> server before building a points value on it — a silent non-firing event looks
> exactly like a scoring bug. Entity hooks (`OnEntityTakeDamage`) and
> `IGameEventAccessor` raw field access are the fallbacks.
>
> Hook callback event objects are **temporary and valid only during the
> callback**. Copy anything needed later; never capture the event object.

### A. `[GameEventHandler]` — declarative CS2 game events

Unregistered automatically on unload. **Discovery runs per registered instance,
not per assembly:** the `BasePlugin` class is registered for you, and any other
class holding handlers needs `Core.Registrator.Register(instance)` in `Load`.
Register each instance once — a second call duplicates every callback on it.
Visibility does not matter; the signature must match the delegate exactly or the
method is silently skipped.

```csharp
[GameEventHandler(HookMode.Post)]
public HookResult OnPlayerDeath(EventPlayerDeath @event)
{
    var victim = @event.Accessor.GetPlayer("userid");
    var attacker = @event.Accessor.GetPlayer("attacker");
    if (victim is not { IsValid: true }) return HookResult.Continue;
    // ...
    return HookResult.Continue;
}
```

### B. `Core.GameEvent.HookPre/HookPost` — imperative

Manual registration, unregistered automatically on unload. Use when registration
is conditional.

```csharp
Core.GameEvent.HookPost<EventPlayerHurt>(OnPlayerHurt);
Core.GameEvent.HookPre<EventPlayerDeath>(OnPlayerDeathPre);
```

> **These are not idempotent.** Register them in `Load()`, never in
> `UseSharedInterface` / `OnSharedInterfaceInjected` — see §8.

### C. `Core.Event` — framework lifecycle (manual `+=` / `-=`)

The framework cleans listeners up on unload and hot reload, so a missing `-=` is
a convention violation to fix, **not** a proven leak. Pair them anyway:

```csharp
private void RegisterListeners()
{
    Core.Event.OnClientPutInServer += OnClientPutInServer;
    Core.Event.OnClientDisconnected += OnClientDisconnected;
    Core.Event.OnMapLoad += OnMapLoad;
}

private void UnregisterListeners()
{
    Core.Event.OnClientPutInServer -= OnClientPutInServer;
    Core.Event.OnClientDisconnected -= OnClientDisconnected;
    Core.Event.OnMapLoad -= OnMapLoad;
}

public override void Unload() => UnregisterListeners();
```

Common members: `OnClientConnected`, `OnClientPutInServer`,
`OnClientDisconnected`, `OnMapLoad`, `OnMapUnload`, `OnStartupServer`,
`OnEntityCreated`, `OnEntityDeleted`, `OnEntityTakeDamage`,
`OnConVarValueChanged`, `OnTick`, `OnWorldUpdate`.

This list **drifts** — the authoritative set is
`@sd get_document("swiftlys2/docs-api-events-ieventsubscriber.md")`, with
delegate signatures under `EventDelegates`. `OnTick` and `OnWorldUpdate` take no
parameter and are hot paths (`OnWorldUpdate` runs during hibernation too) — keep
them allocation-free.

### D. `[EventListener<T>]` — declarative framework events

```csharp
[EventListener<EventDelegates.OnClientSteamAuthorize>]
public void OnClientSteamAuthorize(IOnClientSteamAuthorizeEvent @event) { }
```

### E. Special hook attributes

```csharp
[ClientChatHookHandler]
public HookResult OnClientChat(int playerId, string message, bool teamonly) => HookResult.Continue;

[ClientCommandHookHandler]
public HookResult OnClientCommand(int playerId, string commandLine) => HookResult.Continue;
```

### HookResult — `Handled` does **not** block the original

The most common misreading in the framework. Returning `Handled` when you meant
to cancel lets the original run anyway, silently.

| Value | Following hooks | Original function |
|-------|-----------------|-------------------|
| `Continue` | run | runs |
| `Handled` | cancelled | **runs** |
| `CancelOriginal` | run | cancelled |
| `Stop` | cancelled | cancelled |

`Handled` and `Stop` **do nothing in post hooks** — the original has already
run. To block something, hook `Pre` and return `Stop`, or `CancelOriginal` if
other plugins' hooks should still see it.

### Which to use

| Mechanism | Auto-unregister | Use when |
|-----------|----------------|----------|
| `[GameEventHandler]` | yes | Standard CS2 game events |
| `Core.GameEvent.HookPre/Post` | yes | Conditional/dynamic registration |
| `Core.Event +=/-=` | yes (but `-=` by convention) | Framework lifecycle, imperative |
| `[EventListener<T>]` | yes | Framework lifecycle, declarative (preferred) |

---

## 9. Thread Safety & Async Data Access

### The rule

**Game state — players, entities, pawns, controllers — is only safe to touch
from the main thread.** Background work must marshal back before reading any of
it.

`Core.IsGameThread` answers "am I on it right now", which is worth asserting at
the top of any helper that both hot paths and background continuations can
reach — the failure mode otherwise is a rare corruption with no stack pointing
at the caller.

### `Task.Run` + `Core.Scheduler.NextTick`

```csharp
var steamId = player.SteamID;        // capture primitives BEFORE the hop

Task.Run(async () =>
{
    try
    {
        var record = await LoadRecordAsync(steamId);

        Core.Scheduler.NextTick(() =>
        {
            // Re-resolve and re-validate — the player may have left.
            var p = Core.PlayerManager.GetAllPlayers()
                .FirstOrDefault(x => x.SteamID == steamId);
            if (p?.Controller is not { IsValid: true }) return;
            _cache[steamId] = record;
        });
    }
    catch (Exception ex) { logger.LogError(ex, "Load failed for {SteamId}", steamId); }
});
```

Never capture an `IPlayer` across the marshal — capture the SteamID and
re-resolve.

### `NextWorldUpdate` — entity/pawn modification

```csharp
Core.Scheduler.NextWorldUpdate(() =>
{
    var pawn = player.PlayerPawn;
    if (pawn is null) return;
    // ...
});
```

### `NextWorldUpdateAsync` — main-thread data from an async context

```csharp
var players = await Core.Scheduler.NextWorldUpdateAsync(() =>
    Core.PlayerManager.GetAllValidPlayers()
        .Where(p => !p.IsFakeClient)
        .Select(p => p.SteamID)
        .ToList());

foreach (var steamId in players)
    await SaveRecordAsync(steamId);
```

### ⚠️ `Repeat*` fires the first callback immediately

```csharp
Core.Scheduler.DelayBySeconds(5, () => { });                      // once, after 5s
Core.Scheduler.RepeatBySeconds(1.0f, () => { });                  // NOW, then every 1s
Core.Scheduler.DelayAndRepeatBySeconds(300, 300, SavePass);       // after 300s, then every 300s

Core.Scheduler.StopOnMapChange(timer);
timer?.Cancel();
```

Nothing at the call site hints at this. A save pass, a purge, or anything that
walks entities almost always wants the **delayed** form — `RepeatBySeconds`
would run it during `Load()`, mid map-load, on every single restart.

---

## 10. Data Management & Optimization

### Concurrent collections for shared state

```csharp
private readonly ConcurrentDictionary<ulong, PlayerRecord> _records = new();
```

A plain `Dictionary` touched from both a save task and the game thread will
corrupt — and the symptom (`IndexOutOfRangeException` inside `TryInsert`, or a
`KeyNotFoundException` for a key that was definitely added) does not look like a
threading bug.

### Slot-indexed lookup on hot paths

Kill, hurt and damage events fire many times per second. Resolve per-player
state from a **slot-indexed array holding the record reference directly** —
measured 2.8–3.3 ns/op, against 13.2 for a `ConcurrentDictionary` and 17.7–19.1
for a `ConditionalWeakTable`.

> **Do not reach for `ConditionalWeakTable<IPlayer, T>`** even though SwiftlyS2's
> own `Fixes` plugin uses it. It is the slowest option measured, and its
> `IPlayer` key cannot find a departed record on reconnect — which is what
> FR-DB-07 requires — so the SteamID dictionary has to exist anyway. Rationale
> and numbers: requirements doc §12, design note.

Keep the SteamID-keyed dictionary as the authority for anything off the hot
path, and for records whose player has disconnected but not yet been saved. The
slot array must be **cleared on disconnect**, or it serves a record the
dictionary has already released.

### Save queue + saved-value snapshot

Writes are absolute (requirements doc FR-DB-06), so there is no dirty-flag
bookkeeping at mutation sites — a record diffs against the values its last
*committed* write sent.

```csharp
private readonly HashSet<ulong> _saveQueue = new();      // idempotent by construction
private readonly object _saveQueueLock = new();

internal void EnqueueSave(ulong steamId)
{
    lock (_saveQueueLock) { _saveQueue.Add(steamId); }
}

private void StartSaveProcessor(int intervalSeconds)
{
    // Delayed form: Repeat* would flush an empty queue during Load().
    _saveCts = Core.Scheduler.DelayAndRepeatBySeconds(intervalSeconds, intervalSeconds,
        () => Task.Run(SavePassAsync));
}
```

### Disconnect handling

A departed record stays resident until it is fully saved, and a reconnect adopts
it. The disconnect handler must **not** gate on `IsValid` — the pawn is already
gone by then (§12).

```csharp
private void OnClientDisconnected(ulong steamId)
{
    EnqueueSave(steamId);     // eviction happens after the save confirms, not here
}
```

### Reflection caching

```csharp
private static readonly ConcurrentDictionary<Type, PropertyInfo?> _propertyCache = new();
var prop = _propertyCache.GetOrAdd(typeof(T), static t => t.GetProperty("Accessor"));
```

---

## 12. Player Validation

### `IsValid` is not "not null" — it requires a pawn

The API doc for `IPlayer.IsValid` reads: *"Checks if the player is valid (has
controller, is not HLTV, is connected **and has pawn**)."* That last clause
decides which predicate you need, and getting it wrong fails **silently** — the
handler simply never runs.

| Context | Predicate |
|---|---|
| Spawn, death, damage, weapons, any pawn work | `player is { IsValid: true }` |
| Connect/join, disconnect, chat, menus, map load | `player?.Controller is { IsValid: true }` |

A player who is connecting, sitting in team-select, spectating between rounds,
or disconnecting has a controller but **no pawn**. Gating a join handler on
`IsValid` is why a plugin "does nothing on first connect" — and gating a
*disconnect* handler on it is why records silently fail to save.

```csharp
internal static bool IsPlayable(IPlayer? p)  => p is { IsValid: true, IsFakeClient: false };
internal static bool IsConnected(IPlayer? p) => p?.Controller is { IsValid: true } && !p.IsFakeClient;
```

At join time the real SteamID is not available until Steam has authorized the
client — `IPlayer.UnauthorizedSteamID` holds the provisional value until then,
and a record keyed on the wrong id is a record lost. So there are two distinct
needs, and conflating them produces a polling loop:

- **"When should I load?"** → subscribe to the **`OnClientSteamAuthorize`** core
  event, with `OnClientSteamAuthorizeFail` as its paired failure signal. Never
  poll `IsAuthorized` on a timer or a tick.
- **"Is this SteamID safe to read right now?"** → check `IPlayer.IsAuthorized`
  at the point of use.

> The property is `IPlayer.Name`, **not** `PlayerName`. Several SwiftlyS2 guide
> pages still show `player.PlayerName`; the API reference does not, and it does
> not compile.

### Common patterns

```csharp
// Pawn-having non-bots. GetAllValidPlayers() already applies IsValid.
Core.PlayerManager.GetAllValidPlayers().Where(p => !p.IsFakeClient).ToList();

// Everyone connected, pawn or not — announcements, menus, disconnect cleanup.
Core.PlayerManager.GetAllPlayers().Where(IsConnected).ToList();

// Hot reload — adopt already-connected players.
if (hotReload)
{
    foreach (var p in Core.PlayerManager.GetAllPlayers().Where(IsConnected))
    {
        var steamId = p.SteamID;             // capture BEFORE the hop
        Task.Run(() => LoadRecordAsync(steamId));
    }
}
```

Bots are excluded from scoring by `Rank.BotsEnabled`, but note that a bot has
no meaningful SteamID — never create a record for one.

---

## 13. Plugin Lifecycle

### Verified order

```
Constructor  →  Load(hotReload)  →  ConfigureSharedInterface()
             →  UseSharedInterface()  →  OnSharedInterfaceInjected()
             →  OnAllPluginsLoaded()  →  [runtime]  →  Unload()
```

**`Load()` runs BEFORE `ConfigureSharedInterface()`.** Source:
`PluginManager.LoadPluginInternal` calls `plugin.Load(hotReload)`;
`LoadPlugins()` then calls `RebuildSharedServices()` followed by
`NotifyAllPluginsLoaded()`.

This matters: it is what makes "build the API implementation in `Load`, register
the already-built instance in `ConfigureSharedInterface`" correct. Several
community docs state the reverse — don't trust that claim if it resurfaces.

`RebuildSharedServices()` re-runs the three handshake callbacks for every loaded
plugin on every load **and** unload, so `ConfigureSharedInterface` →
`OnSharedInterfaceInjected` fire many times per session while `Load` and
`OnAllPluginsLoaded` fire once per load cycle.

### Do NOT null-check shared-interface fields in `Load`

On the hot-reload path, `Load` runs before the framework has finished re-wiring
shared-interface fields, so every field reads null even when the provider is
loaded and healthy. `OnAllPluginsLoaded` is the only place where that state is
authoritative.

### Full skeleton

```csharp
[PluginMetadata(Id = "Ranks", Version = "1.0.0", Name = "Ranks",
    Author = "Shmitzas", Description = "Point-based progression and statistics system.")]
public partial class Ranks : BasePlugin
{
    public static new ISwiftlyCore Core { get; private set; } = null!;
    private ILogger<Ranks> logger = null!;
    private IOptionsMonitor<Config> cfg = null!;
    private RanksApi _api = null!;
    private CancellationTokenSource? _saveCts;

    public Ranks(ISwiftlyCore core) : base(core) { }   // empty

    public override void Load(bool hotReload)
    {
        Core = base.Core;
        LoadConfiguration();
        InitializeDatabase();
        _api = new RanksApi(/* services */);
        RegisterCoreEvents();                 // populate the event registry
        RegisterListeners();
        RegisterCommands();

        _saveCts = Core.Scheduler.DelayAndRepeatBySeconds(
            cfg.CurrentValue.Database.SaveIntervalSeconds,
            cfg.CurrentValue.Database.SaveIntervalSeconds,
            () => Task.Run(SavePassAsync));

        if (hotReload)
            foreach (var p in Core.PlayerManager.GetAllPlayers().Where(IsConnected))
            {
                var steamId = p.SteamID;
                Task.Run(() => LoadRecordAsync(steamId));
            }
    }

    public override void ConfigureSharedInterface(IInterfaceManager im)
        => im.AddSharedInterface<IRanksApi, RanksApi>("Ranks.Api.v1", _api);

    public override void OnAllPluginsLoaded()
    {
        if (cfg.CurrentValue.General.DetailedLogging)
            logger.LogInformation("Ranks ready on connection {Connection}.", _api.ConnectionName);

        if (!cfg.CurrentValue.Rank.UseCoreRankingLogic)
            logger.LogWarning("Core ranking logic is disabled — a rating module must write points.");
    }

    public override void Unload()
    {
        UnregisterListeners();
        _saveCts?.Cancel();
        FlushPendingSaves();     // blocking, bounded budget (~8s)
    }
}
```

`Unload` must flush. An absolute-write model with an unflushed queue loses
whatever has accumulated since the last pass.

---

## 14. Auditing & Bug Prevention

### Silent invariants

These rules used to be asserted by load-time self-checks. The checks are gone —
they were pure, so they could only ever report what the author's own first run
already showed, while a wrong assertion would stop the plugin loading on every
server at once. The rules themselves did not go away, and **none of them fails
visibly**: breaking one produces a plausible-looking result, not an error. Verify
them by reading when you touch the logic, and state in your report which ones the
change could have affected.

| Area | Invariant | Why it is silent |
|---|---|---|
| `TableNames` | The plugin supplies the separator, not the operator. Empty affixes change nothing. A module ledger drops the `ranks` prefix. Overlong identifiers fold deterministically | A wrong affix writes to a real table that is simply the wrong one, and a ledger collision reports migrations as applied that never ran |
| `AwardPipeline.IsPlayCounted` | A gate stops points and statistics together, in core and in every module. Every counter write sits behind it — directly, or through `ShouldRecordDeath` for deaths and hits — and it alone decides whether play counts: `MinPlayers` met, no warmup `WarmupPoints` excludes, and with `BotsEnabled` off no team made up only of bots. Decided once per server tick, so a kill's stat and its award, and every module's handler for the same event, see one answer. No module adds a gate of its own: WeaponStats counts and CustomCombos claims only while it is open, and EloPoints rates only rounds it counted — discarding a whole match on a restart, a map change or a season boundary withholds a rating, it does not gate play. A `round_end` naming neither T nor CT is nobody's win or loss | A counter written outside it records play the points refuse, which looks fine on any full server. Shipped that way: `MinPlayers` gated only awards, so deaths, kills and round results moved on an empty server, every drawn round cost both teams a loss, and EloPoints rated matches under a `MinPlayers` rule of its own |
| Scoring anchors | Equal players score the configured value; the difference is bounded and symmetric; the ladder picks the highest threshold met | A broken anchor makes every configured `kill` value mean something other than what it says |
| `ScoreboardService.EngineRankType` | Every visible mode maps to a **confirmed** engine rank type. `0` is the engine's "no rank" | CS2 renders nothing for an unrecognised `m_iCompetitiveRankType` and reports no error, so it looks like the feature is switched off. It shipped broken once, writing `(byte)mode` |
| `ScoreboardService.ResolveMode` | An unparseable or undefined mode falls back to the configured mode, never to `Off`. `Enum.TryParse` accepts numbers, so `Enum.IsDefined` is required | A module typo would otherwise blank every scoreboard on the server |
| `CustomIcon` | At the shipped defaults, ladder position 1 lands on icon 0 and saturates past 18; a margin larger than the position must clamp at 0 | The engine treats the value as an array index |
| `EloMath` | `Expected(r,r) == 0.5` and `Expected(a,b) + Expected(b,a) == 1`; zero evidence moves nothing; shrinkage is monotonic in `n`; the envelope clamp holds on a **corrupted** accrual; the sign floor cannot turn a win into a loss; the penalty is a flat deduction; ties share a percentile rank | A sign error is invisible on even teams, a missing clamp only shows on a lopsided match, and a wrong shrinkage weight just moves the ladder at the wrong speed. The clamp is unreachable on valid data — a 20 000-iteration random sweep passed with it deleted |
| `PrestigeLadder` | Thresholds resolve to the highest level reached; crossing one resets the display to zero; operator order and duplicates do not change the outcome | A wrong level silently shows the wrong number to the player |
| `ComboMatcher` | Closest match wins, ties go to config order, `RequiresNone` excludes, a count combo needs the whole run and keeps accumulating after a loss, weapon class accepts both spellings | A combo that never fires looks like the operator configured it wrong |
| Weapon names | Both event spellings land on one row; non-weapon sources keep their bare name; unusable values become `unknown` | Stats split silently across two rows for the same gun |
| `MenuEntryRegistry` | A module's entries die with it, and re-registering the same `(moduleId, Section, Label)` replaces rather than appends | The handshake re-runs on every other plugin's load, so an append shows the row once per reload; an entry left behind is a delegate into an unloaded assembly load context, which crashes only when someone opens that menu |
| Menu nesting | Every parent-child link is a `SubmenuComponent`, never `ButtonComponent` + `Open()` | `Open()` starts a new root with no back-stack. The menu looks right until the player presses back and the whole tree closes |
| `PlayerRecord.ClearProgress` | Zeroes **every** counter, including `Headshots`, `Hits` and `Misses`, and is the only code that does — the reset and the period rollover both go through it | A forgotten column leaves a "reset" player carrying their old accuracy, which only shows up on a stat nobody checks first. The command's copy already had this bug before the reset paths were merged |
| Period rollover (FR-DB-10) | A resident record's closed-period figures are written to `ranks_players_history` in the **same savepoint** as its fresh row. `PendingArchives` is replaced, never mutated, and cleared only by compare-and-swap. Period ids only rise: a rollover needs a grid period *later* than the active one, and boot resumes the newest stored period when the grid answers an earlier one. Only a stored period **older** than the active one is closed; a newer one makes the server catch up. Nothing guesses the period: if it cannot be read at boot, nothing is recorded | Every failure here looks like a quiet season: a lost archive is a past leaderboard missing a player, a plain clear in `Settle` (which runs off the game thread) drops a period a rollover queued mid-write, and a smaller id after a `VirtualPurgeDays` change makes every older-or-newer comparison — core's and EloPoints' — archive the live season backwards. None of them errors |
| Module period stamping | A module stamps counts it was still holding with `PeriodChanged`'s `OldPeriod`, never `CurrentPeriod`, which has already moved; a delta's period is fixed when its batch is cut and a retried write keeps it; every database write a module makes — flush, reset, purge — runs through one ordered queue; a module holding an authoritative per-player value drops it on `PeriodChanged` and `PlayerReset` | Stamping with the new period moves the last minutes of a season into the next; a reset that bypasses the queue is overtaken by a flush cut before it and the cleared figures come back; a kept rating is written straight back over core's reset at the next match end, which is the `!resetrank` bug EloPoints shipped with |
| Renderer menu ids | `ranks.top`, `weaponstats.weapons` and `customcombos.*` may be reused by every period variant — a renderer reads their figures off the rows. `ranks.stats.main` and `ranks.top.player` stay the current period's, and `weaponstats.hitgroups` / `weaponstats.weaponhitgroups` stay all-time, because a renderer fetches those figures itself | CustomHUD draws its own numbers over a page it recognises by id, so a reused id shows this season's or all-time figures under a past season's title — on a page that looks perfectly designed |
| Accuracy | `Hits / (Hits + Misses)`, never `Hits / Shots` | One shotgun blast is a single trigger pull that lands several hits, so a shots-based figure quietly exceeds 100% |
| `PagedCollectionComponent<TItem>` | `TItem` is the **row type**, and the text comes from `Formatter` | The default formatter is `ToString()`, so passing `IMenuComponent` items compiles, runs, pages correctly — and draws `SwiftlyS2.Shared.Menu.Components.TextComponent` on every line. Shipped that way in the leaderboard and all three module menus |
| Menu row width | A row is one centred line and clips at roughly **42 characters** | Both ends are cut off with no ellipsis, so a too-long row looks like corrupted text rather than a layout problem. Sentences go to chat |
| `IMenu.Parent` | A menu opened from a **row handler** must have `Parent` set by hand | Adoption only happens for a menu a `SubmenuComponent` opened. From `OnItemSelected` the child opens fine and looks right — the break only shows when the player presses back and the whole tree closes |
| Bridge calls from event paths | Anything reachable from a `[GameEventHandler]`, a `Registrator` registration or a scheduler callback must check `_bridge.IsAvailable` before touching the bridge | `IsAvailable` is a plain bool and resolves nothing; every other bridge member names a contract type, so with no Ranks installed the CLR fails to JIT it and throws into the *caller's* frame — a game-event or `NextTick` body, where nothing catches it. WeaponStats avoids this structurally by keeping its collector contract-free — it reaches core's play gate only through a delegate whose target checks `IsAvailable` first; a module whose tracker calls the bridge needs the guard instead |
| Module idle warning | Report absence from `OnAllPluginsLoaded`, never from the `UseSharedInterface` / `OnSharedInterfaceInjected` path | That handshake re-runs on every plugin load and unload, so warning there repeats once per rebuild rather than once per load cycle |
| Menu handler threading | A handler that calls a **thread-unsafe** API (`SendChat`, scoreboard/controller writes) goes through `RankMenus.OnGameThread` | A handler is not guaranteed to run on the game thread. The classic menu API dispatched every click on the thread pool and took a production server down that way; `Core.IsGameThread` + `Scheduler.NextTick` settles it without needing to know which. Framework calls like `IMenu.Open` are *not* in this class and are left alone |

### Where checking belongs instead

- **Operator input is a different category and still gets validated at runtime.**
  `ConfigValidator` stays: numbers clamp and warn, but table affixes **throw**
  against `^[A-Za-z0-9_]*$` because an identifier cannot be parameterised and is
  concatenated into DDL. That is a SQL-injection boundary, not a self-check.
- Do **not** reintroduce assertion classes, `.selfcheck` harness projects, or a
  test project. If a rule above is genuinely load-bearing and cheap, document it
  here and keep the code shaped so it reads correctly.

### How to run an audit

Every item below is an **enumeration, not an affirmation**. "All `Core.Event +=`
have a matching `-=`" is a property you satisfy by glancing at `Unload()`,
seeing a couple of `-=` lines, and feeling right. "Print every `+=` site next to
the line number of its `-=`" cannot be passed by feel: a missing one is an empty
cell in a table you have to show.

**Build the list, check each row, show the list.** If you did not enumerate, you
did not audit — report the item as unchecked rather than ticking it.

Three ordering rules that matter as much as the items:

- **Inventory before symptoms.** When the request arrives with bug reports
  attached, build item 1 *first* and read the reports *after*. Symptoms anchor
  attention; chasing two named bugs turns the rest of the plugin into a drive-by
  read, which is how a first pass misses what a second pass finds.
- **The call site lies about timing.** `RepeatBySeconds(3600, Purge)` reads as
  "hourly" and means "now, then hourly". Nothing local hints at it. Timing,
  re-entrancy and lifecycle semantics come from `@sd`, never from the shape of
  the call.
- **Some bugs are not in this plugin.** Command aliases, shared-interface keys
  and config filenames live in a namespace shared with every other plugin on the
  server. They cannot be validated from inside one folder.

### Checklist

1. **Surface inventory — build this first; everything below walks it**
   - [ ] Every command and alias (`[Command]`, `[CommandAlias]`, **and**
         config-driven `RegisterCommandAlias`)
   - [ ] Every menu and every selectable row
   - [ ] Every game-event hook and `Core.Event` handler
   - [ ] Every timer / repeating scheduler task
   - [ ] Every registered core event key and its default points
   - [ ] Public API surface published, and anything consumed
   - [ ] Every config key, with default and clamp
   - [ ] Every table, column and index

2. **Framework-semantics audit** — the call site is not evidence
   - [ ] Every scheduler call looked up in `@sd`. `Repeat*` fires the first
         callback **immediately**; `DelayAndRepeat*` is the one that waits
   - [ ] Nothing non-idempotent registered in the shared-interface handshake
   - [ ] `IsValid` semantics confirmed per use — it requires a pawn, so it is
         wrong in join, disconnect, chat and menu handlers
   - [ ] Every `HookResult` checked against the table in §7 — `Handled` does not
         block the original
   - [ ] Return values of framework calls that can fail are actually read

3. **Registration / teardown audit** — produce the table
   - [ ] Table: every class holding a `[Command]`, `[GameEventHandler]`,
         `[EventListener<T>]` or any other listener attribute → the
         `Core.Registrator.Register` line that registers it. Only the
         `BasePlugin` class is exempt. An unregistered handler never fires and
         never errors, so an empty cell here is a dead feature
   - [ ] Each of those instances registered **exactly once** — a repeat call
         duplicates every callback on it, and the handshake re-runs per rebuild
   - [ ] Table: every `Core.Event +=` → line number of its `-=` in `Unload()`
   - [ ] Table: every timer/CTS → where it is cancelled
   - [ ] Every hook registered exactly once per load cycle
   - [ ] Records evicted from caches only after a confirmed save

4. **Thread-safety audit**
   - [ ] List every `Task.Run` / async continuation; confirm none dereferences
         `IPlayer`, `PlayerPawn`, controller or entity
   - [ ] Primitives captured BEFORE the hop (SteamID, slot, name)
   - [ ] Re-resolved and re-validated after `NextTick` / `NextWorldUpdate`
   - [ ] `ConcurrentDictionary` for shared state, not `Dictionary`

5. **Database audit**
   - [ ] Engine dispatch keys on `Driver`, not on the connection's CLR type
   - [ ] Every upserted table has the unique/PK its upsert depends on
   - [ ] Migrations verified on MySQL, PostgreSQL **and** SQLite, fresh and
         existing databases — not assumed from one engine passing
   - [ ] Every `IfDatabase(...)` string checked against a real
         `DatabaseTypeAliases` list — `"Postgres"` silently matches nothing
   - [ ] Every migration guards each object individually, not the migration as a
         whole — and `Create.PrimaryKey` is exempt from that guard
   - [ ] The version ledger, and every index and constraint name, carries the
         configured prefix/suffix
   - [ ] Each plugin migrates against **its own** ledger; nothing shares one
   - [ ] Resolved ledger names stay under 63 characters — PostgreSQL truncates
         silently, which can merge two modules' ledgers
   - [ ] Any Dommel table-name resolver filters on `type.Assembly` and is
         installed before the first query
   - [ ] No `Down()` drops a table holding player data
   - [ ] No identifier named `rank`
   - [ ] Connections and migration-runner providers disposed
   - [ ] Every table name routed through the affix resolver

6. **Dependency-direction audit** — specific to this plugin
   - [ ] No core type holds a delegate, interface or descriptor a module supplied
   - [ ] No core config key exists because a module is installed
   - [ ] Core's behaviour with zero modules present is identical to its
         behaviour with ten — if it is not, something inverted
   - [ ] `UseCoreRankingLogic = false` mutes **everything** downstream of a point
         value: handlers, gates, both multipliers, dynamic scaling
   - [ ] … and mutes none of: stat counters, ladder, scoreboard, commands,
         persistence, `AddPoints`, `SetPoints`
   - [ ] `GetPointSettings` returns live values in **both** modes, and has no
         setter
   - [ ] `AddPoints` is stored raw in both modes — no multiplier ever touches it

7. **Cross-plugin collision audit** — cannot be done from inside the folder
   - [ ] Every command name and alias greppable against sibling plugins
   - [ ] Shared-interface key and config filenames likewise
   - [ ] Short aliases (`top`, `stats`) are the usual offenders; first
         registration wins silently and the loser never fires

8. **Divergent-path audit**
   - [ ] Where two paths perform the same operation (chat command vs shared API,
         menu vs command), diff their validation and limits — the second path is
         where the cap, the permission check or the confirmation goes missing

9. **Failure-handling audit**
   - [ ] A failed load never substitutes an empty record — an absolute save
         would then wipe the real row
   - [ ] Every user-visible failure produces a localized message, not silence
   - [ ] User-controlled text validated before it reaches a format string or a
         colour-token pass

10. **Logging audit**
    - [ ] All `LogInformation`/`LogDebug` gated by `DetailedLogging`
    - [ ] `LogWarning`/`LogError` never gated
    - [ ] Structured placeholders, not interpolation
    - [ ] `LogError(ex, …)` passes the exception object
    - [ ] Nothing logs from a binder that re-runs per rebuild

11. **Translation audit** — mechanical, so do it mechanically
    - [ ] Extract every key referenced in code, every key in `en.jsonc`, every
          key in each other file; print the counts and the diffs
    - [ ] Every file parses as JSON
    - [ ] No hardcoded user-facing English in `SendChat` / catch blocks
    - [ ] Colour runs closed with `[default]`

12. **Performance audit**
    - [ ] No synchronous DB calls on the main thread
    - [ ] `OnTick` / `OnWorldUpdate` handlers allocation-free — no LINQ, no DB
          (and for this plugin: no subscription to either at all, NFR-11)
    - [ ] Hot-path player lookup is slot-indexed, not SteamID-hashed
    - [ ] Reflection results cached in a static `ConcurrentDictionary`
    - [ ] Save passes skip unchanged records
    - [ ] Config read from the swapped snapshot, not `CurrentValue` per access

13. **Comment audit** — mechanical, so do it mechanically
    - [ ] Grep `src/` for `//` and `/*` **inside method bodies**; the expected
          count is zero. Each hit is either extracted into a named method or
          promoted to `<remarks>` on the enclosing one
    - [ ] Every non-private method has a `<summary>`, and no `<summary>` runs
          past two lines
    - [ ] No `<param>` / `<returns>` that only restates the identifier
    - [ ] No commented-out code, no `TODO` without a tracking issue

### Common bugs

```csharp
// BUG: accessing a player inside Task.Run
Task.Run(async () => { var name = player.Name; });              // thread-unsafe
// FIX: capture first
var name = player.Name; var steamId = player.SteamID;
Task.Run(async () => await SaveAsync(steamId, name));

// BUG: no re-validation after a scheduler callback
Core.Scheduler.NextTick(() => player.SendChat("Hi"));           // may have left
// FIX
Core.Scheduler.NextTick(() => {
    if (player?.Controller is not { IsValid: true }) return;
    player.SendChat("Hi");
});

// BUG: Repeat* runs the first callback immediately, during Load()
Core.Scheduler.RepeatBySeconds(300, SavePass);                  // flushes mid-load
// FIX
Core.Scheduler.DelayAndRepeatBySeconds(300, 300, SavePass);

// BUG: non-idempotent registration in the handshake
public override void UseSharedInterface(IInterfaceManager im)
    => Core.GameEvent.HookPost<EventPlayerSpawn>(OnSpawn);      // one per rebuild
// FIX: hooks go in Load(); only idempotent registration here.

// BUG: binder early-returns and keeps a stale reference
if (!im.HasSharedInterface("Ranks.Api.v1")) return;             // _api kept
_api = im.GetSharedInterface<IRanksApi>("Ranks.Api.v1");
// FIX: TryGetSharedInterface nulls the out param on a miss, so one assignment fails closed
_ = im.TryGetSharedInterface<IRanksApi>("Ranks.Api.v1", out var api);
_api = api;                                                     // absence -> OnAllPluginsLoaded

// BUG: disconnect handler gated on IsValid — the pawn is already gone
if (!player.IsValid) return;                                    // save never runs
// FIX
if (player?.Controller is not { IsValid: true }) return;

// BUG: engine dispatch on CLR type — SQLite never matches
case SqliteConnection: rb.AddSQLite(); break;
// FIX
case "sqlite": rb.AddSQLite(); break;                           // from GetConnectionInfo().Driver

// BUG: a failed load creates an empty record, and the next absolute save wipes the row
var record = await LoadAsync(steamId) ?? new PlayerRecord(steamId);
// FIX: do not track the player; retry the load.
```

---

## 15. SwiftlyS2 Docs & CS2 Schema Dumps via @sd MCP

### Categories

| Category | Contents |
|----------|----------|
| `swiftlys2` | SwiftlyS2 API docs (classes, interfaces, methods, events, guides) |
| `counter-strike-2` | CS2 schema + binary string dumps (sub-category `CS2-Dumps`) |
| `source2` | FGD dumps, Source 2 docs, tooltex and console dumps |

### Usage

```
@sd list_documentation_categories
@sd browse_category("swiftlys2")              ← top-level category name ONLY
@sd search_documentation("ICommandContext")   ← single keyword or type name
@sd get_document("swiftlys2/docs-api-plugins-baseplugin.md")
```

- `browse_category` takes a **top-level name only** — `"category/subcategory"`
  is invalid input.
- `search_documentation` is **exact substring matching**. Use a single keyword
  or type name. Long natural-language phrases return nothing.
- **Never guess a document path.** Always take it from `browse_category` or
  `search_documentation`.

### Common lookups

| Need | Query |
|------|-------|
| Plugin base class | `BasePlugin` |
| Command context | `ICommandContext` |
| Shared interfaces | `IInterfaceManager` |
| Database service | `IDatabaseService` |
| Scheduler | `ISchedulerService` |
| Menus | `IMenuAPI` |
| Player interface | `IPlayer` |
| Framework events | `IEventSubscriber` |
| Game events | the event name, e.g. `EventPlayerDeath` |
| CS2 entity schemas | browse `counter-strike-2` → `CS2-Dumps` |

---

## Quick Reference: Repository Structure

```
Ranks/
├── AGENTS.md                             ← entry point for tools that do not read .github/
├── .github/
│   ├── copilot-instructions.md          ← this file, always loaded
│   ├── instructions/                    ← loaded only when applyTo matches
│   │   ├── changelog.instructions.md        CHANGELOG.md
│   │   ├── commands.instructions.md         **/Commands/*.cs
│   │   ├── configuration.instructions.md    **/Config/*.cs, **/*.jsonc
│   │   ├── database.instructions.md         **/{Database,Migrations}/*.cs
│   │   ├── menus.instructions.md            **/Menus/*.cs
│   │   ├── readme.instructions.md           **/README.md
│   │   ├── release.instructions.md          .github/workflows/*.yml
│   │   ├── shared-interfaces.instructions.md **/Api/*.cs
│   │   └── translations.instructions.md     **/translations/*.jsonc
│   └── workflows/
│       └── release.yml                  ← build + GitHub release
├── docs/
│   ├── functional-requirements.md       ← the specification
│   ├── implementation-guide.md          ← architecture
│   ├── verify-release-workflow.sh       ← release-workflow harness
│   └── verify-wiki.ps1                  ← wiki checks (gitignored)
├── Ranks/                               ← core plugin
│   ├── Ranks.csproj
│   ├── src/
│   │   ├── Entrypoint.cs                ← plugin class, Load/Unload
│   │   ├── Api/                         ← IRanksApi implementation
│   │   ├── Commands/
│   │   ├── Config/                      ← config models + defaults
│   │   ├── Database/                    ← schema, migrations, store
│   │   ├── Logging/
│   │   ├── Menus/
│   │   ├── Players/                     ← record cache, load/save lifecycle, period rollover
│   │   ├── Ranking/                     ← ladder resolution
│   │   ├── Scoreboard/                  ← rank icon, clan tag, overlay, reveal-all
│   │   └── Scoring/                     ← award pipeline, gates, multipliers
│   └── resources/
│       ├── examples/ranks-competitive.jsonc
│       ├── translations/en.jsonc        ← REQUIRED
│       └── exports/                     ← Ranks.Contract.dll (build-generated)
├── Ranks.Contract/                      ← public API assembly
│   └── IRanksApi.cs, RanksTypes.cs, RanksKillTypes.cs, RanksModuleBase.cs
├── Modules/
│   ├── Ranks_WeaponStats/               ← data, publishes its own contract
│   ├── Ranks_EloPoints/                 ← absolute, publishes its own contract
│   ├── Ranks_CustomCombos/              ← additive, publishes its own contract
│   └── Ranks_Prestige/                  ← display, publishes its own contract
├── CHANGELOG.md
├── LICENSE
└── README.md
```
Each module is its own project with its own `src/Api/RanksBridge.cs`, config
models, `CHANGELOG.md` and `README.md`. The consumer-facing wiki lives in the
separate `Ranks.wiki` repository.
