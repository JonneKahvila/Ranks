using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ranks.Config;

namespace Ranks.Database;

/// <summary>Applies the operator's prefix and suffix to every identifier core creates.</summary>
/// <remarks>
/// One resolver for tables, indexes, constraints and migration ledgers alike — a name that
/// skips it targets an unprefixed table, which on a shared database silently operates on
/// nothing or on another server's data. Affixes are restart-only (NFR-08): changing them
/// points the server at a different table set and migrates nothing.
/// </remarks>
internal sealed partial class TableNames
{
    /// <remarks>
    /// PostgreSQL's limit, and the binding one: MySQL allows 64 and PostgreSQL truncates at 63
    /// <em>silently</em>, which can merge two modules' ledgers into one and recreate exactly the
    /// collision a per-plugin ledger exists to prevent.
    /// </remarks>
    private const int MaxIdentifierLength = 63;

    private const string CoreLedgerBaseName = "ranks_migrations";

    private readonly string _prefix;
    private readonly string _suffix;

    public TableNames(DatabaseOptions database)
    {
        _prefix = NormalizePrefix(database.TablePrefix);
        _suffix = NormalizeSuffix(database.TableSuffix);
    }

    /// <summary>The migration ledger for core's own schema, bare like any name a migration uses.</summary>
    /// <remarks>AffixingProcessor affixes it. Affixing it here as well doubled the affixes before 1.2.0.</remarks>
    public string CoreLedger => CoreLedgerBaseName;

    /// <summary>False when the operator configured neither affix, so there is nothing to verify.</summary>
    public bool HasAffixes => _prefix.Length > 0 || _suffix.Length > 0;

    public string Resolve(string baseName) => Fit($"{_prefix}{baseName}{_suffix}");

    /// <summary>Whether an existing identifier already carries the configured affixes.</summary>
    /// <remarks>
    /// Used to detect tables created by raw <c>Execute.Sql</c>, which no decorator can rewrite.
    /// A folded name is accepted on its prefix alone — <see cref="Fit"/> replaces the tail, so the
    /// suffix is gone by construction and demanding it would report every long name as unaffixed.
    /// </remarks>
    public bool CarriesAffixes(string identifier)
    {
        if (!identifier.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) return false;

        return identifier.Length >= MaxIdentifierLength
            || identifier.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The migration ledger for a module, derived from its plugin id.</summary>
    /// <remarks>
    /// Derived from the <c>[PluginMetadata]</c> id, never the folder name — they match by
    /// convention, and a module that ever diverges would otherwise get a ledger belonging to
    /// something else (FR-DB-05).
    /// </remarks>
    public string LedgerFor(string pluginId) => $"{CoreLedgerBaseName}_{Slug(pluginId)}";

    /// <summary>Where builds before 1.2.0 kept <paramref name="ledger"/>: affixed here and again by the processor.</summary>
    public string LegacyLedger(string ledger) => Resolve(Resolve(ledger));

    /// <summary>Caps an identifier at a length every supported engine accepts.</summary>
    /// <remarks>
    /// Folds the tail into a deterministic hash rather than letting the engine cut it. Apply it
    /// to index names independently of table names: an index is its ledger's name plus eleven
    /// characters, so a ledger that fits can still produce an index that does not.
    /// </remarks>
    public static string Fit(string identifier)
    {
        if (identifier.Length <= MaxIdentifierLength) return identifier;

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier)));
        var tail = digest[..8].ToLowerInvariant();

        return string.Concat(identifier.AsSpan(0, MaxIdentifierLength - tail.Length - 1), "_", tail);
    }

    /// <remarks>
    /// The separator is the plugin's, not the operator's, so "awp", "awp_" and "_awp" all produce
    /// one name. Without that, an operator who sets "awp", accumulates data, then "fixes" it to
    /// "awp_" is silently pointed at an empty table set — indistinguishable from every player
    /// vanishing.
    /// </remarks>
    private static string NormalizePrefix(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : $"{value.Trim().Trim('_')}_";

    private static string NormalizeSuffix(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : $"_{value.Trim().Trim('_')}";

    private static string Slug(string pluginId)
    {
        var collapsed = NonAlphanumeric().Replace(pluginId.ToLowerInvariant(), "_").Trim('_');

        return collapsed.StartsWith("ranks_", StringComparison.Ordinal) ? collapsed[6..] : collapsed;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
