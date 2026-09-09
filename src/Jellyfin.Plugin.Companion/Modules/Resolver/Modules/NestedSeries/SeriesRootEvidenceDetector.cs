using System.Text.RegularExpressions;
using Jellyfin.Plugin.SmartResolver.Core;

namespace Jellyfin.Plugin.SmartResolver.Modules.NestedSeries;

public sealed partial class SeriesRootEvidenceDetector
{
    private static readonly HashSet<string> VideoExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".asf", ".avi", ".divx", ".flv", ".m2ts", ".m4v", ".mkv",
        ".mov", ".mp4", ".mpeg", ".mpg", ".mts", ".ogm", ".ogv", ".ts",
        ".vob", ".webm", ".wmv"
    };

    private static readonly HashSet<string> ArtworkNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "poster.jpg", "poster.png", "folder.jpg", "folder.png",
        "fanart.jpg", "fanart.png", "clearlogo.png", "logo.png"
    };

    public SeriesEvidenceResult Detect(string path)
    {
        try
        {
            var evidence = new List<ResolverEvidence>();
            var directories = Directory.EnumerateDirectories(path).ToArray();
            var files = Directory.EnumerateFiles(path).ToArray();

            if (directories.Any(directory =>
                    IsSeasonFolderName(Path.GetFileName(directory))))
            {
                evidence.Add(ResolverEvidence.SeasonFolder);
            }

            if (files.Any(file => IsEpisodeFileName(Path.GetFileName(file))))
            {
                evidence.Add(ResolverEvidence.EpisodeFile);
            }

            if (files.Any(file =>
                    string.Equals(
                        Path.GetFileName(file),
                        "tvshow.nfo",
                        StringComparison.OrdinalIgnoreCase)))
            {
                evidence.Add(ResolverEvidence.TvShowNfo);
            }

            var hasVideo = files.Any(file => VideoExtensions.Contains(Path.GetExtension(file)));
            var hasArtwork = files.Any(file => ArtworkNames.Contains(Path.GetFileName(file)));
            if (hasArtwork && hasVideo)
            {
                evidence.Add(ResolverEvidence.LocalArtwork);
                evidence.Add(ResolverEvidence.VideoContent);
            }

            return new SeriesEvidenceResult(evidence, false);
        }
        catch (IOException)
        {
            return new SeriesEvidenceResult([], true);
        }
        catch (UnauthorizedAccessException)
        {
            return new SeriesEvidenceResult([], true);
        }
    }

    public static bool IsSeasonFolderName(string name)
    {
        return SeasonFolderRegex().IsMatch(name);
    }

    public static bool IsEpisodeFileName(string name)
    {
        return EpisodeFileRegex().IsMatch(name);
    }

    [GeneratedRegex(
        @"^(?:(?:season|series|сезон)\s*\d+|specials?|спец(?:выпуски|эпизоды)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonFolderRegex();

    [GeneratedRegex(
        @"\bS\d{1,2}E\d{1,3}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeFileRegex();
}

public sealed record SeriesEvidenceResult(
    IReadOnlyList<ResolverEvidence> Evidence,
    bool FilesystemUnavailable);
