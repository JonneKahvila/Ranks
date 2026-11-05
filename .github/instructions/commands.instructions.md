---
description: 'Registering and handling chat/console commands: names, aliases, permissions, help text, unregistration. Trigger phrases: command, alias, permission, register command, command handler.'
applyTo: '**/Commands/*.cs'
---

# Commands

> Part of the Ranks instruction set. `§N` cross-references resolve through the table in [`copilot-instructions.md`](../copilot-instructions.md), which is always loaded; `docs/` references are to the separate requirements and implementation guides.

### Pattern A: `Core.Command.RegisterCommand()` — how `Ranks` registers

FR-CMD-01 makes the command name, its permission, and whether it registers at
all operator-editable in `commands.jsonc`. **Attribute arguments are
compile-time constants, so `[Command]` cannot express any of that** — least of
all "an empty `Command` string means do not register". Registration is therefore
a loop over bound config in `Load`:

```csharp
foreach (var (key, info) in commands.CurrentValue.All)
{
    if (string.IsNullOrWhiteSpace(info.Command)) continue;   // operator removed it

    var guid = Core.Command.RegisterCommand(
        info.Command, handlers.For(key), registerRaw: true,
        permission: info.Permission,
        helpText: Core.Localizer[$"command.{key}.help"]);

    registered.Add(guid);

    foreach (var alias in info.Aliases)
        Core.Command.RegisterCommandAlias(info.Command, alias, registerRaw: true);
}
```

`RegisterCommand` returns a `Guid` for `UnregisterCommand`. Help text is
**mandatory** — one short sentence — and comes from `Core.Localizer`, not from
`commands.jsonc`, because it is player-facing (§6). `permission` is checked
before the handler runs, so an admin command needs no in-body check.
`registerRaw: true` keeps the console name equal to the configured one instead
of `sw_<name>`.

**Bind `commands.jsonc` with `CommandsConfig.Bind`, never `Get<CommandsConfig>()`.**
The binder appends a file's array onto a non-empty list default, so a plain bind
keeps every shipped alias registered beside the operator's replacement. Core and
every module with commands bind onto an empty dictionary and only then fill in
the keys the file lacks (FR-CMD-01).

### Pattern B: `[Command]` attribute — fixed names only

Correct for any command whose name can never change. **`Ranks` has none**, so
this pattern does not appear in core; a module with hardcoded names may use it.

```csharp
[Command("rank", HelpText = "Shows your current rank, points and progress.")]
[CommandAlias("myrank")]
public void OnRank(ICommandContext context) { }
```

> A `[Command]` outside the `BasePlugin` class needs
> `Core.Registrator.Register(instance)` in `Load`, exactly like
> `[GameEventHandler]` (§7). An unregistered command does not error — it just
> does not exist, and the player sees `Unknown command`.

### Handler — `ICommandContext`

Members are `Sender` (`IPlayer?`), `IsSentByPlayer`, `Prefix`, `IsSlient`
(framework spelling — not `IsSilent`), `CommandName`, `Args`, `Reply` and
`ReplyAsync`. `Sender` is `null` from the server console, so check
`IsSentByPlayer` before dereferencing it.

> **Whether `Args[0]` is the first argument or the command name is not stated
> anywhere in the framework docs.** Confirm it once on a live server and fix the
> indexing everywhere — guessing wrong shifts every argument by one, which
> parses as a usage error rather than a crash. `CommandName` is available
> separately either way, so nothing needs `Args` to carry it.

```csharp
private void OnGivePoints(ICommandContext context)
{
    if (!context.IsSentByPlayer) return;
    var args = context.Args;

    if (args.Length < 2)
    {
        context.Reply("Usage: !givepoints <target> <amount>");
        return;
    }

    if (!int.TryParse(args[1], out var amount))
    {
        context.Reply(Core.Localizer["error.invalid_amount"]);
        return;
    }
}
```

### Permission naming

Format: `<pluginid>.<action>` — lowercase, dot-separated, `snake_case` for
multi-word actions. Each plugin uses **its own id** as the scope, so a module
namespaces under itself rather than under `ranks`.

```csharp
"ranks.admin"                  // core admin commands
"ranks.vip"                    // VIP multiplier flag
"ranks_weaponstats.view"       // owned by the WeaponStats module
```

CounterStrikeSharp-style permissions (`@css/...`) are **forbidden** — they are a
different framework's notation and SwiftlyS2 will not resolve them.

### Responses

```csharp
context.Reply("Message");                                   // back to chat or console
player.SendChat(Core.Localizer["prefix"] + " " + message);  // one player
Core.PlayerManager.SendChat(message);                       // everyone

// From a background thread, marshal first and re-validate
Core.Scheduler.NextTick(() =>
{
    if (player?.Controller is not { IsValid: true }) return;
    player.SendChat(message);
});
```

---

