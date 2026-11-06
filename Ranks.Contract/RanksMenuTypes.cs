using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Players;

namespace Ranks.Contract;

/// <summary>The branches of core's menu a module may hang an entry from.</summary>
/// <remarks>
/// Plain strings rather than an enum: the contract is additive-only after 1.0, and a new member on
/// a shared enum changes the type's layout for every module already compiled against it.
/// An unrecognised section is dropped rather than invented, so a module targeting a newer core
/// degrades to "entry missing" instead of appearing somewhere nobody designed.
/// </remarks>
public static class RanksMenuSections
{
    /// <summary>Under <c>Statistics</c>, beside core's own counters.</summary>
    public const string Statistics = "statistics";

    /// <summary>Under <c>Settings</c>, beside core's own preferences.</summary>
    public const string Settings = "settings";

    /// <summary>Under <c>Leaderboard</c>, beside core's own lists.</summary>
    /// <remarks>
    /// With no entry here and periods disabled, core's Leaderboard row still opens its list directly,
    /// so a server without leaderboard modules sees no extra screen.
    /// </remarks>
    public const string Leaderboard = "leaderboard";

    /// <summary>A top-level row on the root menu. Use sparingly — the root is the one screen every player sees.</summary>
    public const string Root = "root";
}

/// <summary>One line a module contributes to core's menu.</summary>
/// <remarks>
/// <para>
/// The module hands over a <em>factory</em>, not rows: core calls <see cref="Build"/> when the
/// player opens the parent and nests whatever comes back, so the module owns its own layout,
/// paging and colours. A menu with no parent of its own is adopted by the entry that opened it,
/// which is what makes the player's back key return to core's menu rather than close everything.
/// </para>
/// <para>
/// <see cref="Build"/> runs on the game thread and must not block. Load inside a
/// <c>PagedCollectionComponent</c>, which is built for exactly this and is how core's own
/// leaderboard reads the database.
/// </para>
/// </remarks>
public sealed record RanksMenuEntry
{
    /// <summary>One of the <see cref="RanksMenuSections"/> constants.</summary>
    public required string Section { get; init; }

    /// <summary>Already localized — core shows it verbatim, because core ships no key for it.</summary>
    public required string Label { get; init; }

    /// <summary>Returns the menu to nest, or null to render the row greyed out.</summary>
    public required Func<IPlayer, IMenu?> Build { get; init; }

    /// <summary>Ascending; core's own entries sit at 0, so a negative value sorts above them.</summary>
    public int Order { get; init; }
}
