using Emby.Naming.Common;
using Jellyfin.Plugin.SmartResolver.Core;
using MediaBrowser.Model.IO;
using System.Text.RegularExpressions;
using JellyfinVideoResolver = Emby.Naming.Video.VideoResolver;

namespace Jellyfin.Plugin.SmartResolver.Modules.Movies;

public sealed partial class MovieFileDetector
{
    private readonly NamingOptions _namingOptions;

    public MovieFileDetector(NamingOptions namingOptions)
    {
        _namingOptions = namingOptions;
    }

    public ResolverDecision Detect(
        string folderPath,
        IReadOnlyCollection<FileSystemMetadata> fileSystemChildren)
    {
        return Detect(folderPath, fileSystemChildren, allowNestedFolder: false);
    }

    public ResolverDecision Detect(
        string folderPath,
        IReadOnlyCollection<FileSystemMetadata> fileSystemChildren,
        bool allowNestedFolder)
    {
        var videos = fileSystemChildren
            .Where(entry => !entry.IsDirectory)
            .Select(entry => JellyfinVideoResolver.ResolveFile(
                entry.FullName,
                _namingOptions,
                Path.GetDirectoryName(folderPath)))
            .Where(video => video is not null && video.ExtraType is null)
            .ToArray();

        if (videos.Length == 0)
        {
            if (allowNestedFolder)
            {
                var nested = TryDetectNestedMovie(folderPath, fileSystemChildren);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return ResolverDecision.Reject(
                folderPath,
                ResolverReasonCode.ChildHasNoMovieEvidence,
                "The folder has no primary video file recognized by Jellyfin.");
        }

        var first = videos[0]!;
        if (videos.Any(video =>
                !string.Equals(video!.Name, first.Name, StringComparison.OrdinalIgnoreCase)
                || video.Year != first.Year))
        {
            return ResolverDecision.Reject(
                folderPath,
                ResolverReasonCode.MultipleMovieFiles,
                "The folder contains primary videos with different titles or years.");
        }

        var decision = ResolverDecision.Accept(
            folderPath,
            first.Path,
            first.Name,
            first.Year,
            ResolverEvidence.MovieVideoFile);

        if (videos.Length == 1)
        {
            return decision;
        }

        var remainingPaths = videos.Skip(1).Select(video => video!.Path).ToArray();
        var allParts = videos.All(video => PartSuffixRegex().IsMatch(
            Path.GetFileNameWithoutExtension(video!.Path)));

        return allParts
            ? decision with
            {
                Evidence =
                [
                    ResolverEvidence.MovieVideoFile,
                    ResolverEvidence.MovieAdditionalPart
                ],
                AdditionalParts = remainingPaths
            }
            : decision with
            {
                Evidence =
                [
                    ResolverEvidence.MovieVideoFile,
                    ResolverEvidence.MovieAlternateVersion
                ],
                AlternateVersions = remainingPaths
            };
    }

    private ResolverDecision? TryDetectNestedMovie(
        string folderPath,
        IReadOnlyCollection<FileSystemMetadata> children)
    {
        var directories = children
            .Where(entry => entry.IsDirectory && !IgnoredDirectoryNames.Contains(entry.Name))
            .ToArray();
        if (directories.Length != 1
            || !PathSafety.IsDirectSafeChild(folderPath, directories[0].FullName))
        {
            return null;
        }

        FileSystemMetadata[] nestedChildren;
        try
        {
            nestedChildren = Directory
                .EnumerateFileSystemEntries(directories[0].FullName)
                .Select(CreateMetadata)
                .ToArray();
        }
        catch (Exception)
        {
            return null;
        }

        var nested = Detect(
            directories[0].FullName,
            nestedChildren,
            allowNestedFolder: false);
        if (!nested.Accepted)
        {
            return null;
        }

        return nested with
        {
            OuterPath = folderPath,
            Evidence = [.. nested.Evidence, ResolverEvidence.NestedMovieFolder]
        };
    }

    private static FileSystemMetadata CreateMetadata(string path)
    {
        var isDirectory = Directory.Exists(path);
        return new FileSystemMetadata
        {
            Exists = isDirectory || File.Exists(path),
            FullName = path,
            IsDirectory = isDirectory,
            Name = Path.GetFileName(path)
        };
    }

    [GeneratedRegex(
        @"(?:^|[\s._-])(?:cd|disc|disk|part|pt)[\s._-]*\d+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartSuffixRegex();
}
