---
description: 'Building and changing in-game menus with Core.Menu: builders, components, paging, dividers, submenus, keybind scopes. Trigger phrases: menu, submenu, menu row, paged menu, divider, menu component.'
applyTo: '**/Menus/*.cs'
---

# Menus & UI

> Part of the Ranks instruction set. `§N` cross-references resolve through the table in [`copilot-instructions.md`](../copilot-instructions.md), which is always loaded; `docs/` references are to the separate requirements and implementation guides.

### Use `Core.Menu`, not `Core.MenusAPI`

`Core.MenusAPI` (`IMenuManagerAPI`, `IMenuBuilderAPI`, `IMenuAPI`, `IMenuOption`)
is **`[Obsolete]` since 1.5.0** — *"the classic menu system is deprecated and will
be removed in future versions."* Every menu in this repo is built through
`Core.Menu` (`IMenuService`, `IMenuBuilder`, `IMenu`, `IMenuSession`), and new
code must be too (FR-CMD-06).

### Building

```csharp
_core.Menu.CreateMenu("ranks.root")          // id doubles as the keybind scope
    .WithTitle(_core.Localizer["menu.root.title"])
    .AddHeader(new DividerComponent())       // WithTitle draws no rule of its own
    .WithItemsPerPage(8)
    .Add(new TextComponent(_core.Localizer["menu.rank.XP", record.Points]))
    .Add(new SubmenuComponent(_core.Localizer["menu.root.stats"], _ => BuildStatistics(player)))
    .Build()
    .Open(player);
```

Ids are namespaced (`"ranks.root"`, `"ranks.stats.main"`) because a menu id is
also its keybind configuration scope — **a duplicate id means the server owner's
binds for one menu silently apply to the other.**

`WithTitle` only *"adds a title line to the header"*; the rule under it is a
`DividerComponent` you add yourself. Every menu here carries one at each end —
`AddHeader` after the title, `AddFooter` before `Build()` — which separates the
body from the title above and the key hints below.

Counting the two calls is **not** a way to check this. A builder with several
early returns — `PrestigeMenus.Build` is the one here — calls `AddHeader` once on
the shared builder and then `AddFooter` on each branch, so the file reads 1 header
against 3 footers and 3 builds while every menu it produces still renders exactly
one of each. Only one branch runs per call. Read the branches before believing a
count.

### Components

Don't hand-roll a widget out of `ButtonComponent`:

| Component | Use |
|---|---|
| `TextComponent` | non-interactive line |
| `ButtonComponent` | clickable action |
| `ToggleComponent` | on/off, with `ctx.NewValue` |
| `SliderComponent` | numeric range |
| `ChoiceComponent` / `SelectorComponent<T>` | cycle through values |
| `InputComponent` | chat input |
| `ProgressBarComponent` | progress, from a `Func` so it stays live |
| `SubmenuComponent` | opens another menu |
| `PagedCollectionComponent<T>` | async list — paging is built in |
| `DividerComponent`, `SpacerComponent`, `TitleComponent` | layout |

A preference menu uses `ToggleComponent`, not buttons that flip a flag.

### Nesting

**`SubmenuComponent`, never `ButtonComponent` + `Open()`.** `Open()` starts a new
root with no back-stack, so the player's back key has nothing to return to:

```csharp
.Add(new SubmenuComponent(label, _ => BuildChild(player)))   // lazy: rebuilt per open
```

The factory form is what you want for anything whose contents can change. A menu
with **no parent of its own is adopted by the entry that opened it** — the same
mechanism that lets a module's menu (FR-CMD-04) sit under core's Statistics and
still come back.

**Adoption only covers `SubmenuComponent`.** A menu opened from a row handler —
`PagedCollectionComponent.OnItemSelected`, a `ButtonComponent` — has to be told:

```csharp
OnItemSelected = ctx =>
{
    var child = BuildDetail(ctx.Player, ctx.Item);
    child.Parent = ctx.Session.Menu;   // without this, back closes the tree
    child.Open(ctx.Player);
}
```

