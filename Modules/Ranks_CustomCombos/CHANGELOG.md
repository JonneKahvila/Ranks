# Changelog

Notable changes to Ranks_CustomCombos, newest first.

<!-- The release workflow takes the version from the TOP "## [x.y.z]" heading below and
     refuses to publish unless it matches PluginMetadata.Version in
     src/Ranks_CustomCombos.cs. Shape:

     ## [1.0.0] - YYYY-MM-DD

     Optional one-line summary.

     - What changed, from the server admin's side.

     See .github/instructions/changelog.instructions.md for the full style. -->

## [1.1.2] - 2026-11-08

- Added Finnish translations.

## [1.1.1] - 2026-11-07

- **Previous seasons** names a season that ran exactly one calendar month by its month, such
  as "November 2026", as Ranks 1.2.0 does.
- If you run your own translation file, add `menu.combos.month` and `month.1` to `month.12`.
- With seasons off, nothing is written per season, even when Ranks 1.2.0 follows another
  server into a new season.

## [1.1.0] - 2026-11-06

Requires Ranks 1.1.0. Worth updating if you run seasons or purge inactive players.

- Combo counts follow Ranks' seasons. With `Database.VirtualPurgeDays` set in Ranks, `!combos`
  opens this season's counts, with rows for all-time counts and for each previous season.
- With seasons off, `!combos` and the API show the same lifetime counts as before.
- Players removed by Ranks' `Database.PurgeDays` now lose their combo counts too. They used to
  stay in `ranks_combos` for good.
- Combo rows left behind by earlier purges are deleted on the next server restart.
- `!resetrank`, `!resetmyrank` and **Settings → Reset my rank** now clear combo counts: every
  count with seasons off, this season's with seasons on. They used to leave them untouched.
- First boot creates one table, `ranks_combos_period`. On a host that does not allow schema
  changes, create it by hand before upgrading — the README lists its columns.
- New API overloads read one season: `GetCombosAsync` and `GetComboAsync` take a period, and
  `ICustomCombosApi.AllTime` asks for lifetime counts.

## [1.0.2] - 2026-11-04

- New API member `GetComboName`: the name `!combos` shows for a combo, so other plugins and
  menu renderers label combos the same way.
- Renaming or removing an alias in `commands.jsonc` now takes effect: the shipped `!mycombos`
  used to stay registered next to whatever replaced it.

## [1.0.1] - 2026-11-04

- Required by Ranks 1.0.4, which no longer loads 1.0.0 of this module.
- Combos no longer count, or take `first_blood`, on kills Ranks gives no points for, such as
  below `Rank.MinPlayers`.

## [1.0.0] - 2026-10-26

First release. Operator-defined kill combinations, awarded on top of core's flat catalogue
or in place of it.

- Combine kill attributes into named combos with their own point value, configured in one
  block per combo.
- Vocabulary covers the raw `player_death` flags, valued attributes (`distance:`,
  `hitgroup:`, `weapon:`) and derived ones (`weapon_class:`, `first_blood`). The README
  lists every name.
- `Count` awards same-tick multi-kills, such as one grenade killing three players.
- `SuppressCoreAwards` makes a matched combo the whole award for that kill instead of a
  bonus on top.
- Where several combos match, the most specific one pays — ties resolve by config order.
- Per-combo counters in a paged menu, opened with `!combos` (alias `!mycombos`) or from
  core's `!rank` → Statistics, and readable through the published
  `Ranks.CustomCombos.Api.v1`.
- Ships no combos by default; `resources/examples/combos.jsonc` has a starting set.
