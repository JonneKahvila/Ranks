# Changelog

Notable changes to Ranks, newest first.

<!-- The release workflow takes the version from the TOP "## [x.y.z]" heading below and
     refuses to publish unless it matches PluginMetadata.Version in
     Ranks/src/Entrypoint.cs.

     This file covers CORE only. Each module keeps its own CHANGELOG.md beside its csproj
     and is versioned independently; a core release attaches the modules as they stand.

     Add the section for 1.0.0 when the plugin is ready to ship. Until then the
     release workflow will refuse to publish, which is intended. Shape:

     ## [1.0.0] - YYYY-MM-DD

     Optional one-line summary.

     - What changed, from the server admin's side.

     See .github/instructions/changelog.instructions.md for the full style. -->

---

## [1.2.0] - 2026-11-07

Worth updating if you run MySQL or MariaDB, or if your players expect the season to
reset on the 1st of the month. Includes the work versioned 1.1.1, which was never
released on its own.

- Players whose names contain emoji or special letters (such as 𝓩) were never saved on
  MySQL/MariaDB: the log showed `skipped <SteamID> in this save pass` on every save, and
  their progress was lost whenever the server restarted or Ranks reloaded. They are saved
  from now on, but progress already lost cannot be recovered.
- The first boot converts the `name` column of `ranks_players` and `ranks_players_history`
  to `utf8mb4`. On a host that does not allow schema changes, run this by hand (add your
  table prefix/suffix): `ALTER TABLE ranks_players MODIFY COLUMN name VARCHAR(128) CHARACTER
  SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT '';` and the same for
  `ranks_players_history`. PostgreSQL and SQLite were not affected.
- With `Database.TablePrefix` or `TableSuffix` set, the migration ledgers carried it twice,
  such as `awp_awp_ranks_migrations`. The first boot renames core's and every module's to the
  right name, `awp_ranks_migrations`, and runs nothing again. On a host that does not allow
  schema changes, rename them by hand: `ALTER TABLE awp_awp_ranks_migrations RENAME TO
  awp_ranks_migrations;`.
- On PostgreSQL, a second Ranks with a different `Database.TablePrefix` or `TableSuffix` in the
  same database failed to create its tables (`relation "PK_ranks_players" already exists`).
  Primary keys now carry the prefix and suffix too. Tables that already exist keep theirs.
- The `Plugin 'Ranks' uses obsolete API: ...ISchedulerService.NextTickAsync` warning no
  longer appears when Ranks loads. It was a false alarm from SwiftlyS2's own check, not a
  fault in Ranks.
- New `Database.VirtualPurgeMode` in `config.jsonc`. Set it to `1` and a new season starts at
  00:00 UTC on the 1st of every month, so seasons follow the calendar instead of drifting by a
  few days each month.
- The default, `0`, keeps seasons working exactly as before, so existing configs need no
  change. Any other number is reported at boot and treated as `0`.
- Switching from `0` to `1` does not end the running season early: it lasts until the first 1st
  on or after its scheduled end, and every season after that starts on the 1st. A season that
  began on a 1st already counts as a monthly one and ends on the next 1st.
- Leave `Database.VirtualPurgeDays` at its old value when you switch, because it is how Ranks
  knows when the running season was due to end. At `0`, that season ends on the first 1st after
  it began, which may be straight away.
- Every server sharing one database must use the same value, so switch them together. A server
  that falls behind now moves to the season another has started, where it used to put that
  player back into an older season and archive the running one early.
- Under **Previous seasons**, a season that ran exactly one calendar month is now listed by
  its month, such as "November 2026". Other seasons still read "Season from 2026-11-07".
- The Leaderboard's season line no longer gets cut off, and it now sits under the title, where
  the selection skips it, instead of being a row that did nothing when selected. A monthly
  season reads "Season: November 2026", and any other shows its last day, such as
  "Last day: 2026-12-06".
