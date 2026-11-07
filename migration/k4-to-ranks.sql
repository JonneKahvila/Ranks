-- =============================================================================
-- K4-System (monthly fork)  ->  SwiftlyS2 Ranks 1.2.0, monthly seasons
-- MySQL 5.7+ / 8.x and MariaDB 10.x
-- =============================================================================
--
-- What it does
--   * REPLACES everything Ranks has recorded so far (core and module tables).
--   * Copies every K4 player into Ranks, with K4's monthly snapshots turned into
--     Ranks seasons: each past month becomes its own season under
--     "Previous seasons", the current month becomes "This season", and all play
--     from before the snapshots began becomes "Before seasons".
--   * Only READS the K4 tables. They are left exactly as they are.
--
-- Before running
--   1. Ranks 1.2.0 must have started once with TablePrefix "retake_" and
--      VirtualPurgeMode 1 (Monthly), so its tables exist.
--   2. STOP THE GAME SERVER. Ranks keeps connected players in memory and writes
--      them back, which would overwrite what this script puts in.
--   3. Back up the database.
--   4. Check the two settings just below.
--
-- Run it CONNECTED TO THE RANKS DATABASE. K4's tables are read from their own
-- database on the same server, named erikpeik_jonnekahvila below; the login you
-- use needs SELECT on that database as well.
--
-- Table names assume both plugins use the prefix "retake_". If anything differs,
-- find-and-replace "erikpeik_jonnekahvila" (K4's database), "retake_k4" (K4
-- tables) and "retake_ranks" (Ranks tables).
-- =============================================================================

-- Must equal Rank.StartPoints in Ranks' config.jsonc and start-points in K4.
SET @start_points := 10000;

-- MySQL Workbench turns safe-update mode on, which refuses the DELETEs below.
SET SQL_SAFE_UPDATES = 0;

-- Ranks stores its dates in UTC. K4 wrote `lastseen` in the database server's
-- local time (CET on yours), so it is converted from the server's time zone.
-- Switching this session to UTC makes K4's TIMESTAMP columns read as UTC too.
SET @k4_time_zone := @@global.time_zone;
SET time_zone = '+00:00';

-- The month Ranks is in now, as YYYY-MM in UTC. Leave as is unless you run this
-- script in the first minutes of a new month and want to pin it.
SET @current_month := DATE_FORMAT(UTC_TIMESTAMP(), '%Y-%m');

-- A Ranks season id is the Unix time of the season's first second, in UTC.
-- TIMESTAMPDIFF from the epoch is used instead of UNIX_TIMESTAMP(), which would
-- shift every id by the database server's time zone.
SET @current_period := TIMESTAMPDIFF(SECOND, '1970-01-01 00:00:00', CONCAT(@current_month, '-01 00:00:00'));
SET @live := '9999-12';

-- Stop here, before anything is created or changed, if this is not the Ranks
-- database or K4's tables cannot be read. Each line errors on a missing table.
SELECT 1 FROM `retake_ranks_players` LIMIT 0;
SELECT 1 FROM `retake_ranks_players_history` LIMIT 0;
SELECT 1 FROM `erikpeik_jonnekahvila`.`retake_k4stats` LIMIT 0;
SELECT 1 FROM `erikpeik_jonnekahvila`.`retake_k4ranks` LIMIT 0;
SELECT 1 FROM `erikpeik_jonnekahvila`.`retake_k4monthly_snapshots` LIMIT 0;

-- -----------------------------------------------------------------------------
-- 0. Let Ranks store every player name. Ranks creates `name` as NVARCHAR, which
--    MariaDB and MySQL make utf8mb3: a name with an emoji or a "fancy font"
--    letter cannot be stored, and Ranks skips saving that player altogether.
--    utf8mb4 stores any name. Safe to repeat.
-- -----------------------------------------------------------------------------
ALTER TABLE `retake_ranks_players`
    MODIFY `name` VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT '';
