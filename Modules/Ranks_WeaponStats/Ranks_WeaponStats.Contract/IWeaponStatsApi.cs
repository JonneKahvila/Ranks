namespace Ranks.WeaponStats.Contract;

/// <summary>One player's totals for a single weapon: lifetime, or one period's from an overload that takes one.</summary>
/// <remarks>
/// <paramref name="Weapon"/> is the entity designer name — <c>weapon_ak47</c>, not <c>ak47</c>.
/// Death sources that are not weapons keep their bare name (<c>world</c>, <c>planted_c4</c>), and
/// a value the module could not use at all becomes <c>unknown</c>.
///
/// <paramref name="Misses"/> counts trigger pulls that damaged nobody, which is <em>not</em>
/// <c>Shots - Hits</c>: one shotgun blast is a single shot that can land nine hits, so that
/// subtraction goes negative. Weapons that cannot deal damage at all — smokes, flashes, decoys —
/// therefore report every shot as a miss.
/// </remarks>
public sealed record WeaponStatRow(
    string Weapon,
    long Shots,
    long Hits,
    long Misses,
    long Damage,
    long Kills,
    long Deaths,
    long Headshots);

/// <summary>One player's totals for a single hitgroup, from the attacker's side: lifetime, or one period's.</summary>
/// <remarks>
/// <paramref name="HitGroup"/> is the raw <c>HitGroup_t</c> value, which is not a dense range:
/// <c>GENERIC = 0</c> through <c>SPECIAL = 11</c> with a hole at <c>UNUSED = 9</c>. Only landed
/// hits appear — a miss has no hitgroup, so misses are counted on the weapon row and on core's
/// player row instead. Nothing records where a player <em>gets</em> hit, so there is no
/// victim-side counterpart.
/// </remarks>
public sealed record HitGroupStatRow(
    short HitGroup,
    long Hits,
    long Damage);

/// <summary>Where one weapon's landed hits went, for a single hitgroup.</summary>
/// <remarks>
/// The same rows as <see cref="HitGroupStatRow"/> split by weapon — summing these over weapons
/// reproduces it exactly.
/// </remarks>
public sealed record WeaponHitGroupStatRow(
    string Weapon,
    short HitGroup,
    long Hits,
    long Damage);

/// <summary>Read access to <c>Ranks_WeaponStats</c>'s per-weapon and per-hitbox totals.</summary>
/// <remarks>
/// Additive only after 1.0, for the same reason as <c>Ranks.Contract</c>: there is no package
/// registry and therefore no version negotiation, so a renamed or re-signed member is a
/// <c>MissingMethodException</c> at call time rather than a build error anyone sees.
///
/// Every method is async because, unlike core's resident player record, these totals live in the
/// database — and an all-time read includes the caller's unflushed session delta, so a figure read
/// here always matches what <c>!weaponstats</c> would print. A period read includes it only for the
/// period being written now; see <see cref="AllTime"/>.
/// </remarks>
public interface IWeaponStatsApi
{
    /// <summary>The shared-interface key consumers bind against.</summary>
    const string InterfaceKey = "Ranks.WeaponStats.Api.v1";

    /// <summary>Pass as <c>period</c> to read all time — what every overload without one returns.</summary>
    /// <remarks>
    /// Any other value is one of core's period ids, as <c>IRanksApi.CurrentPeriod</c> and
    /// <c>GetPeriodsAsync</c> hand them out: the Unix time, in seconds, of the period's UTC start.
    /// The same value as <c>RanksPeriods.AllTime</c>, restated because this contract references no
    /// other assembly. A period holds only what was recorded while core's periods were on, and
    /// carries the unflushed session delta only while it is core's current period.
    /// </remarks>
    const long AllTime = -1;

    /// <summary>Every weapon the player has used, unordered.</summary>
    Task<IReadOnlyList<WeaponStatRow>> GetWeaponsAsync(ulong steamId);

    /// <summary>One row per hitgroup the player has landed a hit on, unordered.</summary>
    Task<IReadOnlyList<HitGroupStatRow>> GetHitGroupsAsync(ulong steamId);

    /// <summary>One row per weapon and hitgroup the player has landed a hit with, unordered.</summary>
    /// <remarks>Lets a caller render where a single gun's hits go without a second query.</remarks>
    Task<IReadOnlyList<WeaponHitGroupStatRow>> GetWeaponHitGroupsAsync(ulong steamId);

    /// <summary>A single weapon's totals, or null when the player has never used it.</summary>
    /// <remarks>Spares a caller rendering one weapon the cost of fetching the whole table.</remarks>
    Task<WeaponStatRow?> GetWeaponAsync(ulong steamId, string weapon);

    /// <summary>Every weapon the player used in one period, or all time with <see cref="AllTime"/>, unordered.</summary>
    Task<IReadOnlyList<WeaponStatRow>> GetWeaponsAsync(ulong steamId, long period);

    /// <summary>One row per hitgroup the player landed a hit on in one period, or all time, unordered.</summary>
    Task<IReadOnlyList<HitGroupStatRow>> GetHitGroupsAsync(ulong steamId, long period);

    /// <summary>One row per weapon and hitgroup the player landed a hit with in one period, or all time, unordered.</summary>
    Task<IReadOnlyList<WeaponHitGroupStatRow>> GetWeaponHitGroupsAsync(ulong steamId, long period);

    /// <summary>A single weapon's totals in one period, or all time; null when the player did not use it there.</summary>
    Task<WeaponStatRow?> GetWeaponAsync(ulong steamId, string weapon, long period);
}
