using System.ComponentModel;
using Microsoft.Extensions.Configuration;

namespace Ranks.WeaponStats.Config;

/// <summary>What counts, and what is deliberately excluded.</summary>
internal sealed class TrackingConfig
{
    [Description("Record hits, damage and kills against a teammate. Turn this on for FFA servers, where same-team is nearly every interaction.")]
    public bool CountFriendlyFire { get; set; }
}

internal sealed class DatabaseConfig
{
    [Description("Seconds between flushes of accumulated stats. 0 disables the timer; disconnect, map change and unload still flush.")]
    public int FlushIntervalSeconds { get; set; } = 120;
}

internal sealed class DisplayConfig
{
    // TopWeapons was removed with the chat listing it capped: the menu pages instead, so a limit
    // would only hide weapons behind no affordance at all.
    [Description("Hide weapons with fewer shots than this, so one stray shot does not fill the list.")]
    public int MinShotsToList { get; set; } = 10;
}

internal sealed class GeneralConfig
{
    [Description("Narrate expected flow. Warnings and errors are never gated by this.")]
    public bool DetailedLogging { get; set; }
}

/// <summary>The module's <c>config.jsonc</c>.</summary>
internal sealed class WeaponStatsConfig
{
    public TrackingConfig Tracking { get; set; } = new();

    public DatabaseConfig Database { get; set; } = new();

    public DisplayConfig Display { get; set; } = new();

    public GeneralConfig General { get; set; } = new();

    /// <summary>Clamps out-of-range values rather than throwing — these are tuning errors.</summary>
    public void Clamp()
    {
        Database.FlushIntervalSeconds = Math.Max(0, Database.FlushIntervalSeconds);
        Display.MinShotsToList = Math.Max(0, Display.MinShotsToList);
    }
}

/// <summary>One command's operator-editable surface.</summary>
internal sealed class CommandDefinition
{
    public string Command { get; set; } = "";

    public List<string> Aliases { get; set; } = [];

    public string Permission { get; set; } = "";
}

internal sealed class CommandsConfig
{
    public const string WeaponStats = "weaponstats";

    [Description("Command names, aliases and permissions. An empty Command removes the command.")]
    public Dictionary<string, CommandDefinition> All { get; set; } = Defaults();

    private static Dictionary<string, CommandDefinition> Defaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        [WeaponStats] = new() { Command = "weaponstats", Aliases = ["ws"] },
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
