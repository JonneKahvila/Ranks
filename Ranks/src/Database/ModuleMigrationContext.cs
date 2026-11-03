using System.Reflection;
using System.Runtime.Loader;
using FluentMigrator;

namespace Ranks.Database;

/// <summary>A throwaway second load of a module assembly, with FluentMigrator bound to core's copy.</summary>
/// <remarks>
/// A module ships its own FluentMigrator because SwiftlyS2's load-time GetTypes() must resolve its
/// Migration subclasses, and that private copy gives them an IMigration core's runner cannot match.
/// Exporting one shared copy would fix that and break other plugins instead: SwiftlyS2 forces every
/// export onto every plugin, replacing the FluentMigrator version they ship.
/// </remarks>
internal sealed class ModuleMigrationContext : AssemblyLoadContext
{
    private readonly AssemblyLoadContext _migrator = GetLoadContext(typeof(IMigration).Assembly) ?? Default;
    private readonly AssemblyLoadContext _module;

    public ModuleMigrationContext(Assembly module)
        : base($"Ranks migrations: {module.GetName().Name}", isCollectible: true) =>
        _module = GetLoadContext(module) ?? Default;

    /// <remarks>From a stream, as SwiftlyS2 loads plugins, so the file stays replaceable for hot reload.</remarks>
    public Assembly LoadModule(string path)
    {
        using var image = File.OpenRead(path);
        return LoadFromStream(image);
    }

    protected override Assembly? Load(AssemblyName name) =>
        name.Name?.StartsWith("FluentMigrator", StringComparison.Ordinal) == true
            ? _migrator.LoadFromAssemblyName(name)
            : _module.LoadFromAssemblyName(name);
}
