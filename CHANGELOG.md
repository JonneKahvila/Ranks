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
