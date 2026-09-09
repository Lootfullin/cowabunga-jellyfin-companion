using Jellyfin.Plugin.SmartResolver.Core;

namespace Jellyfin.Plugin.SmartResolver.Diagnostics;

public sealed record ResolutionHistoryEntry(
    DateTimeOffset Timestamp,
    string Module,
    bool Accepted,
    string SourcePath,
    string? ResolvedPath,
    ResolverReasonCode ReasonCode,
    string Reason,
    string? DetectedName,
    int? DetectedYear,
    IReadOnlyList<ResolverEvidence> Evidence);