- If you run your own translation file, add `menu.period.month`, `menu.period.current_month`,
  `menu.period.last_day` and `month.1` to `month.12`, and remove `menu.period.ends`. A missing
  key shows in game as raw text.

## [1.1.0] - 2026-11-06

Seasons: optional automatic resets that keep every past season and an all-time board
([#2](https://github.com/SwiftlyS2-Plugins/Ranks/issues/2)).

- New `Database.VirtualPurgeDays` in `config.jsonc`, `0` (off) by default. Set it to `30` and
  every 30 days everyone's rank and statistics start over at `Rank.StartPoints`. Nothing is
  deleted: each finished season is kept, and players are told in chat when a new one starts.
- `!top` and `!stats` gain "This season", "All time" and "Previous seasons" while seasons are
  on. The all-time board ranks players as if no season had ever reset them.
- The first boot adds a `period` column to `ranks_players` and creates `ranks_players_history`.
  On a host that does not allow schema changes, apply them by hand before upgrading: a missing
  column is now reported once at boot, and nothing is recorded, instead of every player load failing.
- Turning seasons on starts the first one immediately; everything recorded before it appears
  as "Before seasons". Every server sharing one database must use the same value, and a
  changed value takes effect at its next season boundary.
- `!resetrank` and `!resetmyrank` now tell modules about the reset, so update all modules with
  this release: weapon stats, combo counts and Elo ratings are cleared with the rank instead of
  surviving it. Past seasons are not touched by a reset.
- `GetPositionAsync` returns `0` for a player with no figures instead of ranking them first.

## [1.0.5] - 2026-11-04

- New read-only API members for menu renderers and other plugins: the ladder (`GetLadder`),
  the scoreboard's rank icon for any point total (`GetRankIcon`), and any player's statistics
  whether online or not (`LoadSnapshotAsync`). Snapshots now also carry headshots, hits and
  misses. CustomHUD draws the `!rank` pages from these, so update it along with Ranks.
- Aliases in `commands.jsonc` and `Vip.Flags` in `config.jsonc` now replace the shipped ones
  instead of adding to them: changing `myrank` used to keep `!myrank` registered, and replacing
  `@ranks/vip` still granted VIP to it. Check both files if you edited either list.
- Includes `Ranks_WeaponStats` 1.0.2, which fixes the same alias problem for `!ws`.
- `!setpoints`, `!givepoints`, `!removepoints` and `!resetrank` now find their target: every
  correctly typed command was answered with a usage message. The target is a name, a SteamID
  or the player's id from `status`, and can be someone spectating or still picking a team.
- A partial name that matches several players is refused instead of changing all of them.
  Type more of the name, or use the SteamID or `status` id.

## [1.0.4] - 2026-11-04

- Fixed Ranks breaking other plugins that use FluentMigrator, such as VIPCore. After
  updating, delete `FluentMigrator*.dll` from `Ranks/resources/exports/` and update all
  Ranks modules.
- Statistics now stop whenever points do: below `Rank.MinPlayers`, during warmup with
  `Rank.WarmupPoints` off, and against bots with `Rank.BotsEnabled` off. A drawn round no
  longer counts as a loss.

## [1.0.3] - 2026-11-01

- Use event-driven update frequency for Scoreboard rank instead of OnTick. This improves performance a lot.
- Improved lazy player data loading

## [1.0.2] - 2026-10-30

- Now everyone can see everyone's rank by default without any additional modules required.
- Added `Scoreboard.Reveal` to `config.jsonc` to control when that reveal is re-sent —
  `OnRoundStart`, `OnPlayerJoin`, `OnRankChange` and `JoinDelaySeconds`, all on by default.

## [1.0.1] - 2026-10-29

- Fixed `RankMode` modes to have 4 modes instead of 5 (5-th one was DangerZone which is not present in CS2)
- Removed `Mode` config option. It was being used as a fallback for `RankMode` (leftover thing from POC version)

## [1.0.0] - 2026-10-29

- Fixed modules depenency loading when publishing the plugin

## [1.0.0] - 2026-10-29

- Initial release
