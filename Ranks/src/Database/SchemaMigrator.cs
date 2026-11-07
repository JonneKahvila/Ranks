using System.Data;
using System.Data.Common;
using System.Reflection;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Processors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;

namespace Ranks.Database;

/// <summary>Runs core's migrations and every registered module's, each against its own ledger.</summary>
/// <remarks>
/// Core owns the runner because affix application is the whole point of FR-DB-03 and FluentMigrator
/// offers no table-name convention to intercept. A module registering its assembly is also the only
/// shape that works in time: a module's Load() runs before the handshake, so it has no IRanksApi to
/// ask for a table name at the moment it would need one.
/// </remarks>
internal sealed class SchemaMigrator
{
    private readonly ISwiftlyCore _core;
    private readonly DatabaseConnection _database;
    private readonly TableNames _tables;
    private readonly ILogger _logger;

    /// <remarks>
    /// The handshake re-runs on every plugin load and unload, so modules re-register constantly.
    /// MigrateUp() on an up-to-date ledger is cheap but not free.
    /// </remarks>
    private readonly HashSet<(Assembly Assembly, string PluginId)> _migrated = [];

    private readonly List<(Assembly Assembly, string PluginId)> _pending = [];

    public SchemaMigrator(ISwiftlyCore core, DatabaseConnection database, TableNames tables, ILogger logger)
    {
        _core = core;
        _database = database;
        _tables = tables;
        _logger = logger;
    }

    /// <summary>Queues a module's assembly. Idempotent — safe to call from every handshake.</summary>
    public void Register(Assembly assembly, string pluginId)
    {
        var key = (assembly, pluginId);

        if (_migrated.Contains(key) || _pending.Contains(key)) return;

        _pending.Add(key);
    }

    /// <summary>Migrates core's own schema. Throws — the caller decides what a failure means.</summary>
    public void MigrateCore()
    {
        WarnOnExportedMigrator();
        Run(typeof(SchemaMigrator).Assembly, _tables.CoreLedger, "Ranks");
    }

    /// <summary>Migrates every queued module, isolating each failure.</summary>
    /// <remarks>
    /// Core has taken on running third-party code (FR-DB-05 req. 9); an unhandled throw from one
    /// module would otherwise take core's schema and every other module down with it.
    /// </remarks>
    public void MigratePendingModules()
    {
        if (_pending.Count == 0) return;

        foreach (var (assembly, pluginId) in _pending.ToArray())
        {
            var context = new ModuleMigrationContext(assembly);

            try
            {
                Run(context.LoadModule(LocateModule(assembly, pluginId)), _tables.LedgerFor(pluginId), pluginId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ranks: migrations for module {PluginId} failed; core and other modules are unaffected.", pluginId);
            }
            finally
            {
                context.Unload();
                _migrated.Add((assembly, pluginId));
            }
        }

        _pending.Clear();
    }

    private void Run(Assembly assembly, string ledger, string pluginId)
    {
        AdoptLegacyLedger(ledger);

        var before = ListTables();

        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                ConfigureEngine(rb);
                rb.WithGlobalConnectionString(MigratorConnectionString())
                  .WithVersionTable(new AffixedVersionTable(ledger))
                  .ScanIn(assembly).For.Migrations();
            })
            .AddLogging(lb => lb.AddFluentMigratorConsole());

        DecorateProcessor(services);

        var provider = services.BuildServiceProvider(false);

        try
        {
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        }
        finally
        {
            // Leaving it alive leaks the processor and its connection; on SQLite it holds the
            // database file open across hot reload.
            provider.Dispose();
        }

