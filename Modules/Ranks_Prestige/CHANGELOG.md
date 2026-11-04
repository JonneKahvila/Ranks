# Changelog

Notable changes to Ranks_Prestige, newest first.

<!-- The release workflow takes the version from the TOP "## [x.y.z]" heading below and
     refuses to publish unless it matches PluginMetadata.Version in
     src/Ranks_Prestige.cs. Shape:

     ## [1.0.0] - YYYY-MM-DD

     Optional one-line summary.

     - What changed, from the server admin's side.

     See .github/instructions/changelog.instructions.md for the full style. -->

## [1.0.1] - 2026-11-04

- Publishes a read API, `Ranks.Prestige.Api.v1`, with each player's prestige level and the
  points to the next one. The module now ships `Ranks_Prestige.Contract.dll` in
  `resources/exports`; install it with the plugin.

## [1.0.0] - 2026-10-26

First release. Prestige levels shown as a virtual deduction, with the player's real points
left untouched.

- Crossing a level's `MinimumPoints` restarts the visible ladder at zero, while stored
  points, stats and `!top` keep the true total.
- The rank icon and the clan tag follow the displayed total, so a prestiged player drops
  back to the bottom tier instead of keeping a top-tier icon beside a reset number.
- `TagPrefix` adds a prestige marker to core's clan tag, composed by core — the module never
  writes the tag itself and so never flickers against it.
- A level can switch the scoreboard mode (`Premier`, `Competitive`, `Wingman`,
  `Custom`), so later prestiges can look different from earlier ones.
- The display updates on every points change rather than on a timer.
- `!prestige` (alias `!myprestige`) reports the current level and the points to the next,
  as a menu that is also listed under core's `!rank` → Statistics.
- No database and no migration: the level is derived from points core already stores.
