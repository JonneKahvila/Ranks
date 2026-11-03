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

## [1.0.4] - 2026-11-03

Update if another plugin on the server uses FluentMigrator, such as VIPCore, or if stats
were still being recorded below `Rank.MinPlayers`. Update every Ranks module at the same
time.

- Ranks no longer breaks other plugins' database migrations. With Ranks installed, VIPCore
  failed at startup with `Method 'get_Quoter' ... does not have an implementation`.
- After installing, delete `FluentMigrator.dll` and `FluentMigrator.Abstractions.dll` from
  `Ranks/resources/exports/` and restart. Unzipping over an older Ranks leaves them behind,
  and Ranks logs an error at startup until they are gone.
- `Ranks_WeaponStats`, `Ranks_EloPoints` and `Ranks_CustomCombos` older than 1.0.1 no longer
  load with this version.
- With fewer humans than `Rank.MinPlayers`, statistics now stop along with points. Kills,
  deaths, assists, headshots, hits and misses, round and match results, bomb plants and
  defuses, and `Ranks_CustomCombos` combo counts all kept counting before.
- A killstreak started below `Rank.MinPlayers` no longer pays out once the server fills.
- During warmup with `Rank.WarmupPoints` off, round and match results and bomb plants and
  defuses are no longer recorded. Kills, deaths and accuracy already stopped.
- A drawn round no longer counts as a lost round for everyone, and no longer costs both
  teams the `round_lose` points.
- With `Rank.BotsEnabled` off, nothing counts while a team is made up only of bots. Round
  and match results, bomb plants and defuses, and accuracy no longer count or pay against
  an all-bot team; kills and hits on bots already did not. `Rank.FfaMode` servers are
  unaffected.

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