        VerifyAffixes(before, pluginId);
    }

    /// <summary>Moves a ledger off the name builds before 1.2.0 gave it by affixing it twice.</summary>
    /// <remarks>
    /// Before the runner reads it: under the old name a prefixed install sees an empty ledger and
    /// runs every migration again. Throws when the tables cannot be read, rather than assume there
    /// is nothing to move.
    /// </remarks>
    private void AdoptLegacyLedger(string ledger)
    {
        var legacy = _tables.LegacyLedger(ledger);
        var current = _tables.Resolve(ledger);
        if (string.Equals(legacy, current, StringComparison.OrdinalIgnoreCase)) return;

        using var connection = _database.Create();
        connection.Open();

        switch (DatabaseTables.RenameIfOnlyOld(connection, Driver, legacy, current))
        {
            case TableRename.Renamed:
                _logger.LogInformation("Ranks: renamed migration ledger {Legacy} to {Current}.", legacy, current);
                break;

            case TableRename.BothExist:
                _logger.LogWarning(
                    "Ranks: migration ledgers {Legacy} and {Current} both exist; the second is used. " +
                    "Drop the first once you no longer need it.",
                    legacy, current);
                break;
        }
    }

    /// <summary>The module's assembly file, which <see cref="ModuleMigrationContext"/> loads a second time.</summary>
    /// <remarks>
    /// SwiftlyS2 loads plugins from memory, so Location is empty. Its plugin manager matches a
    /// metadata id or a folder name, and a module's folder is named after its assembly.
    /// </remarks>
    private string LocateModule(Assembly assembly, string pluginId)
    {
        if (!string.IsNullOrEmpty(assembly.Location)) return assembly.Location;

        var name = assembly.GetName().Name!;

        foreach (var id in new[] { pluginId, name })
        {
            if (_core.PluginManager.GetPluginPath(id) is not { } directory) continue;

            var path = Path.Combine(directory, $"{name}.dll");
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException($"Ranks: cannot find {name}.dll for module {pluginId}.", $"{name}.dll");
    }

    /// <remarks>
    /// Older releases exported FluentMigrator, and unzipping an update over one leaves the files
    /// behind. SwiftlyS2 forces every export onto every plugin, so they keep breaking other plugins'
    /// migrations while Ranks itself runs fine.
    /// </remarks>
    private void WarnOnExportedMigrator()
    {
        string[] stale;

        try { stale = Directory.GetFiles(Path.Combine(_core.PluginPath, "resources", "exports"), "FluentMigrator*.dll"); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        if (stale.Length == 0) return;

        _logger.LogError(
            "Ranks: delete {Files} from Ranks/resources/exports and restart the server. SwiftlyS2 " +
            "forces exported assemblies onto every plugin, so these replace the FluentMigrator other " +
            "plugins ship and break their migrations (VIPCore: \"Method 'get_Quoter' ... does not have " +
            "an implementation\").",
            string.Join(", ", stale.Select(Path.GetFileName)));
    }

    /// <summary>The named connection's string, with SwiftlyS2's MySQL timeout key corrected.</summary>
    /// <remarks>
    /// <c>DatabaseConnectionInfo.ToString()</c> writes Npgsql's <c>Timeout=</c> for mysql too, and
    /// MySqlConnector throws "Option 'timeout' not supported" on it. SwiftlyS2's own factory sets
    /// <c>ConnectionTimeout</c> instead, so only code handed the raw string breaks, and only when an
    /// operator configures a non-zero timeout. Postgres is correct as-is. Parsed rather than
    /// string-replaced: a password may contain ';' or '=' and arrives quoted.
    /// </remarks>
    private string MigratorConnectionString()
    {
        var raw = _core.Database.GetConnectionString(_database.Name);
        var driver = _database.Info.Driver?.Trim().ToLowerInvariant() ?? string.Empty;

        if (driver is not ("mysql" or "mariadb")) return raw;

        var builder = new DbConnectionStringBuilder { ConnectionString = raw };

        if (!builder.TryGetValue("timeout", out var timeout)) return raw;

        builder.Remove("timeout");

        if (!builder.ContainsKey("connectiontimeout")) builder["ConnectionTimeout"] = timeout;

        return builder.ConnectionString;
    }

    /// <remarks>
    /// Dispatch is on the Driver string, never on the CLR type of the connection: the framework
    /// resolves a connection by name and the concrete type tells you which ADO provider is in play,
    /// not which SQL dialect the operator configured.
    /// </remarks>
    private void ConfigureEngine(IMigrationRunnerBuilder rb)
    {
        var driver = _database.Info.Driver?.Trim().ToLowerInvariant() ?? string.Empty;

        switch (driver)
        {
            case "mysql" or "mariadb":
                rb.AddMySql5();
                break;

            case "postgresql" or "postgres" or "pgsql":
                rb.AddPostgres();
                break;

            case "sqlite":
                rb.AddSQLite();
                break;

            default:
                throw new InvalidOperationException(
                    $"Ranks: database driver '{_database.Info.Driver}' is not supported. " +
                    "Supported drivers are mysql, postgresql and sqlite (FR-DB-01).");
        }
    }

    /// <remarks>
    /// Replaces whichever accessor the engine registered rather than re-registering
    /// IMigrationProcessor: the runner reads IProcessorAccessor.Processor, so anything else would
    /// decorate a service nothing resolves.
    /// </remarks>
    private void DecorateProcessor(IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IProcessorAccessor))
            ?? throw new InvalidOperationException(
                "Ranks: FluentMigrator registered no IProcessorAccessor, so table affixes cannot be enforced.");

        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(IProcessorAccessor),
            sp => new AffixingProcessorAccessor(ResolveOriginal(sp, descriptor), _tables),
            descriptor.Lifetime));
    }

    private static IProcessorAccessor ResolveOriginal(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IProcessorAccessor instance) return instance;
        if (descriptor.ImplementationFactory is not null) return (IProcessorAccessor)descriptor.ImplementationFactory(sp);

        return (IProcessorAccessor)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
    }

    /// <summary>Reports any table a migration created without the configured affixes.</summary>
    /// <remarks>
    /// Execute.Sql carries no expression tree, so the decorator cannot reach inside it — enforcement
    /// for the expression API, detection for the rest (FR-DB-05 req. 10).
    /// </remarks>
    private void VerifyAffixes(IReadOnlySet<string> before, string pluginId)
    {
        if (!_tables.HasAffixes) return;

        try
        {
            foreach (var table in ListTables())
            {
                if (before.Contains(table) || _tables.CarriesAffixes(table)) continue;

                _logger.LogError(
                    "Ranks: module {PluginId} created table '{Table}' without the configured prefix/suffix. " +
                    "Raw Execute.Sql DDL cannot be rewritten — use the expression API so core can apply affixes.",
                    pluginId, table);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks: could not verify table affixes for module {PluginId}.", pluginId);
        }
    }

    private HashSet<string> ListTables()
    {
        try
        {
            using var connection = _database.Create();
            connection.Open();

            return DatabaseTables.List(connection, Driver);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks: could not list existing tables; affix verification is skipped this run.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private string Driver => _database.Info.Driver?.Trim().ToLowerInvariant() ?? string.Empty;
}
