using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Ranks.CustomCombos.Combos;
using Ranks.CustomCombos.Contract;
using Ranks.CustomCombos.Stats;
using SwiftlyS2.Shared;

namespace Ranks.CustomCombos.Api;

/// <summary>This module's published read API.</summary>
/// <remarks>
/// Unlike <see cref="RanksBridge"/> this names its contract without any JIT isolation, and that is
/// correct: a provider hard-requires the assembly it publishes from, exactly as core does with
/// Ranks.Contract. This module ships that assembly itself, so it is present whenever the module is.
///
/// Answers from the database plus the unflushed counts, so a figure read here matches what
/// <c>!combos</c> prints rather than lagging a flush interval behind. Core's periods arrive as a
/// plain delegate, so nothing here names a Ranks.Contract type either.
/// </remarks>
internal sealed class CustomCombosApi : ICustomCombosApi
{
    private readonly ISwiftlyCore _core;
    private readonly Writer _writer;
    private readonly ComboCounters _counters;
    private readonly Func<long?> _currentPeriod;
    private readonly ILogger _logger;

    private ComboMatcher _matcher;

    public CustomCombosApi(ISwiftlyCore core, Writer writer, ComboCounters counters, Func<long?> currentPeriod, ComboMatcher matcher, ILogger logger)
    {
        _core = core;
        _writer = writer;
        _counters = counters;
        _currentPeriod = currentPeriod;
        _matcher = matcher;
        _logger = logger;
    }

    public void UpdateRules(ComboMatcher matcher) => _matcher = matcher;

    public IReadOnlyList<string> ConfiguredCombos => [.. _matcher.Rules.Select(rule => rule.Key)];

    /// <remarks>Same missing-key fallback as the arbiter's chat line, for the same reason.</remarks>
    public string GetComboName(string comboKey)
    {
        var key = $"combo.{comboKey}";
        var resolved = _core.Localizer[key];

        return string.IsNullOrEmpty(resolved)
               || string.Equals(resolved, key, StringComparison.Ordinal)
               || resolved.EndsWith('.' + key, StringComparison.Ordinal)
            ? comboKey
            : StripColorTokens(resolved);
    }

    /// <remarks>
    /// <c>combo.*</c> names are operator-authored and shared with the arbiter's chat line, so they
    /// routinely carry chat colour tokens. A menu row wants HTML, so a token left in renders as
    /// literal <c>{lime}</c> text to the player.
    /// </remarks>
    private static string StripColorTokens(string text) =>
        ColorToken.Replace(text, string.Empty).Trim();

    private static readonly Regex ColorToken = new(@"\{[a-zA-Z]+\}|\[[a-zA-Z]+\]", RegexOptions.Compiled);

    public Task<IReadOnlyList<ComboStatRow>> GetCombosAsync(ulong steamId) =>
        GetCombosAsync(steamId, ICustomCombosApi.AllTime);

    /// <remarks>
    /// The unflushed counts join a period read only when the next flush will stamp them with that
    /// period. Anywhere else they would show a figure the database never ends up holding.
    /// </remarks>
    public async Task<IReadOnlyList<ComboStatRow>> GetCombosAsync(ulong steamId, long period)
    {
        var live = await PeekAsync(steamId);
        var stored = period == ICustomCombosApi.AllTime
            ? await _writer.ReadAsync(steamId)
            : await _writer.ReadAsync(steamId, period);

        return Merge(stored, period == ICustomCombosApi.AllTime || live.Period == period ? live.Counts : []);
    }

    public Task<ComboStatRow?> GetComboAsync(ulong steamId, string comboKey) =>
        GetComboAsync(steamId, comboKey, ICustomCombosApi.AllTime);

    public async Task<ComboStatRow?> GetComboAsync(ulong steamId, string comboKey, long period) =>
        (await GetCombosAsync(steamId, period)).FirstOrDefault(row => string.Equals(row.ComboKey, comboKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stored rows plus unflushed counts, highest count first.</summary>
    /// <remarks>
    /// An unflushed combo has no stored row and therefore no stored timestamp; UtcNow is the honest
    /// answer, since the count it describes was landed this session.
    /// </remarks>
    private static IReadOnlyList<ComboStatRow> Merge(List<(string ComboKey, long Count, DateTime LastAt)> stored, Dictionary<string, int> live)
    {
        var merged = new Dictionary<string, ComboStatRow>(StringComparer.Ordinal);

        foreach (var (comboKey, count, lastAt) in stored)
            merged[comboKey] = new ComboStatRow(comboKey, count, lastAt);

        foreach (var (comboKey, count) in live)
        {
            var row = merged.GetValueOrDefault(comboKey);

            merged[comboKey] = row is null
                ? new ComboStatRow(comboKey, count, DateTime.UtcNow)
                : row with { Count = row.Count + count, LastAt = DateTime.UtcNow };
        }

        return [.. merged.Values.OrderByDescending(row => row.Count)];
    }

    /// <summary>One player's unflushed counts, and the period the next flush will stamp them with.</summary>
    /// <remarks>
    /// Hops to the game thread: the counters are game-thread state and a consumer calls this from
    /// wherever it likes. Both are read in the same tick, so a period change cannot fall between
    /// them. The returned map is a copy, so the continuation is free to use it off-thread afterwards.
    /// </remarks>
    private Task<(Dictionary<string, int> Counts, long? Period)> PeekAsync(ulong steamId)
    {
        var completion = new TaskCompletionSource<(Dictionary<string, int>, long?)>(TaskCreationOptions.RunContinuationsAsynchronously);

        _core.Scheduler.NextTick(() =>
        {
            try
            {
                completion.SetResult((_counters.Peek(steamId), _currentPeriod()));
            }
            catch (Exception ex)
            {
                // Falling back to stored-only is better than failing the query: the answer is then
                // merely behind, which is what it would have been without this merge at all.
                _logger.LogWarning(ex, "Ranks_CustomCombos: could not read unflushed counts for {SteamId}.", steamId);
                completion.SetResult(([], null));
            }
        });

        return completion.Task;
    }
}
