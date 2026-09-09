using System.Collections.Concurrent;
using Jellyfin.Plugin.SmartResolver.Core;

namespace Jellyfin.Plugin.SmartResolver.Diagnostics;

public sealed class ResolutionHistory
{
    private const int MaximumEntries = 200;
    private readonly ConcurrentQueue<ResolutionHistoryEntry> _entries = new();

    public void Add(string module, ResolverDecision decision)
    {
        _entries.Enqueue(new ResolutionHistoryEntry(
            DateTimeOffset.UtcNow,
            module,
            decision.Accepted,
            decision.OuterPath,
            decision.ResolvedPath,
            decision.ReasonCode,
            decision.HumanReadableReason,
            decision.DetectedName,
            decision.DetectedYear,
            decision.Evidence));

        while (_entries.Count > MaximumEntries)
        {
            _entries.TryDequeue(out _);
        }
    }

    public IReadOnlyList<ResolutionHistoryEntry> GetRecent()
    {
        return _entries.Reverse().ToArray();
    }

    public void Clear()
    {
        _entries.Clear();
    }
}
