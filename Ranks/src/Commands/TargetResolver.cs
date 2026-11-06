using System.Globalization;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SteamAPI;

namespace Ranks.Commands;

/// <summary>Resolves an admin command's target — name, SteamID, status id or <c>@</c> group — to connected humans.</summary>
/// <remarks>
/// Not <c>FindTargettedPlayers</c>: it skips players without a pawn and matches nobody when the
/// sender has none, so a spectating admin found no one; and it reads a bare status id as a name.
/// </remarks>
internal static class TargetResolver
{
    public enum Outcome
    {
        Found,
        NotFound,
        Ambiguous,
    }

    public readonly record struct Match(Outcome Outcome, IReadOnlyList<IPlayer> Players);

    public static Match Resolve(ISwiftlyCore core, IPlayer? sender, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return new(Outcome.NotFound, []);

        var humans = ConnectedHumans(core);

        if (pattern.StartsWith('@')) return Group(core, sender, pattern, humans);

        var identifier = pattern.StartsWith('#') ? pattern[1..] : pattern;

        if (TryParseStatusId(identifier, out var userId)) return ExactlyOne(humans.Where(p => p.UserID == userId));
        if (TryParseSteamId(identifier, out var steamId)) return ExactlyOne(humans.Where(p => p.SteamID == steamId));

        return ByName(humans, pattern);
    }

    private static List<IPlayer> ConnectedHumans(ISwiftlyCore core) =>
        core.PlayerManager.GetAllPlayers().Where(p => p.Controller is { IsValid: true } && !p.IsFakeClient).ToList();

    /// <remarks>The id <c>status</c> prints is the engine userid. A SteamID64 overflows int and falls through.</remarks>
    private static bool TryParseStatusId(string identifier, out int userId) =>
        int.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out userId);

    private static bool TryParseSteamId(string identifier, out ulong steamId)
    {
        var parsed = new CSteamID(identifier);
        steamId = parsed.IsValid() ? parsed.GetSteamID64() : 0;
        return steamId != 0;
    }

    /// <remarks>An exact name wins, so a player whose whole name is part of another's stays reachable.</remarks>
    private static Match ByName(List<IPlayer> humans, string pattern)
    {
        var exact = humans.Where(p => string.Equals(p.Name, pattern, StringComparison.OrdinalIgnoreCase)).ToList();

        return ExactlyOne(exact.Count > 0
            ? exact
            : humans.Where(p => p.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)));
    }

    /// <remarks>Several matches are refused, not all applied: these commands overwrite or wipe a record.</remarks>
    private static Match ExactlyOne(IEnumerable<IPlayer> candidates)
    {
        var players = candidates.Take(2).ToList();

        return players.Count switch
        {
            0 => new(Outcome.NotFound, []),
            1 => new(Outcome.Found, players),
            _ => new(Outcome.Ambiguous, []),
        };
    }

    /// <remarks><c>@aim</c> stays the framework's: it needs the engine's trace from the sender's pawn.</remarks>
    private static Match Group(ISwiftlyCore core, IPlayer? sender, string selector, List<IPlayer> humans)
    {
        IEnumerable<IPlayer> members = selector.ToLowerInvariant() switch
        {
            "@me" => humans.Where(p => p.Slot == sender?.Slot),
            "@all" or "@human" or "@!bots" => humans,
            "@t" => humans.Where(p => p.Controller.TeamNum == (int)Team.T),
            "@ct" => humans.Where(p => p.Controller.TeamNum == (int)Team.CT),
            "@spec" => humans.Where(p => p.Controller.TeamNum == (int)Team.Spectator),
            "@alive" => humans.Where(p => p.IsAlive),
            "@dead" => humans.Where(p => p is { IsValid: true, IsAlive: false }),
            "@aim" when sender is not null =>
                core.PlayerManager.FindTargettedPlayers(sender, "@aim", TargetSearchMode.IncludeSelf | TargetSearchMode.NoBots),
            _ => [],
        };

        var players = members.ToList();

        return new(players.Count > 0 ? Outcome.Found : Outcome.NotFound, players);
    }
}
