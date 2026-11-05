using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using SwiftlyS2.Shared;

namespace Ranks.Config;

/// <summary>Binds one config file and keeps a snapshot current across edits.</summary>
/// <remarks>
/// Binds straight off <c>Core.Configuration.Manager</c> — the framework already owns the
/// configuration root, the file watching and the reload token, so there is no ServiceCollection,
/// no IOptionsMonitor and no provider to dispose here.
/// </remarks>
internal sealed class ConfigHost<T> : IDisposable
    where T : class, new()
{
    private readonly string _fileName;
    private readonly string _sectionName;
    private readonly IConfiguration _root;
    private readonly Func<IConfiguration, T> _bind;
    private readonly Action<T, ILogger>? _validate;
    private readonly ILogger _logger;
    private readonly IDisposable? _subscription;

    private volatile T _current;
    private string _lastApplied;

    private ConfigHost(ISwiftlyCore core, string fileName, string sectionName, Func<IConfiguration, T> bind, Action<T, ILogger>? validate,
        ILogger logger, T initial, string fingerprint)
    {
        _fileName = fileName;
        _sectionName = sectionName;
        _root = core.Configuration.Manager;
        _bind = bind;
        _validate = validate;
        _logger = logger;
        _current = initial;
        _lastApplied = fingerprint;

        _subscription = ChangeToken.OnChange(_root.GetReloadToken, Apply);
    }

    /// <summary>The snapshot every read goes through, including hot paths.</summary>
    /// <remarks>An atomic reference swap, so a hot path never pays for a lookup or a re-bind.</remarks>
    public T Current => _current;

    /// <summary>Creates the file if absent, binds it, and validates. Throws if it cannot be used.</summary>
    /// <param name="bind">Reads the section; for a model whose collection defaults the plain binder would append to.</param>
    public static ConfigHost<T> Create(
        ISwiftlyCore core,
        ILogger logger,
        string fileName,
        string sectionName,
        Action<T, ILogger>? validate = null,
        Func<IConfiguration, T>? bind = null)
    {
        core.Configuration
            // addMissingKeys: a plugin upgrade inserts new keys into an existing file with their
            // defaults and Description comments, leaving the operator's values and formatting be.
            .InitializeJsonWithModel<T>(fileName, sectionName, true)
            // The full path matters: AddJsonFile resolves relative to the process working
            // directory, not the plugin folder, so a bare name silently binds the wrong file.
            .Configure(builder => builder.AddJsonFile(
                core.Configuration.GetConfigPath(fileName), optional: false, reloadOnChange: true));

        bind ??= section => section.Get<T>() ?? new T();
        var initial = bind(core.Configuration.Manager.GetSection(sectionName));

        // Taken before validation, because validation mutates (it dedupes and clamps). Apply()
        // fingerprints the raw bind, so fingerprinting the validated one here would leave the two
        // describing different shapes, and the next save of ANY file would re-apply this host and
        // log a reload nobody asked for.
        var fingerprint = Fingerprint(initial);

        validate?.Invoke(initial, logger);

        return new ConfigHost<T>(core, fileName, sectionName, bind, validate, logger, initial, fingerprint);
    }

    /// <summary>Re-binds on a file edit, ignoring notifications that carry no change.</summary>
    /// <remarks>
    /// Every file shares one configuration root and therefore one reload token, so a save to any
    /// of them notifies all four hosts; editors also truncate-then-write, so one save arrives two
    /// or three times. The fingerprint is what turns that into a single apply.
    /// </remarks>
    private void Apply()
    {
        T incoming;

        try
        {
            incoming = _bind(_root.GetSection(_sectionName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: could not re-read {File}; keeping the previous config.", _fileName);
            return;
        }

        var fingerprint = Fingerprint(incoming);
        if (fingerprint == _lastApplied) return;
        _lastApplied = fingerprint;

        if (!TryValidate(incoming)) return;

        _current = incoming;
        _logger.LogWarning("Ranks: {File} reloaded.", _fileName);
    }

    /// <summary>Validates a reloaded config, keeping the previous one if it is unusable.</summary>
    /// <remarks>
    /// Deliberately not the same response as the initial load, which throws. A bad edit at boot
    /// should stop the plugin; the same edit at runtime should leave the server playing on the
    /// last good values.
    /// </remarks>
    private bool TryValidate(T incoming)
    {
        try
        {
            _validate?.Invoke(incoming, _logger);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: reloaded {File} is invalid; keeping the previous config.", _fileName);
            return false;
        }
    }

    /// <remarks>
    /// Fingerprints the bound model rather than the file bytes, so reformatting or a comment edit
    /// is correctly a no-op and another file's save cannot be mistaken for this one.
    /// </remarks>
    private static string Fingerprint(T config) => JsonSerializer.Serialize(config);

    public void Dispose() => _subscription?.Dispose();
}

/// <summary>The four files core loads, and the one place their names and sections live.</summary>
/// <remarks>
/// Section names are distinct per file on purpose. Every bound model shares one configuration
/// root, so two files declaring the same root key would merge into one section and each binding
/// would silently see the other file's keys — which is why the ladder sits under "Ladder" rather
/// than the "Ranks" its filename suggests.
/// </remarks>
internal static class ConfigFiles
{
    private const string LadderFile = "ranks.jsonc";

    public static ConfigHost<RanksConfig> Main(ISwiftlyCore core, ILogger logger) =>
        ConfigHost<RanksConfig>.Create(core, logger, "config.jsonc", "Ranks", ConfigValidator.Validate, RanksConfig.Bind);

    public static ConfigHost<PointsConfig> Points(ISwiftlyCore core, ILogger logger) =>
        ConfigHost<PointsConfig>.Create(core, logger, "points.jsonc", "Points");

    public static ConfigHost<CommandsConfig> Commands(ISwiftlyCore core, ILogger logger) =>
        ConfigHost<CommandsConfig>.Create(core, logger, "commands.jsonc", "Commands", CommandsConfig.Validate, CommandsConfig.Bind);

    public static ConfigHost<LadderConfig> Ladder(ISwiftlyCore core, ILogger logger)
    {
        SeedLadderFile(core, logger);

        return ConfigHost<LadderConfig>.Create(core, logger, LadderFile, "Ladder", LadderConfig.Validate);
    }

    /// <summary>Writes the shipped ladder on first run, before the binder ever sees the file.</summary>
    /// <remarks>
    /// Seeded here rather than through the model template because the binder appends array entries
    /// onto a non-empty collection initializer. Writing the file and leaving the model's list empty
    /// is what makes the operator's ladder the only one that binds.
    /// </remarks>
    private static void SeedLadderFile(ISwiftlyCore core, ILogger logger)
    {
        var path = core.Configuration.GetConfigPath(LadderFile);

        if (File.Exists(path)) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, LadderSeed.FileContents());

            logger.LogInformation("Ranks: wrote the default rank ladder to {File}.", LadderFile);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ranks: could not write {File}; the shipped ladder will be used in memory.", LadderFile);
        }
    }
}
