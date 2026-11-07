using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Ranks.Api;
using Ranks.Config;
using Ranks.Contract;
using Ranks.Database;
using Ranks.Logging;
using Ranks.Ranking;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;

namespace Ranks.Players;

/// <summary>Owns when a record is loaded, when it is written, and when bulk work runs.</summary>
internal sealed class PlayerSessions
{
    /// <remarks>10s total, which covers a slow Steam handshake without holding a slot forever.</remarks>
    private const int AuthPollAttempts = 100;
    private const int AuthPollIntervalMs = 100;

    private readonly ISwiftlyCore _core;
    private readonly ConfigHost<RanksConfig> _config;
    private readonly PlayerStore _store;
    private readonly PlayerRegistry _registry;
    private readonly RankLadder _ladder;
    private readonly RanksEventHub _events;
    private readonly DetailLog _detail;
    private readonly ILogger _logger;

    private readonly Lock _saveGate = new();
    private Task<int>? _inFlightSave;

    /// <remarks>Both triggers reach Track before either load publishes; without this the row is
    /// read twice and PlayerDataLoaded is raised twice.</remarks>
    private readonly ConcurrentDictionary<ulong, byte> _loading = new();

    /// <remarks>In memory by design: a pass always runs at cold start, so a persisted timestamp
    /// could only ever suppress a run that is already the default behaviour.</remarks>
    private DateTime _lastMaintenance = DateTime.MinValue;

    /// <remarks>Written only on the game thread, by initialization and rollover; read from anywhere.</remarks>
    private long _activePeriod;

    private CancellationTokenSource? _saveTimer;

    public PlayerSessions(
        ISwiftlyCore core,
        ConfigHost<RanksConfig> config,
        PlayerStore store,
        PlayerRegistry registry,
        RankLadder ladder,
        RanksEventHub events,
        DetailLog detail,
        ILogger logger)
    {
        _core = core;
        _config = config;
        _store = store;
        _registry = registry;
        _ladder = ladder;
        _events = events;
        _detail = detail;
        _logger = logger;
    }

    /// <summary>True once the schema probe has run; false disables every write.</summary>
    public bool SchemaUsable { get; set; } = true;

    /// <summary>The period every resident record is in, and every new record starts in (FR-DB-10).</summary>
    public long ActivePeriod => Volatile.Read(ref _activePeriod);

    public bool PeriodsEnabled => CurrentGrid.IsEnabled;

    public DateTime? ActivePeriodEndsAt =>
        CurrentGrid is { IsEnabled: true } grid ? PeriodClock.EndOf(ActivePeriod, grid) : null;

    private PeriodGrid CurrentGrid => PeriodGrid.Of(_config.Current.Database);

