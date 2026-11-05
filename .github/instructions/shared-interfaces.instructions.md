---
description: 'Shared interfaces and the Ranks module bridge: the handshake, JIT isolation, NoInlining, publishing and consuming an interface. Trigger phrases: shared interface, bridge, RanksBridge, contract, module API, handshake.'
applyTo: '**/Api/*.cs'
---

# Shared Interfaces

> Part of the Ranks instruction set. `§N` cross-references resolve through the table in [`copilot-instructions.md`](../copilot-instructions.md), which is always loaded; `docs/` references are to the separate requirements and implementation guides.

`Ranks` is primarily a **provider**. The contract is the product — treat a
breaking change to it as a breaking change to every module in the ecosystem.

> **The dependency runs one way.** Core never calls module code, never holds a
> module-supplied delegate, and never gains config keys because a module was
> installed. The contract therefore exposes **facts and operations only** — read
> a record, change points, persist, observe, read core's own settings, and read
> what its ladder and scoreboard display, so another plugin can draw the same
> page (FR-API-12). If a new method would have core invoking something a module
> supplied, it belongs on the module's side of the line instead. See the
> requirements doc, FR-PTS-01.

### Contract project

```csharp
// Ranks.Contract/IRanksApi.cs
namespace Ranks.Contract;

public interface IRanksApi
{
    void AddPoints(ulong steamId, int amount, string? reason = null);
    long GetStat(ulong steamId, string statKey);
    RanksPointSettings GetPointSettings();
    string ResolveTableName(string baseName);
    event Action<RanksPointsChangedArgs>? PointsChanged;
    // full surface: docs/functional-requirements.md §11
}
```

Interfaces, DTOs and enums only — no implementation, no framework dependencies
beyond `IPlayer`.

### Registering (provider side)

```csharp
public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
{
    interfaceManager.AddSharedInterface<IRanksApi, RanksApi>("Ranks.Api.v1", _api);
}
```

`Load()` runs **before** `ConfigureSharedInterface()`, so build the
implementation in `Load` and register the already-built instance here.

`AddSharedInterface` throws on a duplicate key, but the rebuild disposes the
manager (clearing the key set) before re-running registration, so
re-registration is safe.

### ⚠️ The handshake re-runs constantly

`PluginManager.RebuildSharedServices()` disposes and rebuilds the whole
interface manager, then re-runs `ConfigureSharedInterface` →
`UseSharedInterface` → `OnSharedInterfaceInjected` for **every loaded plugin**.
It fires on every plugin load **and every unload**.

Three consequences:

1. **Nothing non-idempotent goes in the handshake.** A `Core.GameEvent.HookPost`
   or a bare `+=` there accumulates one live registration per rebuild — on a
   busy server that is N duplicate handlers per event. Hooks belong in `Load()`.
   Idempotent registration (registering an event descriptor, a menu entry) is
   fine.

   > **Confirmed live in official code.** Several `SwiftlyS2-Plugins/VIPCore`
   > modules reset `_isFeatureRegistered = false` at the top of
   > `UseSharedInterface` and then re-run a `RegisterVipFeatures()` that ends
   > with `_vipApi.PlayerLoaded += …` / `OnPlayerSpawn += …`. The
   > `RegisterFeature` call itself is idempotent — core overwrites
   > `_features[key]` — but the `+=` lines are not, and they are only removed in
   > `Unload`. Do not copy that shape: subscribe in `Load`, or guard with a flag
   > that the handshake does **not** reset.
2. **Binders must be fail-closed**, never early-return — an early return leaves
   a stale reference pointing at a disposed provider.
3. **Never log from a binder.** It runs per rebuild and will spam. Report
   absence from `OnAllPluginsLoaded`, which runs once per load cycle and is the
   only place where field state is authoritative.

### Consuming (module side)

```csharp
private object? _ranksApi;            // NOT IRanksApi? — see below
private volatile bool _available;

public override void UseSharedInterface(IInterfaceManager im) => Acquire(im);
public override void OnSharedInterfaceInjected(IInterfaceManager im) => Acquire(im);

public override void OnAllPluginsLoaded()
{
    if (!_available)
        Core.Logger.LogError("Ranks API not found — this module is disabled.");
}
```

Bind in **both** `UseSharedInterface` and `OnSharedInterfaceInjected`: the first
covers providers already loaded, the second covers providers that load later.

### ⚠️ An absent contract DLL kills the entire host

For a publicly distributed plugin, **every** consumed interface is a soft
dependency. You cannot know what is installed on someone else's server, so there
is no "it's always there" shortcut.

The CLR resolves every type a method *names* when it JITs that method, not when
execution reaches the line. On a server without the provider:

- a `TryGetSharedInterface<IRanksApi>(...)` call site — **or merely a field
  typed `IRanksApi?`** — throws `FileNotFoundException` when its enclosing
  method is prepared;
- the throw surfaces at the **caller's** frame, so the method's own try/catch
  never sees it.

**Which callback it escapes from decides the blast radius**, and only one of the
two is survivable (verified in `PluginManager.cs`):

