# Changelog

Notable changes to Ranks_CustomCombos, newest first.

<!-- The release workflow takes the version from the TOP "## [x.y.z]" heading below and
     refuses to publish unless it matches PluginMetadata.Version in
     src/Ranks_CustomCombos.cs. Shape:

     ## [1.0.0] - YYYY-MM-DD

     Optional one-line summary.

     - What changed, from the server admin's side.

     See .github/instructions/changelog.instructions.md for the full style. -->

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
