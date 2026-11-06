using Microsoft.Extensions.Logging;
using Ranks.WeaponStats.Contract;
using Ranks.WeaponStats.Stats;
using SwiftlyS2.Shared;

namespace Ranks.WeaponStats.Api;

/// <summary>This module's published read API.</summary>
/// <remarks>
/// Unlike <see cref="RanksBridge"/> this names its contract without any JIT isolation, and that is
/// correct: a provider hard-requires the assembly it publishes from, exactly as core does with
/// Ranks.Contract. This module ships that assembly itself, so it is present whenever the module is.
/// </remarks>
internal sealed class WeaponStatsApi : IWeaponStatsApi
{
    private readonly ISwiftlyCore _core;
    private readonly Writer _writer;
    private readonly Collector _collector;
    private readonly Func<long?> _writePeriod;
    private readonly ILogger _logger;

    /// <param name="writePeriod">The period unwritten counts will land in, or null while core's periods are off.</param>
    public WeaponStatsApi(ISwiftlyCore core, Writer writer, Collector collector, Func<long?> writePeriod, ILogger logger)
    {
        _core = core;
        _writer = writer;
        _collector = collector;
        _writePeriod = writePeriod;
        _logger = logger;
    }

    public Task<IReadOnlyList<WeaponStatRow>> GetWeaponsAsync(ulong steamId) => GetWeaponsAsync(steamId, IWeaponStatsApi.AllTime);

    public async Task<IReadOnlyList<WeaponStatRow>> GetWeaponsAsync(ulong steamId, long period)
    {
        var live = await PeekAsync(steamId, period);
        var stored = await _writer.ReadWeaponsAsync(steamId, StoredPeriod(period));

        var merged = new Dictionary<string, WeaponStatRow>(StringComparer.Ordinal);

        foreach (var (weapon, shots, hits, misses, damage, kills, deaths, headshots) in stored)
            merged[weapon] = new WeaponStatRow(weapon, shots, hits, misses, damage, kills, deaths, headshots);

        foreach (var (weapon, counters) in live?.Weapons ?? [])
        {
            var row = merged.GetValueOrDefault(weapon) ?? new WeaponStatRow(weapon, 0, 0, 0, 0, 0, 0, 0);

            merged[weapon] = row with
            {
                Shots = row.Shots + counters.Shots,
                Hits = row.Hits + counters.Hits,
                Misses = row.Misses + counters.Misses,
                Damage = row.Damage + counters.Damage,
                Kills = row.Kills + counters.Kills,
                Deaths = row.Deaths + counters.Deaths,
                Headshots = row.Headshots + counters.Headshots,
            };
        }

        return [.. merged.Values];
    }

    public Task<IReadOnlyList<HitGroupStatRow>> GetHitGroupsAsync(ulong steamId) => GetHitGroupsAsync(steamId, IWeaponStatsApi.AllTime);

    public async Task<IReadOnlyList<HitGroupStatRow>> GetHitGroupsAsync(ulong steamId, long period)
    {
        var live = await PeekAsync(steamId, period);
        var stored = await _writer.ReadHitGroupsAsync(steamId, StoredPeriod(period));

        var merged = new Dictionary<short, HitGroupStatRow>();

        foreach (var (hitGroup, hits, damage) in stored)
            merged[hitGroup] = new HitGroupStatRow(hitGroup, hits, damage);

        foreach (var (hitGroup, hits, damage) in live?.HitGroups ?? [])
        {
            var row = merged.GetValueOrDefault(hitGroup) ?? new HitGroupStatRow(hitGroup, 0, 0);

            merged[hitGroup] = row with { Hits = row.Hits + hits, Damage = row.Damage + damage };
        }

        return [.. merged.Values];
    }

    public Task<IReadOnlyList<WeaponHitGroupStatRow>> GetWeaponHitGroupsAsync(ulong steamId) =>
        GetWeaponHitGroupsAsync(steamId, IWeaponStatsApi.AllTime);

    public async Task<IReadOnlyList<WeaponHitGroupStatRow>> GetWeaponHitGroupsAsync(ulong steamId, long period)
    {
        var live = await PeekAsync(steamId, period);
        var stored = await _writer.ReadWeaponHitGroupsAsync(steamId, StoredPeriod(period));

        var merged = new Dictionary<(string Weapon, short HitGroup), WeaponHitGroupStatRow>();

        foreach (var (weapon, hitGroup, hits, damage) in stored)
            merged[(weapon, hitGroup)] = new WeaponHitGroupStatRow(weapon, hitGroup, hits, damage);

        foreach (var (weapon, hitGroup, hits, damage) in live?.WeaponHitGroups ?? [])
        {
            var key = (weapon, hitGroup);
            var row = merged.GetValueOrDefault(key) ?? new WeaponHitGroupStatRow(weapon, hitGroup, 0, 0);

            merged[key] = row with { Hits = row.Hits + hits, Damage = row.Damage + damage };
        }

        return [.. merged.Values];
    }

    public Task<WeaponStatRow?> GetWeaponAsync(ulong steamId, string weapon) => GetWeaponAsync(steamId, weapon, IWeaponStatsApi.AllTime);

    /// <remarks>
    /// Normalised so a caller passing <c>ak47</c> finds the row stored as <c>weapon_ak47</c>; the two
    /// event spellings are exactly what <see cref="WeaponNames"/> exists to reconcile.
    /// </remarks>
    public async Task<WeaponStatRow?> GetWeaponAsync(ulong steamId, string weapon, long period)
    {
        var normalised = WeaponNames.Normalize(weapon);

        return (await GetWeaponsAsync(steamId, period)).FirstOrDefault(row => row.Weapon == normalised);
    }

    /// <summary>The writer's argument for a period: null reads the all-time tables.</summary>
    private static long? StoredPeriod(long period) => period == IWeaponStatsApi.AllTime ? null : period;

    /// <summary>Whether the session not yet written belongs in the figures for <paramref name="period"/>.</summary>
    /// <remarks>
    /// Unwritten counts land in all time and in the period writes go to now — never in a closed
    /// period, and in no period at all while core's periods are off (FR-DB-10).
    /// </remarks>
    private bool IncludesUnwritten(long period) => period == IWeaponStatsApi.AllTime || period == _writePeriod();

    /// <remarks>
    /// Hops to the game thread: sessions are game-thread state and a consumer calls this from
    /// wherever it likes. The returned delta is a copy, so the continuation is free to use it
    /// off-thread afterwards. No hop at all for a period the session does not belong to.
    /// </remarks>
    private Task<StatsDelta?> PeekAsync(ulong steamId, long period)
    {
        if (!IncludesUnwritten(period)) return Task.FromResult<StatsDelta?>(null);

        var completion = new TaskCompletionSource<StatsDelta?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _core.Scheduler.NextTick(() =>
        {
            try
            {
                completion.SetResult(_collector.PeekFor(steamId));
            }
            catch (Exception ex)
            {
                // Falling back to stored-only is better than failing the query: the answer is then
                // merely behind, which is what it would have been without this merge at all.
                _logger.LogWarning(ex, "Ranks_WeaponStats: could not read live session stats for {SteamId}.", steamId);
                completion.SetResult(null);
            }
        });

        return completion.Task;
    }
}
