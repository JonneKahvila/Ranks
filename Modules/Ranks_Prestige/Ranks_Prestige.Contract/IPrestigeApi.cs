namespace Ranks.Prestige.Contract;

/// <summary>Where a player stands on the prestige ladder.</summary>
/// <param name="Level">0 before the first level is reached.</param>
/// <param name="Points">The player's stored points, which every level threshold is measured against.</param>
/// <param name="NextLevelPoints">The next level's threshold, or null at the top level.</param>
public sealed record PrestigeProgress(int Level, int Points, int? NextLevelPoints);

/// <summary>Read access to <c>Ranks_Prestige</c>'s levels.</summary>
/// <remarks>
/// Additive only after 1.0, for the same reason as <c>Ranks.Contract</c>: there is no package
/// registry and therefore no version negotiation, so a renamed or re-signed member is a
/// <c>MissingMethodException</c> at call time rather than a build error anyone sees.
/// </remarks>
public interface IPrestigeApi
{
    /// <summary>The shared-interface key consumers bind against.</summary>
    const string InterfaceKey = "Ranks.Prestige.Api.v1";

    /// <summary>False when the operator switched prestige off.</summary>
    bool IsEnabled { get; }

    /// <summary>A player's standing, or null while prestige is off, Ranks is absent, or the player has no loaded record.</summary>
    /// <remarks>Synchronous: it reads core's resident record, the same figure <c>!prestige</c> shows.</remarks>
    PrestigeProgress? GetProgress(ulong steamId);
}
