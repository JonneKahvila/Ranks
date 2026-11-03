# Changelog

Notable changes to Ranks_EloPoints, newest first.

<!-- The release workflow takes the version from the TOP "## [x.y.z]" heading below and
     refuses to publish unless it matches PluginMetadata.Version in
     src/Ranks_EloPoints.cs. Shape:

     ## [1.0.0] - YYYY-MM-DD

     Optional one-line summary.

     - What changed, from the server admin's side.

     See .github/instructions/changelog.instructions.md for the full style. -->

## [1.0.1] - 2026-11-04

- Required by Ranks 1.0.4, which no longer loads 1.0.0 of this module.
- Ratings now move only on rounds Ranks counts, so `Rank.MinPlayers`, warmup and bot rules
  apply to both the same way. A drawn round no longer counts as a loss.

## [1.0.0] - 2026-10-26

First release. Faceit-style Elo rating in place of core's per-kill scoring.

- Rating is settled once per match from the result, not per kill. Set
  `Rank.UseCoreRankingLogic` to `false` so this module owns scoring.
- Shrinkage keeps a short session from counting as a full result, and a performance bonus
  rewards carrying a losing team.
- Abandoning a match applies a configurable penalty.
- Published `Ranks.EloPoints.Api.v1` for standings, the leaderboard and whether a rated
  match is in progress.
