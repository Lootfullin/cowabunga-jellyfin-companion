namespace Jellyfin.Plugin.SmartResolver.Core;

public sealed record ResolverDecision(
    bool Accepted,
    string OuterPath,
    string? ResolvedPath,
    ResolverReasonCode ReasonCode,
    string HumanReadableReason,
    string? DetectedName,
    int? DetectedYear,
    IReadOnlyList<ResolverEvidence> Evidence)
{
    public IReadOnlyList<string> AdditionalParts { get; init; } = [];

    public IReadOnlyList<string> AlternateVersions { get; init; } = [];

    public static ResolverDecision Accept(
        string outerPath,
        string resolvedPath,
        string detectedName,
        int? detectedYear,
        params ResolverEvidence[] evidence)
    {
        return new ResolverDecision(
            true,
            outerPath,
            resolvedPath,
            ResolverReasonCode.Accepted,
            "A single nested media root was accepted.",
            detectedName,
            detectedYear,
            evidence);
    }

    public static ResolverDecision Reject(
        string outerPath,
        ResolverReasonCode reasonCode,
        string reason)
    {
        return new ResolverDecision(
            false,
            outerPath,
            null,
            reasonCode,
            reason,
            null,
            null,
            []);
    }
}