ALTER TABLE `retake_ranks_players_history`
    MODIFY `name` VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT '';

-- -----------------------------------------------------------------------------
-- 1. Work tables. Real tables rather than TEMPORARY ones because MySQL cannot
--    join a temporary table to itself. Dropped again at the end.
-- -----------------------------------------------------------------------------
DROP TABLE IF EXISTS k2r_marks;
DROP TABLE IF EXISTS k2r_next;
DROP TABLE IF EXISTS k2r_periods;

-- Every point at which K4 recorded a player's running totals: one row per
-- monthly snapshot, plus the live K4 values marked @live.
CREATE TABLE k2r_marks (
    steam_id      BIGINT   NOT NULL,
    ym            CHAR(7)  NOT NULL,
    kills         BIGINT   NOT NULL,
    deaths        BIGINT   NOT NULL,
    assists       BIGINT   NOT NULL,
    headshots     BIGINT   NOT NULL,
    shoots        BIGINT   NOT NULL,
    hits          BIGINT   NOT NULL,
    round_wins    BIGINT   NOT NULL,
    round_losses  BIGINT   NOT NULL,
    match_wins    BIGINT   NOT NULL,
    match_losses  BIGINT   NOT NULL,
    bombs_planted BIGINT   NOT NULL,
    bombs_defused BIGINT   NOT NULL,
    points        BIGINT   NOT NULL,
    taken_at      DATETIME NULL,
    PRIMARY KEY (steam_id, ym)
) ENGINE=InnoDB;

-- For each mark, the player's next mark.
CREATE TABLE k2r_next (
    steam_id BIGINT  NOT NULL,
    ym       CHAR(7) NOT NULL,
    next_ym  CHAR(7) NOT NULL,
    PRIMARY KEY (steam_id, ym)
) ENGINE=InnoDB;

-- One row per player per Ranks season: what they did in it, and their points
-- when it ended.
CREATE TABLE k2r_periods (
    steam_id      BIGINT NOT NULL,
    period        BIGINT NOT NULL,
    kills         BIGINT NOT NULL,
    deaths        BIGINT NOT NULL,
    assists       BIGINT NOT NULL,
    headshots     BIGINT NOT NULL,
    shoots        BIGINT NOT NULL,
    hits          BIGINT NOT NULL,
    round_wins    BIGINT NOT NULL,
    round_losses  BIGINT NOT NULL,
    match_wins    BIGINT NOT NULL,
    match_losses  BIGINT NOT NULL,
    bombs_planted BIGINT NOT NULL,
    bombs_defused BIGINT NOT NULL,
    points        BIGINT NOT NULL,
    PRIMARY KEY (steam_id, period)
) ENGINE=InnoDB;

-- -----------------------------------------------------------------------------
-- 2. Collect K4's marks. Only real SteamID64s: anything else cannot be keyed in
--    Ranks, which stores the id as a number.
-- -----------------------------------------------------------------------------
INSERT INTO k2r_marks
SELECT CAST(s.`steam_id` AS SIGNED), s.`year_month`,
       s.`kills`, s.`deaths`, s.`assists`, s.`headshots`, s.`shoots`, s.`hits_given`,
       s.`round_win`, s.`round_lose`, s.`game_win`, s.`game_lose`,
       s.`bomb_planted`, s.`bomb_defused`, s.`points`, s.`snapshotted_at`
  FROM `erikpeik_jonnekahvila`.`retake_k4monthly_snapshots` s
 WHERE s.`steam_id` REGEXP '^7656119[0-9]{10}$';

