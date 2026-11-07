# Changelog

Notable changes to Ranks_WeaponStats, newest first.

<!-- The release workflow takes the version from the TOP "## [x.y.z]" heading below and
     refuses to publish unless it matches PluginMetadata.Version in
     src/Ranks_WeaponStats.cs. Shape:

     ## [1.0.0] - YYYY-MM-DD

     Optional one-line summary.

     - What changed, from the server admin's side.

     See .github/instructions/changelog.instructions.md for the full style. -->

## [1.1.1] - 2026-11-07

- Under **Previous seasons**, a season that ran exactly one calendar month is now listed by
  its month, such as "November 2026", matching Ranks 1.2.0 with `Database.VirtualPurgeMode`
  set to `1`. Other seasons still read "Season from …". New translation keys:
  `menu.period.month` and `month.1` to `month.12`.

## [1.1.0] - 2026-11-06

Requires Ranks 1.1.0. With an older Ranks it keeps working as 1.0.2 did, without seasons
or resets, and logs a warning saying so.

- Weapon and hit statistics are now kept per season as well as for all time. With core's
  `Database.VirtualPurgeDays` set, **Weapon stats** and **Overall Hit Stats** open on a choice
  of **This season**, **All time** and **Previous seasons**. With seasons off both open their
  lists directly, exactly as before.
- Per-season figures start with this update. Seasons that closed earlier, and **Before
  seasons**, show none here; everything recorded before is under **All time**.
- `!resetrank`, `!resetmyrank` and the reset in the Settings menu now clear weapon statistics
  too. Before, a reset player kept every weapon and hitbox figure.
- With seasons on, a reset clears only the current season and takes it off the all-time
  totals. Past seasons are kept, as they are in Ranks.
- The first start after updating creates three tables: `ranks_weapon_stats_period`,
  `ranks_hit_stats_period` and `ranks_weapon_hit_stats_period`. Nothing is written to them
  while seasons are off, and the existing tables keep their all-time totals.
- A purge now also removes the purged players' per-season rows.
- The published API gains overloads that take a season id. The existing calls still return
  all-time totals.

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
