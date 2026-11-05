# Changelog

Notable changes to Ranks_WeaponStats, newest first.

<!-- The release workflow takes the version from the TOP "## [x.y.z]" heading below and
     refuses to publish unless it matches PluginMetadata.Version in
     src/Ranks_WeaponStats.cs. Shape:

     ## [1.0.0] - YYYY-MM-DD

     Optional one-line summary.

     - What changed, from the server admin's side.

     See .github/instructions/changelog.instructions.md for the full style. -->

## [1.0.2] - 2026-11-05

- Renaming or removing an alias in `commands.jsonc` now takes effect: `"Aliases": ["wstats"]`
  used to keep `!ws` working too. Restart the server after editing aliases.

## [1.0.1] - 2026-11-04

- Required by Ranks 1.0.4, which no longer loads 1.0.0 of this module.
- Weapon statistics now follow the same `Rank.MinPlayers`, warmup and bot rules as Ranks, so
  `!weaponstats` and `!stats` agree.

## [1.0.0] - 2026-10-26

First release. Per-weapon and per-hitbox statistics for the Ranks plugin.

- Tracks shots, hits, misses, damage, kills, deaths and headshots per weapon, plus hits
  and damage per hitgroup — both overall and broken down per weapon.
- `!weaponstats` (alias `!ws`) opens a paged weapon menu; selecting a weapon shows where
  that gun's hits landed. **Overall Hit Stats** is the same breakdown across every gun.
  Both are also listed under core's `!rank` → Statistics, so a player finds them without
  knowing the command exists.
- `Display.TopWeapons` was dropped: it capped rows in a chat list that no longer exists,
  and the menu pages instead. `Display.MinShotsToList` still hides barely-used weapons.
- Bot interactions are counted only when core's `Rank.BotsEnabled` is on — this module has
  no bot switch of its own, so the two can never be configured into disagreeing.
- Figures include the caller's unflushed session delta, so a reading never lags a flush
  interval behind.
- Published `Ranks.WeaponStats.Api.v1` for other plugins to read the same totals.
