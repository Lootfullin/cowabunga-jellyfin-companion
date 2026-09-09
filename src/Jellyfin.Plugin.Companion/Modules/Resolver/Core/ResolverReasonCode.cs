namespace Jellyfin.Plugin.SmartResolver.Core;

public enum ResolverReasonCode
{
    Accepted,
    PluginDisabled,
    WrongCollectionType,
    AlreadyInsideSeries,
    AlreadyInsideSeason,
    OuterNameRejected,
    NoEligibleChild,
    MultipleEligibleChildren,
    ChildHasNoSeriesEvidence,
    ChildHasNoMovieEvidence,
    MultipleMovieFiles,
    FilesystemUnavailable,
    PathLoopDetected,
    InnerPathEqualsOuterPath,
    InvalidCustomRegex,
    UnknownFailure
}
