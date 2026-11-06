namespace Ranks.CustomCombos.Contract;

/// <summary>One player's lifetime count for a single combo.</summary>
/// <remarks>
/// <paramref name="ComboKey"/> is the operator's own config key, so the set of keys is
/// server-specific and a consumer must treat an unfamiliar one as ordinary data rather than an
/// error. <paramref name="LastAt"/> is UTC.
/// </remarks>
public sealed record ComboStatRow(
    string ComboKey,
    long Count,
    DateTime LastAt);

/// <summary>Read access to <c>Ranks_CustomCombos</c>'s per-combo counters.</summary>
/// <remarks>
/// Additive only after 1.0, for the same reason as <c>Ranks.Contract</c>: there is no package
/// registry and therefore no version negotiation, so a renamed or re-signed member is a
/// <c>MissingMethodException</c> at call time rather than a build error anyone sees.
///
/// Every method is async because these totals live in the database — and every one includes the
/// caller's unflushed counts, so a figure read here always matches what <c>!combos</c> would print.
/// </remarks>
public interface ICustomCombosApi
{
    /// <summary>The shared-interface key consumers bind against.</summary>
    const string InterfaceKey = "Ranks.CustomCombos.Api.v1";

    /// <summary>Every combo the player has landed, highest count first.</summary>
    Task<IReadOnlyList<ComboStatRow>> GetCombosAsync(ulong steamId);

    /// <summary>One combo's count, or null when the player has never landed it.</summary>
    /// <remarks>Spares a caller rendering one combo the cost of fetching the whole set.</remarks>
    Task<ComboStatRow?> GetComboAsync(ulong steamId, string comboKey);

    /// <summary>The combo keys currently configured, in the order they resolve.</summary>
    /// <remarks>
    /// Synchronous because it reads the loaded rule set, not the database. Lets a consumer render
    /// a combo an operator has configured but nobody has landed yet, which no counter row covers.
    /// </remarks>
    IReadOnlyList<string> ConfiguredCombos { get; }

    /// <summary>The name <c>!combos</c> shows for a combo: its <c>combo.&lt;key&gt;</c> translation without colour tokens, else the key.</summary>
    /// <remarks>Synchronous: it reads the loaded translations, not the database.</remarks>
    string GetComboName(string comboKey);
}
