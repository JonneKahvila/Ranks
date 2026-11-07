using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Ranks.Config;

/// <summary>Clamps out-of-range values and rejects ones that cannot be clamped safely.</summary>
/// <remarks>
/// Numbers clamp and warn; table affixes throw. A bad multiplier is a bad game, a bad
/// identifier is a SQL injection surface (implementation guide §3.5).
/// </remarks>
internal static partial class ConfigValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9_]*$")]
    private static partial Regex SqlIdentifier { get; }

    public static void Validate(RanksConfig config, ILogger logger)
    {
        RequireSafeAffix(config.Database.TablePrefix, nameof(DatabaseOptions.TablePrefix));
        RequireSafeAffix(config.Database.TableSuffix, nameof(DatabaseOptions.TableSuffix));

        ClampDatabase(config.Database, logger);
        ClampRank(config.Rank, logger);
        ClampPoints(config.Points, logger);
        ClampVip(config.Vip, logger);
        ClampRankMode(config.Scoreboard, logger);
        ClampReveal(config.Scoreboard.Reveal, logger);
    }

    /// <remarks>Clamps rather than throws — a bad delay is a tuning error, not a data-loss risk.</remarks>
    private static void ClampReveal(RevealOptions reveal, ILogger logger)
    {
        var clamped = Math.Clamp(reveal.JoinDelaySeconds, 0f, 30f);

        WarnIfChanged(reveal.JoinDelaySeconds, clamped, "Scoreboard.Reveal.JoinDelaySeconds", logger);
        reveal.JoinDelaySeconds = clamped;
    }

    /// <remarks>
    /// Resets rather than warns: any number binds to an enum, so an out-of-range one — <c>5</c>
    /// from before Custom was renumbered, say — would reach EngineRankType and blank every
    /// scoreboard with no error.
    /// </remarks>
    private static void ClampRankMode(ScoreboardOptions scoreboard, ILogger logger)
    {
        if (Enum.IsDefined(scoreboard.RankMode)) return;

        logger.LogWarning(
            "Ranks: Scoreboard.RankMode '{Value}' is not a known mode; using {Fallback}. Valid values: {Valid}.",
            (int)scoreboard.RankMode, RankDisplayMode.Premier, string.Join(", ", Enum.GetNames<RankDisplayMode>()));

        scoreboard.RankMode = RankDisplayMode.Premier;
    }

    /// <remarks>
    /// Any number binds to an enum, and an unknown mode must not guess a calendar: falling back to
    /// <c>Days</c> leaves <c>VirtualPurgeDays</c> in charge, which is what the operator last set.
    /// </remarks>
    private static void ClampVirtualPurgeMode(DatabaseOptions database, ILogger logger)
    {
        if (Enum.IsDefined(database.VirtualPurgeMode)) return;

        logger.LogWarning(
            "Ranks: Database.VirtualPurgeMode {Value} is not a known mode; using {Fallback}. Valid values: {Valid}.",
            (int)database.VirtualPurgeMode, AsConfigValue(VirtualPurgeMode.Days),
            string.Join(", ", Enum.GetValues<VirtualPurgeMode>().Select(AsConfigValue)));

        database.VirtualPurgeMode = VirtualPurgeMode.Days;
    }

    private static string AsConfigValue(VirtualPurgeMode mode) => $"{(int)mode} ({mode})";

    private static void RequireSafeAffix(string value, string key)
    {
        if (SqlIdentifier.IsMatch(value)) return;

        throw new InvalidOperationException(
            $"Database.{key} must match ^[A-Za-z0-9_]*$ — it becomes a SQL identifier. " +
            $"Got '{value}'.");
    }

    private static void ClampDatabase(DatabaseOptions database, ILogger logger)
    {
        database.SaveIntervalSeconds = AtLeast(database.SaveIntervalSeconds, 1, "Database.SaveIntervalSeconds", logger);
        database.PurgeDays = AtLeast(database.PurgeDays, 0, "Database.PurgeDays", logger);
        database.VirtualPurgeDays = AtLeast(database.VirtualPurgeDays, 0, "Database.VirtualPurgeDays", logger);
        ClampVirtualPurgeMode(database, logger);
        database.MaintenanceIntervalHours =
            AtLeast(database.MaintenanceIntervalHours, 0, "Database.MaintenanceIntervalHours", logger);
    }

    private static void ClampRank(RankOptions rank, ILogger logger)
    {
        rank.MinPlayers = AtLeast(rank.MinPlayers, 0, "Rank.MinPlayers", logger);
        rank.MinPoints = AtLeast(rank.MinPoints, 0, "Rank.MinPoints", logger);
        rank.StartPoints = AtLeast(rank.StartPoints, rank.MinPoints, "Rank.StartPoints", logger);
    }

    private static void ClampPoints(PointsOptions points, ILogger logger)
    {
        points.DynamicKillMin = ClampMultiplierFloor(points.DynamicKillMin, "Points.DynamicKillMin", logger);
        points.DynamicDeathMin = ClampMultiplierFloor(points.DynamicDeathMin, "Points.DynamicDeathMin", logger);
        points.DynamicKillMax = ClampMultiplierCeiling(points.DynamicKillMax, "Points.DynamicKillMax", logger);
        points.DynamicDeathMax = ClampMultiplierCeiling(points.DynamicDeathMax, "Points.DynamicDeathMax", logger);

        points.LongDistanceUnits = AtLeast(points.LongDistanceUnits, 0, "Points.LongDistanceUnits", logger);
        points.SecondsBetweenKills = AtLeast(points.SecondsBetweenKills, 0, "Points.SecondsBetweenKills", logger);
    }

    private static void ClampVip(VipOptions vip, ILogger logger)
    {
        // A flag list is a set; the same flag written twice is one flag. An empty list grants VIP
        // to nobody, and Vip.Multiplier = 1.0 turns the multiplier off.
        if (vip.Flags.Count > 1)
            vip.Flags = vip.Flags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (vip.Multiplier > 0) return;

        logger.LogWarning("Ranks: Vip.Multiplier was {Value}; clamped to 1.0.", vip.Multiplier);
        vip.Multiplier = 1.0;
    }

    /// <summary>Clamps a dynamic-scaling floor into <c>(0, 1]</c>.</summary>
    private static double ClampMultiplierFloor(double value, string key, ILogger logger)
    {
        var clamped = value switch
        {
            <= 0 => 0.01,
            > 1 => 1.0,
            _ => value,
        };

        WarnIfChanged(value, clamped, key, logger);
        return clamped;
    }

    /// <summary>Clamps a dynamic-scaling ceiling to <c>&gt;= 1</c>.</summary>
    private static double ClampMultiplierCeiling(double value, string key, ILogger logger)
    {
        var clamped = Math.Max(value, 1.0);
        WarnIfChanged(value, clamped, key, logger);
        return clamped;
    }

    private static int AtLeast(int value, int floor, string key, ILogger logger)
    {
        if (value >= floor) return value;

        logger.LogWarning("Ranks: {Key} was {Value}; clamped to {Floor}.", key, value, floor);
        return floor;
    }

    private static void WarnIfChanged(double original, double clamped, string key, ILogger logger)
    {
        if (original.Equals(clamped)) return;

        logger.LogWarning("Ranks: {Key} was {Value}; clamped to {Clamped}.", key, original, clamped);
    }
}
