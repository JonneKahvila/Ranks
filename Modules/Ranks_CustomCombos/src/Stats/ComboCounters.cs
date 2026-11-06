namespace Ranks.CustomCombos.Stats;

/// <summary>Per-player combo counts awaiting a flush. Game thread only.</summary>
/// <remarks>
/// Accumulated in memory because the arbiter runs inside core's award path, where a database round
/// trip would sit on the game thread for every kill.
///
/// Holds no period of its own: core changes period only on the game thread, and the module drains
/// on that change, so everything held here was earned in the period current at the next drain
/// (FR-DB-10).
/// </remarks>
internal sealed class ComboCounters
{
    private readonly Dictionary<ulong, Dictionary<string, int>> _pending = [];

    public void Record(ulong steamId, string comboKey)
    {
        if (!_pending.TryGetValue(steamId, out var combos))
        {
            combos = new Dictionary<string, int>(StringComparer.Ordinal);
            _pending[steamId] = combos;
        }

        combos[comboKey] = combos.GetValueOrDefault(comboKey) + 1;
    }

    /// <summary>Takes everything pending, stamped with the period it was earned in, and clears it.</summary>
    /// <param name="period">Null while core runs no periods, which writes the lifetime table only.</param>
    public List<ComboDelta> Drain(long? period)
    {
        if (_pending.Count == 0) return [];

        var batch = new List<ComboDelta>(_pending.Count);

        foreach (var (steamId, combos) in _pending)
            foreach (var (key, count) in combos)
                batch.Add(new ComboDelta(steamId, key, count, period));

        _pending.Clear();

        return batch;
    }

    /// <summary>Drops one player's unflushed counts, so a reset cannot see them written afterwards.</summary>
    public void Discard(ulong steamId) => _pending.Remove(steamId);

    /// <summary>One player's unflushed counts, without clearing them.</summary>
    /// <remarks>
    /// Copied rather than handed out: the caller is the published API, whose continuation runs off
    /// the game thread this dictionary belongs to.
    /// </remarks>
    public Dictionary<string, int> Peek(ulong steamId) =>
        _pending.TryGetValue(steamId, out var combos)
            ? new Dictionary<string, int>(combos, StringComparer.Ordinal)
            : [];
}

/// <summary>One drained count, with the period it was earned in.</summary>
/// <remarks>The period travels with the row, so a write retried after a period change still lands in the period it was earned in.</remarks>
internal readonly record struct ComboDelta(ulong SteamId, string ComboKey, int Count, long? Period);