INSERT INTO k2r_marks
SELECT CAST(s.`steam_id` AS SIGNED), @live,
       s.`kills`, s.`deaths`, s.`assists`, s.`headshots`, s.`shoots`, s.`hits_given`,
       s.`round_win`, s.`round_lose`, s.`game_win`, s.`game_lose`,
       s.`bomb_planted`, s.`bomb_defused`, COALESCE(r.`points`, @start_points), NULL
  FROM `erikpeik_jonnekahvila`.`retake_k4stats` s
  LEFT JOIN `erikpeik_jonnekahvila`.`retake_k4ranks` r ON r.`steam_id` = s.`steam_id`
 WHERE s.`steam_id` REGEXP '^7656119[0-9]{10}$';

INSERT INTO k2r_next
SELECT m.steam_id, m.ym, MIN(n.ym)
  FROM k2r_marks m
  JOIN k2r_marks n ON n.steam_id = m.steam_id AND n.ym > m.ym
 GROUP BY m.steam_id, m.ym;

-- -----------------------------------------------------------------------------
-- 3. Work out each season.
-- -----------------------------------------------------------------------------

-- Monthly seasons: from one snapshot to the next is that month's play. Points
-- are the next mark's, because K4 reset points to start-points right after
-- taking each snapshot, so a snapshot holds the previous month's final points.
-- A total that went backwards means an admin reset that player's K4 stats during
-- the month; the running total restarted from zero, so its new value is the play
-- since the reset.
INSERT INTO k2r_periods
SELECT m.steam_id,
       TIMESTAMPDIFF(SECOND, '1970-01-01 00:00:00', CONCAT(m.ym, '-01 00:00:00')),
       IF(n.kills >= m.kills, n.kills - m.kills, n.kills),
       IF(n.deaths >= m.deaths, n.deaths - m.deaths, n.deaths),
       IF(n.assists >= m.assists, n.assists - m.assists, n.assists),
       IF(n.headshots >= m.headshots, n.headshots - m.headshots, n.headshots),
       IF(n.shoots >= m.shoots, n.shoots - m.shoots, n.shoots),
       IF(n.hits >= m.hits, n.hits - m.hits, n.hits),
       IF(n.round_wins >= m.round_wins, n.round_wins - m.round_wins, n.round_wins),
       IF(n.round_losses >= m.round_losses, n.round_losses - m.round_losses, n.round_losses),
       IF(n.match_wins >= m.match_wins, n.match_wins - m.match_wins, n.match_wins),
       IF(n.match_losses >= m.match_losses, n.match_losses - m.match_losses, n.match_losses),
       IF(n.bombs_planted >= m.bombs_planted, n.bombs_planted - m.bombs_planted, n.bombs_planted),
       IF(n.bombs_defused >= m.bombs_defused, n.bombs_defused - m.bombs_defused, n.bombs_defused),
       n.points
  FROM k2r_marks m
  JOIN k2r_next x ON x.steam_id = m.steam_id AND x.ym = m.ym
  JOIN k2r_marks n ON n.steam_id = x.steam_id AND n.ym = x.next_ym
 WHERE m.ym <> @live;

-- "Before seasons": a player's first mark holds everything they did before K4
-- started taking snapshots. A player who first joined mid-month gets a
-- near-empty first snapshot, which the activity filter below drops.
INSERT INTO k2r_periods
SELECT m.steam_id, 0,
       m.kills, m.deaths, m.assists, m.headshots, m.shoots, m.hits,
       m.round_wins, m.round_losses, m.match_wins, m.match_losses,
       m.bombs_planted, m.bombs_defused, m.points
  FROM k2r_marks m
  JOIN (SELECT steam_id, MIN(ym) AS first_ym FROM k2r_marks GROUP BY steam_id) f
    ON f.steam_id = m.steam_id AND f.first_ym = m.ym
 WHERE m.kills + m.deaths + m.assists + m.shoots + m.round_wins + m.round_losses
     + m.match_wins + m.match_losses + m.bombs_planted + m.bombs_defused > 0;

-- Seasons in which a player did nothing and kept the start points are left
-- out, as Ranks itself would never have written them.
DELETE FROM k2r_periods
 WHERE period <> 0
   AND kills + deaths + assists + shoots + round_wins + round_losses
     + match_wins + match_losses + bombs_planted + bombs_defused = 0
   AND points = @start_points;

