using System.ComponentModel;
using Microsoft.Extensions.Configuration;

namespace Ranks.Prestige.Config;

/// <summary>One prestige step: the points it opens at, and how the scoreboard renders it.</summary>
/// <remarks>
/// <see cref="MinimumPoints"/> doubles as the deduction. A player at this level is shown
/// <c>points - MinimumPoints</c>, so crossing the threshold resets the visible total to zero and
/// the ladder starts again — which is the whole point of prestige.
/// </remarks>
public sealed class PrestigeLevel
{
    [Description("Prestige number shown to the player. Must be unique and above zero.")]
    public int Level { get; set; }

    [Description("Stored points at which this level opens, and the amount deducted from the display.")]
    public int MinimumPoints { get; set; }

    [Description("Scoreboard mode for this level: Premier, Competitive, Wingman, Custom. Empty keeps core's configured mode.")]
    public string RankMode { get; set; } = string.Empty;
}

public sealed class PrestigeConfig
{
    [Description("Turns the whole module off without uninstalling it. Existing overlays are cleared on the next load.")]
    public bool Enabled { get; set; } = true;

    [Description("Prepended to core's clan tag, with {0} replaced by the level. Empty writes no prefix.")]
    public string TagPrefix { get; set; } = "[P{0}] ";

    [Description("Prestige steps. Order does not matter; the highest level a player's points reach wins.")]
    public List<PrestigeLevel> Levels { get; set; } =
    [
        new() { Level = 1, MinimumPoints = 40000 },
        new() { Level = 2, MinimumPoints = 80000 },
        new() { Level = 3, MinimumPoints = 120000 },
        new() { Level = 4, MinimumPoints = 160000 },
        new() { Level = 5, MinimumPoints = 200000 },
    ];

    [Description("Narrates expected flow. Warnings and errors print regardless.")]
    public bool DetailedLogging { get; set; } = false;
}

public sealed class CommandDefinition
{
    public string Command { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = [];

    public string Permission { get; set; } = string.Empty;
}

public sealed class CommandsConfig
{
    public const string Prestige = "prestige";

    [Description("Command names, aliases and permissions. An empty Command removes the command.")]
    public Dictionary<string, CommandDefinition> All { get; set; } = Defaults();

    private static Dictionary<string, CommandDefinition> Defaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        [Prestige] = new() { Command = "prestige", Aliases = ["myprestige"] },
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