| Escapes from | Guarded by core? | Result |
|---|---|---|
| `Load` | `LoadPluginInternal` wraps it in `try`/`catch` → `CleanupFailedPlugin` + `FailWithError` | **Your plugin only** is marked `Error` |
| `ConfigureSharedInterface`, `UseSharedInterface`, `OnSharedInterfaceInjected` | **No.** `RebuildSharedServices` runs all three loops bare | Propagates through `LoadPlugins` → `Initialize` → `StartupService`'s constructor → `Hosting failed to start`, **every plugin on the box** |
| `OnAllPluginsLoaded` | **No.** `NotifyAllPluginsLoaded` is bare | Same as above |

Acquisition happens in `UseSharedInterface` / `OnSharedInterfaceInjected`, i.e.
in the unguarded half. That is why the `try`/`catch` has to be there and cannot
be deferred to `Load`.

`Try…` / `Has…` look defensive and are not. Isolate instead — nothing outside
the `[NoInlining]` method may name the contract type, and the `try` goes in the
**caller**, which is the frame the throw actually lands in:

```csharp
private void Acquire(IInterfaceManager im)   // names no contract type
{
    try { _bridge.TryAcquire(im); }
    catch (Exception ex) { logger.LogDebug(ex, "contract not resolvable"); }
}

[MethodImpl(MethodImplOptions.NoInlining)]
public bool TryAcquire(IInterfaceManager im)
{
    try
    {
        if (!im.TryGetSharedInterface<IRanksApi>("Ranks.Api.v1", out var api) || api is null)
        {
            _api = null; _available = false;
            return false;
        }

        _api = api; _available = true;
        Attach(api);
        return true;
    }
    catch (Exception ex)   // runtime faults inside Attach — never the JIT throw
    {
        logger.LogWarning(ex, "could not acquire the Ranks API");
        _api = null; _available = false;
        return false;
    }
}
```

`TryGetSharedInterface` subsumes the `Has…` probe: one call, no window between
check and fetch, `out` null on a miss, one place to fail closed. The caller's
catch is **unfiltered** on purpose — filtering to
`FileNotFoundException or TypeLoadException or BadImageFormatException` lets
anything unforeseen through, and this path's whole job is to never fail the host.

Every other method reads `_available` (a `bool` needs no contract resolution)
and routes typed calls through its own `[NoInlining]` helper.

**`Unload` is the classic miss.** `_api.PointsChanged -= handler` needs the
contract loaded to unwire, and its in-body try/catch cannot catch its own
JIT-time throw — give it the same isolated-helper treatment. Core catches it
there, so the cost is a failed unload and a plugin left `Indeterminate` rather
than a dead host. When auditing, grep the **field name** across the whole
plugin: the acquire path is the obvious site and never the only one. A stray
`if (_ranksApi != null)` in a summary log line throws just as readily, and the
table above says what it costs depending on where it runs.

### Referencing the contract

Modules reference `Ranks.Contract` with `<Private>false</Private>` and
`<ExcludeAssets>runtime</ExcludeAssets>`, and **never ship their own copy** —
two copies mean two Type identities for one interface, which breaks the
cross-plugin cast inside `TryGetSharedInterface<T>`.

The provider ships the contract DLL in `resources/exports/`.

**A contract you publish is different from one you consume, and must also ship
beside your own plugin DLL.** `PluginManager.LoadExports` runs exactly once, from
`Initialize`, and snapshots every `plugins/*/resources/exports/*.dll` into
`_sharedTypes`; `ReloadPluginByDllName` never re-runs it. So a module installed or
updated on an already-running server has no shared copy of its own contract — and
`FindPluginType` calls `Assembly.GetTypes()`, which resolves every TypeDef's
interfaces, so the `*Api : I*Api` class alone fails the **whole plugin** before
`Load` runs:

```
ReflectionTypeLoadException: Could not load file or assembly 'Ranks_EloPoints.Contract'
   at PluginManager.FindPluginType(PluginLoader loader)
```

No amount of `[NoInlining]` helps — `GetTypes()` walks type definitions, not
method bodies. Copy the contract to `$(OutDir)` / `$(PublishDir)` as well as to
`resources/exports`; the local copy is only the loader's base-path fallback for
that window, and because SwiftlyS2 sets `PreferSharedTypes = true` the shared copy
still wins whenever it exists, so this cannot split the identity. This applies
only to a contract the module itself publishes — never copy `Ranks.Contract`,
which core publishes and every module consumes.

> **Do not add `resources/exports` to a static `<Content Include>` glob.** Item
> globs are evaluated during project evaluation, *before* any target runs, so a
> build-generated contract DLL is not on disk yet. It works on a dev machine (a
> previous build left the file behind) and ships **nothing** from a clean CI
> checkout — producing a `ReflectionTypeLoadException` at runtime with no build
> failure. Copy it in from an explicit `<Copy>` target at `AfterTargets="Build"`
> and `AfterTargets="Publish"` instead.

### Versioning

- Key format `Ranks.Api.v1`. Breaking changes bump to `.v2`, and the old
  interface stays registered for at least one minor release.
- Additive changes (new method on a new interface, new optional DTO field) do
  not bump the key.
- **One interface, one key — do not split the surface per concern.** Unprefixed
  and **lowercase `v`** follow the stable *Shared API* guide
  (`PluginName.ServiceName.v1`), which `VIPCore` matches and `Admins` does not.
  Not splitting is a deliberate divergence from `Admins.Core`, which publishes
  five — the cost shows up in its consumers as five nested
  `try` / `HasSharedInterface` / `LogWarning` blocks per module, each needing
  the fail-closed binding right independently. See FR-API-01.

---

