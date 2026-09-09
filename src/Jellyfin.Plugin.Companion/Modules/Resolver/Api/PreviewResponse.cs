using Jellyfin.Plugin.SmartResolver.Core;

namespace Jellyfin.Plugin.SmartResolver.Api;

public sealed record PreviewResponse(
    string Module,
    bool Accepted,
    string SourcePath,
    string? ResolvedPath,
    ResolverReasonCode ReasonCode,
    string Reason,
    string? DetectedName,
    int? DetectedYear,
    IReadOnlyList<ResolverEvidence> Evidence);