    /// <summary>Settles the period before anyone is tracked. False when it cannot be known.</summary>
    /// <remarks>
    /// Resumes the newest period the data holds until it ends — with periods off, so switching them
    /// off never starts a period, and after <c>VirtualPurgeMode</c> or <c>VirtualPurgeDays</c>
    /// changed, so a period id never goes backwards and a running season is not cut short. A failure must not
    /// guess: every row on a later period than the guess would be closed and reset on its owner's
    /// next load, which is a virtual purge nobody asked for.
    /// </remarks>
    public bool TryInitializePeriod()
    {
        var grid = CurrentGrid;

        try
        {
            var latest = _store.LatestPeriod();
            var period = grid.IsEnabled ? PeriodClock.Due(latest, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), grid) : latest;

            Volatile.Write(ref _activePeriod, period);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: could not read the current period; nothing will be recorded until the next restart.");
            return false;
        }
    }

    /// <summary>Moves every resident record into the due period once a boundary has passed.</summary>
    /// <remarks>
    /// Game thread only — it rewrites records gameplay mutates. Runs at round end and on the save
    /// timer, so a server whose rounds never end still turns over within one save interval. With
    /// periods off nothing is ever due, which freezes the period rather than starting a new one.
    /// <para>
    /// Nothing is due before the active period's <c>PeriodClock.EndOf</c>, which after a schedule
    /// change is a boundary of the new grid; that keeps period ids rising, which every older-or-newer
    /// comparison here and in modules relies on.
    /// </para>
    /// </remarks>
    public void RollOverIfDue()
    {
        if (!SchemaUsable) return;

        var grid = CurrentGrid;
        if (!grid.IsEnabled) return;

        var closed = ActivePeriod;
        var due = PeriodClock.Due(closed, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), grid);
        if (due <= closed) return;

        Volatile.Write(ref _activePeriod, due);

        var startPoints = _config.Current.Rank.StartPoints;
        var rankIndex = _ladder.IndexFor(startPoints);
        var moved = 0;

        foreach (var record in _registry.All)
        {
            if (record.Period == due) continue;

            record.StartPeriod(due, startPoints, rankIndex);
            moved++;
        }

        _logger.LogInformation(
            "Ranks: period {Closed} closed and {Opened} opened; {Count} resident record(s) moved into it.",
            closed, due, moved);

        _events.RaisePeriodChanged(new RanksPeriodChangedArgs(closed, due));
        _ = SaveAllAsync();
    }

    public void Start(bool hotReload)
    {
        _core.Event.OnClientSteamAuthorize += OnSteamAuthorized;
        _core.Event.OnClientSteamAuthorizeFail += OnSteamAuthorizeFailed;
        _core.Event.OnClientPutInServer += OnPutInServer;
        _core.Event.OnClientDisconnected += OnDisconnected;

        StartSaveTimer();

        // Load(hotReload: true) runs with players already connected, so their authorize event has
        // long since fired and waiting for another one would leave the whole server untracked.
        if (hotReload) AdoptConnectedPlayers();
    }

    public void Stop()
    {
        _core.Event.OnClientSteamAuthorize -= OnSteamAuthorized;
        _core.Event.OnClientSteamAuthorizeFail -= OnSteamAuthorizeFailed;
        _core.Event.OnClientPutInServer -= OnPutInServer;
        _core.Event.OnClientDisconnected -= OnDisconnected;

        _saveTimer?.Cancel();
        _saveTimer = null;
    }

    private void StartSaveTimer()
    {
        var interval = _config.Current.Database.SaveIntervalSeconds;
        if (interval <= 0) return;

        // RepeatBySeconds fires its first callback immediately, so the flag check inside
        // SaveAllAsync is what stops a save racing the schema probe at boot.
        _saveTimer = _core.Scheduler.RepeatBySeconds(interval, () =>
        {
            RollOverIfDue();
            _ = SaveAllAsync();
        });
    }

    private void OnSteamAuthorized(IOnClientSteamAuthorizeEvent e)
    {
        if (_core.PlayerManager.GetPlayer(e.PlayerId) is { } player) Track(player);
    }

    /// <remarks>
    /// The trigger every other plugin here uses as its primary: authorize alone leaves no trace
    /// when it does not fire, and can land before GetPlayer resolves. Track is idempotent.
    /// </remarks>
    private void OnPutInServer(IOnClientPutInServerEvent e)
    {
        if (_core.PlayerManager.GetPlayer(e.PlayerId) is { } player) Track(player);
    }

    private void Track(IPlayer player)
    {
        if (player.IsFakeClient) return;

        var steamId = player.SteamID;
        var slot = player.Slot;

        // PutInServer commonly lands a few hundred ms before Steam auth finalizes.
        if (steamId == 0)
        {
            PollForAuthorization(slot);
            return;
        }

        var name = SafeName(player);

        // Already resident: a reconnect adopts the record rather than reloading it, which is what
        // stops a fast reconnect discarding points the pending save has not written yet.
        if (_registry.BySteamId(steamId) is { } resident)
        {
            resident.Name = name;
            _registry.Adopt(slot, resident);
            _events.RaisePlayerDataLoaded(Snapshot(resident));
            return;
        }

        if (!_loading.TryAdd(steamId, 0)) return;

        var period = ActivePeriod;
        var startPoints = _config.Current.Rank.StartPoints;

        Task.Run(() =>
        {
            try
            {
                var loaded = _store.Load(steamId, period, startPoints) ?? NewRecord(steamId, name, period);

                _core.Scheduler.NextTick(() =>
                {
                    try { Publish(steamId, slot, name, loaded); }
                    finally { _loading.TryRemove(steamId, out _); }
                });
            }
            catch (Exception ex)
            {
                _loading.TryRemove(steamId, out _);

                // Deliberately leaves the player untracked. Substituting an empty record would let
                // the next absolute save wipe their real row (FR-DB-07).
                _logger.LogError(ex, "Ranks: failed to load record for {SteamId}; the player stays untracked.", steamId);
            }
        });
    }

    /// <remarks>Reads IPlayer on the game thread; doing it on the pool thread crashes the host.</remarks>
    private void PollForAuthorization(int slot)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < AuthPollAttempts; attempt++)
            {
                await Task.Delay(AuthPollIntervalMs).ConfigureAwait(false);

                var settled = await _core.Scheduler.NextTickAsync(() =>
                {
                    var player = _core.PlayerManager.GetPlayer(slot);

                    if (player is null || player.IsFakeClient) return true;
                    if (player.SteamID == 0) return false;

                    Track(player);
                    return true;
                }).ConfigureAwait(false);

                if (settled) return;
            }

            _logger.LogWarning(
                "Ranks: slot {Slot} never finished Steam authorization; that player is not tracked and earns nothing this session.",
                slot);
        });
    }

    private void Publish(ulong steamId, int slot, string name, PlayerRecord loaded)
    {
        AlignPeriod(loaded);

        loaded.Name = name;
        loaded.LastSeen = DateTime.UtcNow;
        loaded.RankIndex = _ladder.IndexFor(loaded.Points);
        loaded.Dirty = true;

        var record = _registry.Adopt(slot, loaded);

        _detail.Write("Ranks: loaded {SteamId} with {Points} points.", steamId, record.Points);
        _events.RaisePlayerDataLoaded(Snapshot(record));
    }

    /// <summary>Brings a record loaded off-thread into the period the server is in now.</summary>
    /// <remarks>
    /// A newer stored period means another server sharing the database crossed the boundary first,
    /// so this one catches up rather than trailing it. An older one means a rollover landed while
    /// the load was in flight, and the record follows everyone else across.
    /// </remarks>
    private void AlignPeriod(PlayerRecord loaded)
    {
        if (loaded.Period > ActivePeriod) RollOverIfDue();
        if (loaded.Period == ActivePeriod) return;

        var startPoints = _config.Current.Rank.StartPoints;
        loaded.StartPeriod(ActivePeriod, startPoints, _ladder.IndexFor(startPoints));
    }

    /// <remarks>
    /// Ungated at Warning: a player silently not being tracked is indistinguishable from a bug, so
    /// this is the only signal anyone gets (FR-DB-07).
    /// </remarks>
    private void OnSteamAuthorizeFailed(IOnClientSteamAuthorizeFailEvent e) =>
        _logger.LogWarning(
            "Ranks: Steam authorization failed for slot {Slot}; that player is not tracked and earns nothing this session.",
            e.PlayerId);

    /// <remarks>
    /// Must not gate on pawn validity — IsValid requires a pawn, which a disconnecting player no
    /// longer has, so the usual guard would skip every disconnect.
    /// </remarks>
    private void OnDisconnected(IOnClientDisconnectedEvent e)
    {
        var record = _registry.BySlot(e.PlayerId);
        _registry.ReleaseSlot(e.PlayerId);

        if (record is null) return;

        record.LastSeen = DateTime.UtcNow;
        record.Dirty = true;

        _ = SavePlayerAsync(record.SteamId, evictAfter: true);
    }

    private void AdoptConnectedPlayers()
    {
        foreach (var player in _core.PlayerManager.GetAllPlayers())
            if (!player.IsFakeClient && player.IsAuthorized)
                Track(player);
    }

    /// <summary>Writes one player. False when the SteamID is not tracked.</summary>
    public async Task<bool> SavePlayerAsync(ulong steamId, bool evictAfter = false)
    {
        if (!SchemaUsable) return false;

        var record = _registry.BySteamId(steamId);
        if (record is null) return false;

        var row = PlayerSnapshotRow.From(record);

        var ok = await Task.Run(() =>
        {
            try
            {
                var outcome = _store.Save([row]);
                ReportFailures(outcome);

                return outcome.Committed.Count > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: failed to save {SteamId}.", steamId);
                return false;
            }
        }).ConfigureAwait(false);

        if (ok) Settle(record, row, evictAfter);

        return ok;
    }

    /// <summary>Writes every dirty record. Coalesces — a concurrent call joins the pass in flight.</summary>
    /// <remarks>
    /// A module calling this every round end on a 64-slot server must cost one pass, not 64
    /// (FR-API-09).
    /// </remarks>
    public Task<int> SaveAllAsync()
    {
        if (!SchemaUsable) return Task.FromResult(0);

        lock (_saveGate)
        {
            if (_inFlightSave is { IsCompleted: false } running) return running;

            _inFlightSave = RunSaveAllAsync();

            return _inFlightSave;
        }
    }

    private async Task<int> RunSaveAllAsync()
    {
        var pending = new List<(PlayerRecord Record, PlayerSnapshotRow Row)>();

        foreach (var record in _registry.All)
            if (record.Dirty)
                pending.Add((record, PlayerSnapshotRow.From(record)));

        if (pending.Count == 0) return 0;

        var rows = pending.Select(p => p.Row).ToArray();

        var outcome = await Task.Run(() =>
        {
            try
            {
                var result = _store.Save(rows);
                ReportFailures(result);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: save pass failed for {Count} record(s); they stay dirty and retry.", rows.Length);
                return new SaveOutcome([], []);
            }
        }).ConfigureAwait(false);

        var committed = outcome.Committed.ToHashSet();

        foreach (var (record, row) in pending)
            if (committed.Contains(record.SteamId))
                Settle(record, row, evictAfter: false);

        _detail.Write("Ranks: saved {Count} record(s).", committed.Count);

        return committed.Count;
    }

    /// <remarks>
    /// Clears the dirty flag only when nothing changed during the write. An award landing mid-save
    /// must stay dirty, or an absolute writer would persist the pre-award value and then never
    /// revisit it.
    /// </remarks>
    private void Settle(PlayerRecord record, PlayerSnapshotRow written, bool evictAfter)
    {
        if (PlayerSnapshotRow.From(record) == written) record.Dirty = false;

        record.SettleArchives(written.Archives);

        _events.RaisePlayerDataSaved(Snapshot(record));

        if (evictAfter && _registry.BySlot(SlotOf(record.SteamId)) is null && !record.Dirty)
            _registry.Evict(record.SteamId);
    }

    private int SlotOf(ulong steamId) =>
        _core.PlayerManager.GetPlayerFromSteamId(steamId, allowUnauthorized: true)?.Slot ?? -1;

    private void ReportFailures(SaveOutcome outcome)
    {
        foreach (var (steamId, error) in outcome.Failed)
            _logger.LogError(error, "Ranks: skipped {SteamId} in this save pass; it stays dirty and retries.", steamId);
    }

    /// <summary>Runs the purge, then tells modules the window is open.</summary>
    /// <remarks>
    /// <paramref name="force"/> is true only at cold start. A hot reload passes neither force nor
    /// an empty server, which is the point — it is how an operator deploys a fix mid-session, so a
    /// purge there would freeze a live server.
    /// </remarks>
    public async Task RunMaintenanceAsync(bool force)
    {
        if (!SchemaUsable) return;

        var config = _config.Current;

        if (!force)
        {
            var hours = config.Database.MaintenanceIntervalHours;
            if (hours <= 0) return;
            if (DateTime.UtcNow - _lastMaintenance < TimeSpan.FromHours(hours)) return;
            if (_core.PlayerManager.GetAllPlayers().Any(p => !p.IsFakeClient)) return;
        }

        try
        {
            await PurgeAsync(config.Database.PurgeDays).ConfigureAwait(false);

            // Set after the pass, so a failure retries on the next eligible round end rather than
            // being marked done.
            _lastMaintenance = DateTime.UtcNow;

            _core.Scheduler.NextTick(_events.RaiseMaintenanceDue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: maintenance pass failed.");
        }
    }

    private async Task PurgeAsync(int purgeDays)
    {
        if (purgeDays <= 0) return;

        var purged = await Task.Run(() => _store.Purge(purgeDays)).ConfigureAwait(false);

        if (purged.Count == 0) return;

        _logger.LogInformation("Ranks: purged {Count} player record(s) unseen for {Days} day(s).", purged.Count, purgeDays);

        _core.Scheduler.NextTick(() =>
        {
            _registry.EvictAll(purged);
            _events.RaisePlayersPurged(purged);
        });
    }

    /// <summary>Blocking final flush with a bounded budget, for <c>Unload</c>.</summary>
    public void FlushOnUnload(TimeSpan budget)
    {
        if (!SchemaUsable) return;

        try
        {
            if (!SaveAllAsync().Wait(budget))
                _logger.LogWarning("Ranks: final save did not finish within {Seconds}s; some records may be stale.", budget.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: final save failed.");
        }
    }

    public RanksPlayerSnapshot Snapshot(PlayerRecord record)
    {
        var tier = _ladder.Tiers[Math.Clamp(record.RankIndex, 0, _ladder.Tiers.Count - 1)];

        return new RanksPlayerSnapshot(
            record.SteamId, record.Name, record.Points, tier.Name, tier.Tag, record.RankIndex,
            record.Kills, record.Deaths, record.Assists,
            record.RoundWins, record.RoundLosses, record.MatchWins, record.MatchLosses,
            record.BombsPlanted, record.BombsDefused, record.FirstSeen, record.LastSeen)
        {
            Headshots = record.Headshots,
            Hits = record.Hits,
            Misses = record.Misses,
            Period = record.Period,
        };
    }

    private PlayerRecord NewRecord(ulong steamId, string name, long period) => new()
    {
        SteamId = steamId,
        Name = name,
        Period = period,
        Points = _config.Current.Rank.StartPoints,
        FirstSeen = DateTime.UtcNow,
        LastSeen = DateTime.UtcNow,
    };

    private string SafeName(IPlayer player)
    {
        try
        {
            var name = player.Name;

            return string.IsNullOrWhiteSpace(name) ? player.SteamID.ToString() : name;
        }
        catch
        {
            return player.SteamID.ToString();
        }
    }
}
