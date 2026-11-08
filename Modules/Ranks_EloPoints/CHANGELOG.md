# Changelog

Notable changes to Ranks_EloPoints, newest first.

<!-- The release workflow takes the version from the TOP "## [x.y.z]" heading below and
     refuses to publish unless it matches PluginMetadata.Version in
     src/Ranks_EloPoints.cs. Shape:

     ## [1.0.0] - YYYY-MM-DD

     Optional one-line summary.

     - What changed, from the server admin's side.

     See .github/instructions/changelog.instructions.md for the full style. -->

## [1.1.1] - 2026-11-08

- No changes; released alongside Ranks 1.3.1.

## [1.1.0] - 2026-11-06

Requires Ranks 1.1.0. Worth updating if you use `!resetrank` or `!resetmyrank`.

- Ratings restart with each season when core's `Database.VirtualPurgeDays` is set: everyone
  starts the new season from `Rank.StartPoints`, and every past season's final ratings are kept.
- `!resetrank` and `!resetmyrank` now clear the rating for good. Before, the old rating came
  back at the end of the player's next match.
- A match still in progress when a season ends goes unrated.
- A player whose stored rating has not loaded by the end of a match is left unrated for it,
  instead of being dropped to `Rank.MinPoints`.
- First boot adds a `period` column to `ranks_elopoints` and creates the
  `ranks_elopoints_history` table. Existing ratings carry on unchanged while seasons are off.
- `Ranks.EloPoints.Api.v1` can read a past season's standings and leaderboard. Its existing
  calls now answer for the current season only.

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
