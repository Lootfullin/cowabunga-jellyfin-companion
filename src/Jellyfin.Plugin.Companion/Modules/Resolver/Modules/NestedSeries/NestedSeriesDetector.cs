using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Companion.Configuration;
using Jellyfin.Plugin.SmartResolver.Core;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.SmartResolver.Modules.NestedSeries;

public sealed partial class NestedSeriesDetector
{
    private readonly OuterFolderMatcher _outerFolderMatcher;
    private readonly SeriesRootEvidenceDetector _evidenceDetector;

    public NestedSeriesDetector()
        : this(new OuterFolderMatcher(), new SeriesRootEvidenceDetector())
    {
    }

    public NestedSeriesDetector(
        OuterFolderMatcher outerFolderMatcher,
        SeriesRootEvidenceDetector evidenceDetector)
    {
        _outerFolderMatcher = outerFolderMatcher;
        _evidenceDetector = evidenceDetector;
    }

    public ResolverDecision Detect(
        string outerPath,
        IReadOnlyCollection<FileSystemMetadata> fileSystemChildren)
    {
        return Detect(outerPath, fileSystemChildren, new PluginConfiguration());
    }

    public ResolverDecision Detect(
        string outerPath,
        IReadOnlyCollection<FileSystemMetadata> fileSystemChildren,
        PluginConfiguration configuration)
    {
        var outerName = Path.GetFileName(outerPath);
        if (string.IsNullOrWhiteSpace(outerName))
        {
            return RejectOuterName(outerPath);
        }

        var match = _outerFolderMatcher.Match(
            outerName,
            configuration.NestedSeriesMode,
            configuration.CustomRegex);
        if (!match.IsMatch)
        {
            return ResolverDecision.Reject(
                outerPath,
                match.ReasonCode,
                match.ReasonCode == ResolverReasonCode.InvalidCustomRegex
                    ? "The custom regular expression is empty or invalid."
                    : "The outer folder name is not allowed by the active resolver mode.");
        }

        if (configuration.NestedSeriesMode == ResolverMode.AnySingleNestedSeries
            && OuterContainsSeriesEvidence(fileSystemChildren))
        {
            return ResolverDecision.Reject(
                outerPath,
                ResolverReasonCode.OuterNameRejected,
                "The outer folder already contains season or episode evidence.");
        }

        var directories = fileSystemChildren
            .Where(entry =>
                entry.IsDirectory
                && !IgnoredDirectoryNames.Contains(entry.Name))
            .ToArray();

        if (directories.Length == 0)
        {
            return ResolverDecision.Reject(
                outerPath,
                ResolverReasonCode.NoEligibleChild,
                "The outer folder does not contain an eligible nested directory.");
        }

        if (configuration.RequireExactlyOneEligibleChild && directories.Length != 1)
        {
            return ResolverDecision.Reject(
                outerPath,
                ResolverReasonCode.MultipleEligibleChildren,
                "The outer folder contains more than one eligible nested directory.");
        }

        if (directories.Length != 1)
        {
            return ResolverDecision.Reject(
                outerPath,
                ResolverReasonCode.MultipleEligibleChildren,
                "Automatic selection is unsafe when multiple nested directories exist.");
        }

        var innerPath = directories[0].FullName;
        if (!Directory.Exists(innerPath))
        {
            return ResolverDecision.Reject(
                outerPath,
                ResolverReasonCode.FilesystemUnavailable,
                "The nested directory does not exist or is unavailable.");
        }

        if (!PathSafety.IsDirectSafeChild(outerPath, innerPath))
        {
            return ResolverDecision.Reject(
                outerPath,
                ResolverReasonCode.PathLoopDetected,
                "The nested path is not a safe direct child of the outer folder.");
        }

        var evidenceResult = _evidenceDetector.Detect(innerPath);
        if (evidenceResult.FilesystemUnavailable)
        {
            return ResolverDecision.Reject(
                outerPath,
                ResolverReasonCode.FilesystemUnavailable,
                "The nested directory could not be inspected.");
        }

        if (evidenceResult.Evidence.Count == 0)
        {
            return ResolverDecision.Reject(
                outerPath,
                ResolverReasonCode.ChildHasNoSeriesEvidence,
                "The nested directory has no strong series evidence.");
        }

        var innerName = Path.GetFileName(innerPath);
        var year = ExtractYear(innerName) ?? ExtractYear(outerName);

        return ResolverDecision.Accept(
            outerPath,
            innerPath,
            innerName,
            year,
            [.. evidenceResult.Evidence]);
    }

    private static bool OuterContainsSeriesEvidence(
        IReadOnlyCollection<FileSystemMetadata> entries)
    {
        return entries.Any(entry =>
            entry.IsDirectory
                ? SeriesRootEvidenceDetector.IsSeasonFolderName(entry.Name)
                : SeriesRootEvidenceDetector.IsEpisodeFileName(entry.Name));
    }

    private static ResolverDecision RejectOuterName(string outerPath)
    {
        return ResolverDecision.Reject(
            outerPath,
            ResolverReasonCode.OuterNameRejected,
            "The outer folder has no usable name.");
    }

    private static int? ExtractYear(string value)
    {
        var match = YearRegex().Match(value);
        return match.Success
            && int.TryParse(
                match.Groups["year"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var year)
                ? year
                : null;
    }

    [GeneratedRegex(@"(?<year>19\d{2}|20\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();
}
