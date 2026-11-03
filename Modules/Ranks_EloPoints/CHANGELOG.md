# Changelog

Notable changes to Ranks_EloPoints, newest first.

<!-- The release workflow takes the version from the TOP "## [x.y.z]" heading below and
     refuses to publish unless it matches PluginMetadata.Version in
     src/Ranks_EloPoints.cs. Shape:

     ## [1.0.0] - YYYY-MM-DD

     Optional one-line summary.

     - What changed, from the server admin's side.

     See .github/instructions/changelog.instructions.md for the full style. -->

## [1.0.1] - 2026-11-03

- Required by Ranks 1.0.4 and newer, which no longer loads 1.0.0 of this module. This version
  also works with older Ranks.
- A rating now moves on exactly the rounds Ranks counted in its statistics. Rounds played below
  `Rank.MinPlayers`, in warmup with `Rank.WarmupPoints` off, or against a team made up only of
  bots with `Rank.BotsEnabled` off are not rated.
- `Rank.MinPlayers` now counts the humans connected during each round, as Ranks does, instead
  of everyone who played at any point in the match. A match is no longer rated when the server
  never held that many players at once, or left unrated while Ranks kept its statistics.
- A drawn round no longer counts as a lost round for everyone.
- This needs Ranks 1.0.4. With an older Ranks the module rates as before, and the log says so
  once.

## [1.0.0] - 2026-10-26

First release. Faceit-style Elo rating in place of core's per-kill scoring.

- Rating is settled once per match from the result, not per kill. Set
  `Rank.UseCoreRankingLogic` to `false` so this module owns scoring.
- Shrinkage keeps a short session from counting as a full result, and a performance bonus
  rewards carrying a losing team.
- Abandoning a match applies a configurable penalty.
- Published `Ranks.EloPoints.Api.v1` for standings, the leaderboard and whether a rated
  match is in progress.