-- -----------------------------------------------------------------------------
-- 4. Replace Ranks' data. Core's tables are written in one transaction, so a
--    failure leaves them as they were.
-- -----------------------------------------------------------------------------
START TRANSACTION;

DELETE FROM `retake_ranks_players_history`;
DELETE FROM `retake_ranks_settings`;
DELETE FROM `retake_ranks_players`;

-- One player row for everyone K4 knew, in the current season. A player with no
-- play yet this month starts it at the start points, exactly as Ranks would
-- after a season turns over. Ranks needs this row even for someone with no
-- current play: its All time board is built from it.
INSERT INTO `retake_ranks_players`
    (steam_id, name, points, kills, deaths, assists, round_wins, round_losses,
     match_wins, match_losses, bombs_planted, bombs_defused, headshots, hits, misses,
     first_seen, last_seen, period)
SELECT p.steam_id,
       LEFT(COALESCE(r.`name`, s.`name`, ''), 128),
       COALESCE(c.points, @start_points),
       COALESCE(c.kills, 0), COALESCE(c.deaths, 0), COALESCE(c.assists, 0),
       COALESCE(c.round_wins, 0), COALESCE(c.round_losses, 0),
       COALESCE(c.match_wins, 0), COALESCE(c.match_losses, 0),
       COALESCE(c.bombs_planted, 0), COALESCE(c.bombs_defused, 0),
       COALESCE(c.headshots, 0), COALESCE(c.hits, 0),
       GREATEST(0, COALESCE(c.shoots, 0) - COALESCE(c.hits, 0)),
       COALESCE(p.first_taken, COALESCE(CONVERT_TZ(GREATEST(COALESCE(r.`lastseen`, s.`lastseen`), COALESCE(s.`lastseen`, r.`lastseen`)), @k4_time_zone, '+00:00'), GREATEST(COALESCE(r.`lastseen`, s.`lastseen`), COALESCE(s.`lastseen`, r.`lastseen`))), UTC_TIMESTAMP()),
       COALESCE(COALESCE(CONVERT_TZ(GREATEST(COALESCE(r.`lastseen`, s.`lastseen`), COALESCE(s.`lastseen`, r.`lastseen`)), @k4_time_zone, '+00:00'), GREATEST(COALESCE(r.`lastseen`, s.`lastseen`), COALESCE(s.`lastseen`, r.`lastseen`))), UTC_TIMESTAMP()),
       @current_period
  FROM (SELECT steam_id, MIN(taken_at) AS first_taken FROM k2r_marks GROUP BY steam_id) p
  JOIN `erikpeik_jonnekahvila`.`retake_k4stats` s
    ON s.`steam_id` = CONVERT(CAST(p.steam_id AS CHAR) USING utf8mb4) COLLATE utf8mb4_unicode_ci
  LEFT JOIN `erikpeik_jonnekahvila`.`retake_k4ranks` r ON r.`steam_id` = s.`steam_id`
  LEFT JOIN k2r_periods c ON c.steam_id = p.steam_id AND c.period = @current_period;

-- Every closed season goes to history. Seasons can only be older than the
-- current one, but the guard keeps a clock mistake from writing a future one.
INSERT INTO `retake_ranks_players_history`
    (steam_id, period, name, points, kills, deaths, assists, round_wins, round_losses,
     match_wins, match_losses, bombs_planted, bombs_defused, headshots, hits, misses, last_seen)
SELECT h.steam_id, h.period, pl.name, h.points,
       h.kills, h.deaths, h.assists, h.round_wins, h.round_losses,
       h.match_wins, h.match_losses, h.bombs_planted, h.bombs_defused,
       h.headshots, h.hits, GREATEST(0, h.shoots - h.hits),
       pl.last_seen
  FROM k2r_periods h
  JOIN `retake_ranks_players` pl ON pl.steam_id = h.steam_id
 WHERE h.period < @current_period;

