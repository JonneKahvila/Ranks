using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menu;

namespace Ranks.Menus;

/// <summary>Routes core's menus to a renderer another plugin asked for (FR-API-14).</summary>
/// <remarks>
/// <para>
/// Attached to every core menu builder as an <see cref="IMenuRendererSource"/> at
/// <see cref="MenuRendererPriority.Plugin"/>. That puts it above the code default and below the
/// server's <c>menus.jsonc</c>, so an operator can still override any page.
/// </para>
/// <para>
/// It exists because <c>menus.jsonc</c> alone is unsafe for this. <c>MenuBuilder.Build()</c> throws
/// when the resolved renderer is not registered, so a config naming a plugin's renderer breaks
/// <c>!rank</c> for everyone as soon as that plugin is missing. This source asks the registry first
/// and answers only for a renderer that exists, which also covers a plugin that unloaded without
/// clearing its request: SwiftlyS2 drops a plugin's renderers on unload.
/// </para>
/// <para>
/// Holds strings only. Core still never calls module code (FR-API-08).
/// </para>
/// </remarks>
internal sealed class MenuRendererRouting : IMenuRendererSource
{
    private readonly ISwiftlyCore _core;
    private readonly Lock _gate = new();
    private Request? _active;

    public MenuRendererRouting(ISwiftlyCore core) => _core = core;

    private sealed record Request(string ModuleId, string RendererId, HashSet<string>? MenuIds);

    public int Priority => MenuRendererPriority.Plugin;

    public void Set(string moduleId, string rendererId, IReadOnlyCollection<string>? menuIds)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrWhiteSpace(rendererId)) return;

        // Copied, so a caller mutating its own list later cannot change what core routes.
        var ids = menuIds is null
            ? null
            : new HashSet<string>(menuIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);

        lock (_gate)
            _active = new Request(moduleId, rendererId, ids);
    }

    public void Clear(string moduleId)
    {
        lock (_gate)
        {
            if (_active is { } active && string.Equals(active.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
                _active = null;
        }
    }

    public bool TryGetRenderer(string menuId, out string rendererId)
    {
        Request? active;

        lock (_gate)
            active = _active;

        if (active is not null
            && (active.MenuIds is null || active.MenuIds.Contains(menuId))
            && _core.Menu.Renderers.TryGet(active.RendererId, out _))
        {
            rendererId = active.RendererId;
            return true;
        }

        rendererId = string.Empty;
        return false;
    }
}
