using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;

namespace RussianMetadata;

internal static partial class MovieYearAudit
{
    // Require an explicit year marker. A bare trailing number may be part of the title.
    [GeneratedRegex(@"^(?<title>.+?)\s+\((?<year>[0-9]{4})\)(?:\s*\[[^\[\]]+\])*\s*$")]
    private static partial Regex ParenthesizedYear();

    [GeneratedRegex(@"^(?<title>.+?)\.(?<year>[0-9]{4})\.(?:480p|576p|720p|1080[pi]|1440p|2160p|4320p)(?:[._ -]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SceneYear();

    internal static MovieYearMismatch? Check(Movie movie)
    {
        if (string.IsNullOrWhiteSpace(movie.Path) || movie.ProductionYear is not > 0
            || MovieLookup.ExtractTmdbId(movie.ProviderIds) is not int tmdbId)
        {
            return null;
        }

        var fileName = Path.GetFileName(movie.Path);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var match = ParenthesizedYear().Match(stem);
        var isSceneName = !match.Success;
        if (isSceneName) match = SceneYear().Match(stem);
        if (!match.Success
            || !int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || year < 1888 || year > DateTime.UtcNow.Year + 2
            || Math.Abs(year - movie.ProductionYear.Value) <= 1)
        {
            return null;
        }

        var title = match.Groups["title"].Value.Trim();
        if (isSceneName)
        {
            title = title.Replace('.', ' ').Replace('_', ' ');
            var titleIncludingNumber = MovieLookup.Normalize(title + match.Groups["year"].Value);
            if (titleIncludingNumber == MovieLookup.Normalize(movie.Name)
                || titleIncludingNumber == MovieLookup.Normalize(movie.OriginalTitle))
            {
                return null;
            }
        }
        return string.IsNullOrWhiteSpace(title) ? null : new MovieYearMismatch(
            movie.Id, fileName, title, year, movie.ProductionYear.Value, tmdbId, movie.GetProviderId(MetadataProvider.Imdb));
    }
}

public sealed record MovieYearMismatch(
    Guid ItemId, string FileName, string SearchTitle, int FileYear, int SavedYear, int TmdbId, string? ImdbId);

public sealed record MovieYearAuditResult(bool Enabled, int MoviesChecked, MovieYearMismatch[] Mismatches);
