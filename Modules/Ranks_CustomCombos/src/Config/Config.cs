using System.ComponentModel;
using Microsoft.Extensions.Configuration;

namespace Ranks.CustomCombos.Config;

/// <summary>This module's settings. Bound off core's configuration root and live-reloaded.</summary>
internal sealed class CombosConfig
{
    [Description("Replace core's kill award with the matched combo instead of adding to it. A kill matching no combo is unaffected.")]
    public bool SuppressCoreAwards { get; set; } = false;

    [Description("The combos themselves. Key is the combo's identity: it becomes the award reason, the translation key combo.<key>, and the database row key.")]
    public Dictionary<string, ComboEntry> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DatabaseOptions Database { get; set; } = new();

    public GeneralOptions General { get; set; } = new();

    public void Clamp()
    {
        Database.FlushIntervalSeconds = Math.Max(15, Database.FlushIntervalSeconds);

        foreach (var entry in Rules.Values)
            entry.Count = Math.Max(1, entry.Count);
    }
}

/// <summary>One combo: what must be true, and what it pays.</summary>
internal sealed class ComboEntry
{
    [Description("Every attribute here must be present. See the README for the vocabulary.")]
    public List<string> RequiresAll { get; set; } = [];

    [Description("None of these may be present.")]
    public List<string> RequiresNone { get; set; } = [];

    [Description("Matching kills needed on the same tick. 1 is an ordinary single kill; 3 is a grenade triple. Cannot be combined with SuppressCoreAwards.")]
    public int Count { get; set; } = 1;

    [Description("Points awarded. Added on top of core's kill award unless SuppressCoreAwards is on.")]
    public int Points { get; set; }

    [Description("Translation key for the chat line. Defaults to combo.<key>.")]
    public string? Message { get; set; }
}

internal sealed class DatabaseOptions
{
    [Description("How often accumulated combo counts are written. Minimum 15.")]
    public int FlushIntervalSeconds { get; set; } = 120;
}

internal sealed class GeneralOptions
{
    [Description("Narrates expected flow. Warnings and errors are never suppressed by it.")]
    public bool DetailedLogging { get; set; } = false;
}

internal sealed class CommandsConfig
{
    public const string Combos = "combos";

    [Description("Command names, aliases and permissions. An empty Command removes the command.")]
    public Dictionary<string, CommandDefinition> All { get; set; } = Defaults();

    private static Dictionary<string, CommandDefinition> Defaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        [Combos] = new() { Command = "combos", Aliases = ["mycombos"] },
    };

    /// <summary>Reads <c>commands.jsonc</c>: an entry in the file replaces its default whole.</summary>
    /// <remarks>
    /// Not <c>Get&lt;CommandsConfig&gt;()</c>: the binder appends a file's array onto a non-empty
    /// list, so a renamed alias registered beside the shipped one instead of replacing it. The
    /// defaults still seed the generated file, and stand in for an entry the file lacks.
    /// </remarks>
    public static CommandsConfig Bind(IConfiguration section)
    {
        var config = new CommandsConfig { All = new(StringComparer.OrdinalIgnoreCase) };
        section.Bind(config);
        foreach (var (key, definition) in Defaults())
            config.All.TryAdd(key, definition);
        return config;
    }
}

internal sealed class CommandDefinition
{
    public string Command { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = [];

    public string Permission { get; set; } = string.Empty;
}
