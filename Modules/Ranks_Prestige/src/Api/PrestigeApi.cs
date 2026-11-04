using Ranks.Prestige.Config;
using Ranks.Prestige.Contract;

namespace Ranks.Prestige.Api;

/// <summary>This module's published read API.</summary>
/// <remarks>
/// Names its own contract without JIT isolation, as CustomCombos does: this module ships that
/// assembly, so it is present whenever the module is. Everything touching <c>Ranks.Contract</c>
/// stays behind <see cref="RanksBridge"/>.
/// </remarks>
internal sealed class PrestigeApi : IPrestigeApi
{
    private readonly RanksBridge _bridge;
    private volatile PrestigeConfig _config;

    public PrestigeApi(RanksBridge bridge, PrestigeConfig config)
    {
        _bridge = bridge;
        _config = config;
    }

    public void UpdateConfig(PrestigeConfig config) => _config = config;

    public bool IsEnabled => _config.Enabled;

    // IsAvailable first: reading it resolves nothing, while ProgressFor names core's contract.
    public PrestigeProgress? GetProgress(ulong steamId) =>
        _config.Enabled && _bridge.IsAvailable ? _bridge.ProgressFor(steamId) : null;
}