### Loading data

The factory runs on the **game thread and must not block**. Anything that reads
the database goes inside `PagedCollectionComponent`, which owns its own thread
and already pages:

```csharp
.Add(new PagedCollectionComponent<RanksLeaderboardEntry>(async () =>
{
    try   { return await _api.GetTopAsync("points", 50).ConfigureAwait(false); }
    catch (Exception ex) { _logger.LogError(ex, "..."); throw; }   // rethrow -> FailedText
})
{
    Formatter  = e => _core.Localizer["menu.top.row", e.Position, e.Name, e.Value],
    EmptyText  = _core.Localizer["menu.top.empty"],
    FailedText = _core.Localizer["menu.top.error"],
})
```

**`TItem` is the row type, never `IMenuComponent`.** The component renders each
item through `Formatter`, which defaults to `ToString()` — so handing it
`TextComponent`s draws `SwiftlyS2.Shared.Menu.Components.TextComponent` on every
line. Both this repo's leaderboard and its module menus shipped that way once.

`EmptyText` and `FailedText` cover the no-rows and threw cases, so the loader
returns data only. Catch to **log and rethrow** — swallowing the exception shows
the empty message for a failure.

### Lifetime

- **One open menu per player, across every plugin.** Opening yours closes someone
  else's; that is normal, not an error to guard against.
- **Released automatically on unload** — each plugin's menus are attributed to it
  — so `Unload` needs no menu teardown.
- A **module's contributed entry is a delegate core holds**, not a menu core owns,
  so it needs explicit unregistration (`RanksModuleBase.Unregister` does it).

Menu colour markup is **HTML tags, never chat brackets** (§6). A row is one
centred line and clips at roughly **42 characters** — anything longer belongs in
chat.

### Server configuration is not ours

Key binds, renderer and input method are the **server owner's**, and they live in
`configs/menus/menus.jsonc` — **not** `core.jsonc`, and not in any Ranks config.
The `"core.menu"` block inside that file is the section every menu inherits; its
name has nothing to do with `core.jsonc`, which is the usual mix-up.

Resolution is highest-priority-first, and the server file wins outright:

| Priority | Source |
|---|---|
| `Global` (100) | `configs/menus/menus.jsonc` |
| `Plugin` (50) | `WithKeybindSource` / `WithInputMethodSource` / `WithRendererSource` |
| `CodeDefault` (0) | `WithInputMethod` / `WithRenderer` / the action's `DefaultKey` |

**This repo sets none of them**, deliberately — so nothing here can shadow an
operator's choice, and "the config is being ignored" is never a Ranks bug. Check
the file path first, then that the menu id matches, then that the menu uses the
`Buttons` input method (binds do not apply under `Chat`).

### Custom renderers

Menus stay **renderer-agnostic** — stock components, nothing aimed at one
renderer. A plugin that draws these pages its own way recognises a page by its
id and takes the figures from the API, so when it needs more than a page
carries, the answer is a read-only member on the owning plugin's API
(FR-API-12), **never a renderer-specific node**.

That makes menu ids, and a paged list's row type when it is a contract record
(`RanksLeaderboardEntry`, a module's `ComboStatRow`), something other plugins
match on. Renaming either breaks nothing here and silently costs the renderer
that page.

### Clan tag updates (scoreboard)

Three steps, all required — without the last two the scoreboard will not reflect
the change until the next natural refresh:

```csharp
using SwiftlyS2.Shared.GameEventDefinitions;

player.Controller.Clan = rankTag;
player.Controller.ClanUpdated();
if (Core.GameEvent.IsListeningToEvent<EventNextlevelChanged>(player.PlayerID))
    Core.GameEvent.FireToPlayerAsync<EventNextlevelChanged>(player.PlayerID);
```

Do **not** re-write the tag every tick. Other plugins write the same field with
no arbitration, so a tight loop produces flicker rather than ownership.

---

