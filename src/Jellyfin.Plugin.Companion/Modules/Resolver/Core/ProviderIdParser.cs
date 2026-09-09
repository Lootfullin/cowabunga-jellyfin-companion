using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SmartResolver.Core;

public static partial class ProviderIdParser
{
    public static string? GetTmdbId(string value)
    {
        return GetId(TmdbIdRegex(), value);
    }

    public static string? GetImdbId(string value)
    {
        return GetId(ImdbIdRegex(), value);
    }

    public static string? GetTvdbId(string value)
    {
        return GetId(TvdbIdRegex(), value);
    }

    private static string? GetId(Regex regex, string value)
    {
        var match = regex.Match(value);
        return match.Success ? match.Groups["id"].Value : null;
    }

    [GeneratedRegex(
        @"\[tmdbid-(?<id>\d+)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TmdbIdRegex();

    [GeneratedRegex(
        @"\[imdbid-(?<id>tt\d+)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImdbIdRegex();

    [GeneratedRegex(
        @"\[tvdbid-(?<id>\d+)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TvdbIdRegex();
}