COMMIT;

-- -----------------------------------------------------------------------------
-- 5. Clear the modules' test data. Each table is emptied only if it exists, so
--    this works whichever modules you installed. K4 had no per-weapon, combo or
--    Elo figures, so these all start empty.
-- -----------------------------------------------------------------------------
SET @t := 'retake_ranks_weapon_stats';             SET @q := IF((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t) > 0, CONCAT('DELETE FROM `', @t, '`'), 'DO 0'); PREPARE q FROM @q; EXECUTE q; DEALLOCATE PREPARE q;
SET @t := 'retake_ranks_weapon_stats_period';      SET @q := IF((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t) > 0, CONCAT('DELETE FROM `', @t, '`'), 'DO 0'); PREPARE q FROM @q; EXECUTE q; DEALLOCATE PREPARE q;
SET @t := 'retake_ranks_hit_stats';                SET @q := IF((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t) > 0, CONCAT('DELETE FROM `', @t, '`'), 'DO 0'); PREPARE q FROM @q; EXECUTE q; DEALLOCATE PREPARE q;
SET @t := 'retake_ranks_hit_stats_period';         SET @q := IF((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t) > 0, CONCAT('DELETE FROM `', @t, '`'), 'DO 0'); PREPARE q FROM @q; EXECUTE q; DEALLOCATE PREPARE q;
SET @t := 'retake_ranks_weapon_hit_stats';         SET @q := IF((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t) > 0, CONCAT('DELETE FROM `', @t, '`'), 'DO 0'); PREPARE q FROM @q; EXECUTE q; DEALLOCATE PREPARE q;
SET @t := 'retake_ranks_weapon_hit_stats_period';  SET @q := IF((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t) > 0, CONCAT('DELETE FROM `', @t, '`'), 'DO 0'); PREPARE q FROM @q; EXECUTE q; DEALLOCATE PREPARE q;
SET @t := 'retake_ranks_combos';                   SET @q := IF((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t) > 0, CONCAT('DELETE FROM `', @t, '`'), 'DO 0'); PREPARE q FROM @q; EXECUTE q; DEALLOCATE PREPARE q;
SET @t := 'retake_ranks_combos_period';            SET @q := IF((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t) > 0, CONCAT('DELETE FROM `', @t, '`'), 'DO 0'); PREPARE q FROM @q; EXECUTE q; DEALLOCATE PREPARE q;
SET @t := 'retake_ranks_elopoints';                SET @q := IF((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t) > 0, CONCAT('DELETE FROM `', @t, '`'), 'DO 0'); PREPARE q FROM @q; EXECUTE q; DEALLOCATE PREPARE q;
SET @t := 'retake_ranks_elopoints_history';        SET @q := IF((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t) > 0, CONCAT('DELETE FROM `', @t, '`'), 'DO 0'); PREPARE q FROM @q; EXECUTE q; DEALLOCATE PREPARE q;

-- -----------------------------------------------------------------------------
-- 6. Clean up, then report what was written.
-- -----------------------------------------------------------------------------
DROP TABLE k2r_marks;
DROP TABLE k2r_next;
DROP TABLE k2r_periods;

SELECT 'players' AS what, COUNT(*) AS `rows` FROM `retake_ranks_players`
UNION ALL
SELECT 'season rows in history', COUNT(*) FROM `retake_ranks_players_history`;

SELECT CASE WHEN period = 0 THEN 'Before seasons'
            ELSE DATE_FORMAT(DATE_ADD('1970-01-01 00:00:00', INTERVAL period SECOND), '%Y-%m') END AS season,
       COUNT(*) AS players, SUM(kills) AS kills
  FROM `retake_ranks_players_history`
 GROUP BY period
 ORDER BY period;
