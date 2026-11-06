using Ranks.Api;
using Ranks.Contract;
using Ranks.Ranking;
using Ranks.Scoreboard;

namespace Ranks.Players;

/// <summary>Wipes one player's current period, from either the command or the Settings menu.</summary>
/// <remarks>
/// Shared so the two entry points cannot drift. Closed periods are history and stay — a reset
/// clears what the player is working on now, not what a past leaderboard already recorded.
/// </remarks>
internal sealed class PlayerReset
{
    private readonly RankLadder _ladder;
    private readonly PlayerSessions _sessions;
    private readonly ScoreboardService _scoreboard;
    private readonly RanksEventHub _events;

    public PlayerReset(RankLadder ladder, PlayerSessions sessions, ScoreboardService scoreboard, RanksEventHub events)
    {
        _ladder = ladder;
        _sessions = sessions;
        _scoreboard = scoreboard;
        _events = events;
    }

    /// <remarks>
    /// Modules are told in the same pass: they hold their own figures for the player, and without
    /// the event a "reset" player kept every weapon row and, under a rating module, had the old
    /// rating written straight back at the next match end.
    /// </remarks>
    public void Apply(PlayerRecord record)
    {
        record.ClearProgress(0, _ladder.IndexFor(0));

        // Saved immediately so a disconnect before the next autosave cannot resurrect the old row.
        _ = _sessions.SavePlayerAsync(record.SteamId);
        _scoreboard.Apply(record.SteamId);
        _events.RaisePlayerReset(new RanksPlayerResetArgs(record.SteamId));
    }
}
