namespace Ranks.WeaponStats.Stats;

/// <summary>Per-weapon counters for one session.</summary>
internal sealed class WeaponCounters
{
    /// <remarks>
    /// Twelve, not eleven. HitGroup_t runs GENERIC = 0 through SPECIAL = 11 with a hole at
    /// UNUSED = 9, so indexing by the raw enum value needs 12 slots — an 11-element array
    /// overflows on SPECIAL. Slot 9 stays zero forever and is skipped at flush time.
    /// </remarks>
    public const int HitGroupSlots = 12;

    public long Shots;
    public long Hits;
    public long Misses;
    public long Damage;
    public long Kills;
    public long Deaths;
    public long Headshots;

    /// <remarks>
    /// Per weapon rather than per player so a single gun's distribution is answerable; the player
    /// total is the sum across weapons, which is why it is no longer tracked separately.
    /// </remarks>
    public long[] HitGroupHits { get; } = new long[HitGroupSlots];

    public long[] HitGroupDamage { get; } = new long[HitGroupSlots];

    /// <summary>A fired shot that has not yet been seen to damage anyone.</summary>
    /// <remarks>
    /// Latch state, not a counter, so it is deliberately absent from <see cref="IsEmpty"/> and
    /// from the detached copy. It can only ever be true alongside a non-zero <see cref="Shots"/>
    /// within the same flush window, so a pending shot can never be skipped as empty.
    /// </remarks>
    public bool PendingShot;

    /// <remarks>
    /// The hitgroup arrays are not consulted: a hitgroup slot only ever moves alongside
    /// <see cref="Hits"/>, so a weapon with an empty scalar set has an empty distribution too.
    /// </remarks>
    public bool IsEmpty => (Shots | Hits | Misses | Damage | Kills | Deaths | Headshots) == 0;

    /// <summary>Banks an unresolved shot as a miss.</summary>
    public void ResolvePendingShot()
    {
        if (!PendingShot) return;

        PendingShot = false;
        Misses++;
    }
}

/// <summary>One player's unflushed deltas. Game thread only.</summary>
/// <remarks>
/// No synchronisation anywhere: every mutator and the snapshot pass all run on the game thread,
/// which is what makes the design race-free without a lock or a concurrent collection.
/// </remarks>
internal sealed class SessionStats
{
    public required ulong SteamId { get; init; }

    public Dictionary<string, WeaponCounters> Weapons { get; } = new(StringComparer.Ordinal);

    public WeaponCounters For(string weapon)
    {
        if (Weapons.TryGetValue(weapon, out var counters)) return counters;

        counters = new WeaponCounters();
        Weapons[weapon] = counters;

        return counters;
    }

    /// <summary>Takes the accumulated deltas, stamped with the period they were earned in, and zeroes them in place.</summary>
    /// <remarks>
    /// Detaching on the game thread is what lets the writer run off it and never touch a live
    /// session again. Zeroing in place rather than replacing the object keeps the slot reference
    /// valid for handlers that are mid-dispatch.
    /// </remarks>
    public StatsDelta? Detach(long? period) => Capture(reset: true, period);

    /// <summary>Copies the accumulated deltas without clearing them.</summary>
    /// <remarks>
    /// For the read API: the database holds only what was last flushed, so a query that ignored
    /// this would under-report by the whole of the player's current session — up to
    /// FlushIntervalSeconds behind, for exactly the player most likely to be asked about.
    /// Reading rather than flushing keeps a query from costing a write. Unstamped: a copy is never
    /// written, and the reader decides which period it belongs to.
    /// </remarks>
    public StatsDelta? Peek() => Capture(reset: false, period: null);

    private StatsDelta? Capture(bool reset, long? period)
    {
        var weapons = new List<(string Weapon, WeaponCounters Counters)>();
        var weaponHitGroups = new List<(string Weapon, short HitGroup, long Hits, long Damage)>();

        var rolledHits = new long[WeaponCounters.HitGroupSlots];
        var rolledDamage = new long[WeaponCounters.HitGroupSlots];

        foreach (var (weapon, counters) in Weapons)
        {
            // A flush is many ticks after the shot, so a still-unresolved latch can only be a
            // miss. Peek must not do this: banking the miss on a read would bank it again on the
            // next fire.
            if (reset) counters.ResolvePendingShot();

            if (counters.IsEmpty) continue;

            var copy = new WeaponCounters
            {
                Shots = counters.Shots,
                Hits = counters.Hits,
                Misses = counters.Misses,
                Damage = counters.Damage,
                Kills = counters.Kills,
                Deaths = counters.Deaths,
                Headshots = counters.Headshots,
            };

            for (short slot = 0; slot < WeaponCounters.HitGroupSlots; slot++)
            {
                var hits = counters.HitGroupHits[slot];
                var damage = counters.HitGroupDamage[slot];

                if (hits == 0 && damage == 0) continue;

                copy.HitGroupHits[slot] = hits;
                copy.HitGroupDamage[slot] = damage;

                weaponHitGroups.Add((weapon, slot, hits, damage));

                rolledHits[slot] += hits;
                rolledDamage[slot] += damage;
            }

            weapons.Add((weapon, copy));

            if (!reset) continue;

            counters.Shots = 0;
            counters.Hits = 0;
            counters.Misses = 0;
            counters.Damage = 0;
            counters.Kills = 0;
            counters.Deaths = 0;
            counters.Headshots = 0;

            Array.Clear(counters.HitGroupHits);
            Array.Clear(counters.HitGroupDamage);
        }

        var hitGroups = new List<(short HitGroup, long Hits, long Damage)>();

        for (short slot = 0; slot < WeaponCounters.HitGroupSlots; slot++)
        {
            if (rolledHits[slot] == 0 && rolledDamage[slot] == 0) continue;

            hitGroups.Add((slot, rolledHits[slot], rolledDamage[slot]));
        }

        return weapons.Count == 0 ? null : new StatsDelta(SteamId, period, weapons, hitGroups, weaponHitGroups);
    }
}

/// <summary>A detached batch, safe to hand to the writer.</summary>
/// <remarks>
/// <paramref name="HitGroups"/> is the per-weapon breakdown rolled up, so the two agree by
/// construction rather than by two code paths counting the same events.
///
/// <paramref name="Period"/> is core's period the deltas were earned in, or null while periods are
/// off, which writes them to the all-time tables only. It is stamped when the batch is cut rather
/// than when it is written, because the retry buffer mixes batches: one retried after a boundary
/// still lands in its own period (FR-DB-10).
/// </remarks>
internal sealed record StatsDelta(
    ulong SteamId,
    long? Period,
    IReadOnlyList<(string Weapon, WeaponCounters Counters)> Weapons,
    IReadOnlyList<(short HitGroup, long Hits, long Damage)> HitGroups,
    IReadOnlyList<(string Weapon, short HitGroup, long Hits, long Damage)> WeaponHitGroups);
