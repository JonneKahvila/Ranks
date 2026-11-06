using Microsoft.Extensions.Logging;
using Ranks.Contract;

namespace Ranks.Api;

/// <summary>The one place core's API events are held and raised.</summary>
/// <remarks>
/// Separate from the API implementation so core can raise an event without depending on the type
/// that publishes it, and so the per-subscriber guard lives in one place: a module that throws is
/// logged and skipped, never allowed to stop scoring (NFR-06).
/// </remarks>
internal sealed class RanksEventHub
{
    private readonly ILogger _logger;

    public RanksEventHub(ILogger logger) => _logger = logger;

    public event Action<RanksPlayerSnapshot>? PlayerDataLoaded;

    public event Action<RanksPointsChangingArgs>? PointsChanging;

    public event Action<RanksPointsChangedArgs>? PointsChanged;

    public event Action<RanksRankChangedArgs>? RankChanged;

    public event Action<RanksPlayerSnapshot>? PlayerDataSaved;

    public event Action<IReadOnlyList<ulong>>? PlayersPurged;

    public event Action? MaintenanceDue;

    public event Action? CoreReady;

    public event Action<RanksPeriodChangedArgs>? PeriodChanged;

    public event Action<RanksPlayerResetArgs>? PlayerReset;

    public void RaisePlayerDataLoaded(RanksPlayerSnapshot snapshot) => Raise(PlayerDataLoaded, snapshot, nameof(PlayerDataLoaded));

    public void RaisePlayerDataSaved(RanksPlayerSnapshot snapshot) => Raise(PlayerDataSaved, snapshot, nameof(PlayerDataSaved));

    public void RaisePointsChanged(RanksPointsChangedArgs args) => Raise(PointsChanged, args, nameof(PointsChanged));

    public void RaiseRankChanged(RanksRankChangedArgs args) => Raise(RankChanged, args, nameof(RankChanged));

    public void RaisePlayersPurged(IReadOnlyList<ulong> steamIds) => Raise(PlayersPurged, steamIds, nameof(PlayersPurged));

    public void RaiseMaintenanceDue() => Raise(MaintenanceDue, nameof(MaintenanceDue));

    public void RaiseCoreReady() => Raise(CoreReady, nameof(CoreReady));

    public void RaisePeriodChanged(RanksPeriodChangedArgs args) => Raise(PeriodChanged, args, nameof(PeriodChanged));

    public void RaisePlayerReset(RanksPlayerResetArgs args) => Raise(PlayerReset, args, nameof(PlayerReset));

    /// <summary>Offers an award to subscribers and returns what they made of it.</summary>
    /// <remarks>
    /// Subscribers run in subscription order and each sees the previous one's Amount, so scaling
    /// composes without core arbitrating anything. A subscriber that throws is treated as having
    /// made no change — its mutation may be half-applied, so the pre-call value is restored.
    /// </remarks>
    public RanksPointsChangingArgs RaisePointsChanging(RanksPointsChangingArgs args)
    {
        if (PointsChanging is null) return args;

        foreach (var handler in PointsChanging.GetInvocationList().Cast<Action<RanksPointsChangingArgs>>())
        {
            var amount = args.Amount;
            var cancel = args.Cancel;

            try
            {
                handler(args);
            }
            catch (Exception ex)
            {
                args.Amount = amount;
                args.Cancel = cancel;
                _logger.LogError(ex, "Ranks: a PointsChanging subscriber threw; its changes were discarded.");
            }
        }

        return args;
    }

    private void Raise<T>(Action<T>? handlers, T payload, string name)
    {
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: a {Event} subscriber threw.", name);
            }
        }
    }

    private void Raise(Action? handlers, string name)
    {
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: a {Event} subscriber threw.", name);
            }
        }
    }

    /// <remarks>Cleared on unload, or handlers accumulate across the handshake rebuild (FR-API-04).</remarks>
    public void Clear()
    {
        PlayerDataLoaded = null;
        PointsChanging = null;
        PointsChanged = null;
        RankChanged = null;
        PlayerDataSaved = null;
        PlayersPurged = null;
        MaintenanceDue = null;
        CoreReady = null;
        PeriodChanged = null;
        PlayerReset = null;
    }
}
