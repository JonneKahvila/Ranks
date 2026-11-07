# K4-System → Ranks migration

`k4-to-ranks.sql` moves player data from the monthly K4-System fork into
SwiftlyS2 Ranks 1.2.0 with `VirtualPurgeMode` set to Monthly. K4 and Ranks live in
two databases on the same MySQL/MariaDB server: the script runs connected to the
**Ranks** database and only **reads** K4's tables from `erikpeik_jonnekahvila`.

## What ends up where

| K4 | Ranks |
|---|---|
| Play before the first monthly snapshot | **Before seasons** |
| Each past month (snapshot → next snapshot) | Its own season, e.g. **October 2026** |
| The current month (last snapshot → now) | **This season** |
| `kills`, `deaths`, `assists`, `headshots` | same columns |
| `hits_given` / `shoots` | `hits` / `misses` (`shoots − hits_given`) |
| `round_win`, `round_lose`, `game_win`, `game_lose` | `round_wins`, `round_losses`, `match_wins`, `match_losses` |
| `bomb_planted`, `bomb_defused` | `bombs_planted`, `bombs_defused` |
| Points at each month's end | that season's points; All time is worked out by Ranks |

Not carried over, because Ranks has nowhere to put them: playtime (`k4times`),
hit groups, first bloods, MVPs, grenade and special-kill counters. Weapon stats,
combos and Elo ratings start empty, since K4 never recorded them.

Everything Ranks recorded before the migration is **replaced**, including the
module tables.

## Running it

1. Make sure Ranks 1.2.0 has started at least once with `TablePrefix: "retake_"` and
   `VirtualPurgeMode: 1`, so its tables exist.
2. **Stop the CS2 server.** Ranks writes connected players back from memory and
   would overwrite the migrated rows.
3. Back up the database.
4. Check `@start_points` at the top of the script (10000 for your server). If K4's
   database is not called `erikpeik_jonnekahvila`, find-and-replace that name.
5. Run the script **while connected to the Ranks database** (the one SwiftlyS2's
   `configs/database.jsonc` points Ranks at), for example:

   ```bash
   mysql -h <host> -u <user> -p <ranks_database> < k4-to-ranks.sql
   ```

   In phpMyAdmin / HeidiSQL / MySQL Workbench, select the Ranks database first,
   then run the whole script at once. The login needs full access to the Ranks
   database and `SELECT` on K4's.
6. It ends by printing how many players and seasons it wrote. Start the server.

If you run it in the wrong database, it stops on its first line with
"Table '….retake_ranks_players' doesn't exist", before creating or changing anything.

Running it again simply redoes the migration from K4's tables, giving the same result.

## Good to know

- **Run it at the switchover.** K4 is still writing to its tables while the CSS
  server runs. Stop that server for good, then migrate, then start Ranks. Anything
  K4 records after the migration is not carried over.
- The script changes Ranks' `name` columns to `utf8mb4`. Ranks creates them as
  `utf8mb3`, which cannot hold emoji or "fancy font" letters, and Ranks then skips
  saving that player entirely. On your server that is 42 players, 23 of them
  active in the last month. This fixes it for play after the migration as well.
- A player whose K4 stats were reset keeps the months from before the reset as
  past seasons, as a Ranks `!resetmyrank` would. Their All time is therefore
  higher than K4's current total (166 players on your server). Within the month
  of the reset, only play after the reset counts.
- K4 took its snapshot when the first player connected in a new month, so play in
  the first hours of a month may be counted in the previous one.
- Ranks' `PurgeDays` (300 on your server) still applies, so players unseen for more
  than 300 days are removed at Ranks' next maintenance pass, history included.

Tested against MySQL 8.4 and MariaDB 11 with K4's table definitions and sample
players covering the edge cases above, and on MySQL with K4 and Ranks in separate
databases, using a login that can only read K4's.

Dry run on a copy of the live data (MariaDB 12, CET, 7 Nov 2026): 2 218 players and
5 145 past-season rows for March–October 2026, in under a second. Every player's
current-season points and kills matched K4's this-month figures; every player's
All time kills, deaths and headshots matched K4's totals except the 166 whose K4
stats had been reset, as described above.
