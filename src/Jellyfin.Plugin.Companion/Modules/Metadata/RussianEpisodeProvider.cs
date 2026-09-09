using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace RussianMetadata;

public partial class RussianEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, ICustomMetadataProvider<Episode>
{
    private readonly ILogger<RussianEpisodeProvider> _logger;
    private CompanionPlugin CurrentPlugin => CompanionPlugin.Instance!;

    private const string TmdbApiBase = "https://api.themoviedb.org/3";

    [GeneratedRegex(@"\b(tt\d{7,8})\b")]
    private static partial Regex ImdbIdRegex();

    [GeneratedRegex(@"[Ss](\d+)[.\s_-]*[Ee](\d+)")]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"(?:^|\D)(\d+)[xX](\d+)(?:\D|$)")]
    private static partial Regex SeasonEpisodeAltRegex();

    public RussianEpisodeProvider(ILogger<RussianEpisodeProvider> logger)
    {
        _logger = logger;
    }

    public string Name => CompanionPlugin.MetadataProviderName;

    public Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.AllowsPath(CompanionModule.Metadata, info.Path)) return Task.FromResult(new MetadataResult<Episode>());

        var result = new MetadataResult<Episode>();

        _logger.LogInformation(
            "RussianMetadata (Episode): GetMetadata — Series='{Name}', S{Season}E{Episode}, SeriesTmdbId={Tmdb}",
            info.Name ?? "?",
            info.ParentIndexNumber,
            info.IndexNumber,
            info.SeriesProviderIds.TryGetValue("Tmdb", out var tid) ? tid : "N/A");

        return Task.FromResult(result);
    }

    public async Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.Allows(CompanionModule.Metadata, item)) return ItemUpdateType.None;

        _logger.LogInformation(
            "RussianMetadata (Episode): FetchAsync — '{Name}' (Series={SeriesId})",
            item.Name, item.SeriesId);

        // Need series TMDB ID + season/episode numbers
        int seasonNumber, episodeNumber;

        if (item.ParentIndexNumber.HasValue && item.IndexNumber.HasValue)
        {
            seasonNumber = item.ParentIndexNumber.Value;
            episodeNumber = item.IndexNumber.Value;
        }
        else
        {
            // Episode entity may not have ParentIndexNumber/IndexNumber populated
            // during metadata refresh; try to extract from file path.
            if (!TryParseSeasonEpisode(item.Path, out seasonNumber, out episodeNumber))
            {
                _logger.LogWarning(
                    "RussianMetadata (Episode): Missing season/episode for '{Name}' (path={Path}), skipping",
                    item.Name, item.Path);
                return ItemUpdateType.None;
            }
            _logger.LogInformation(
                "RussianMetadata (Episode): Parsed S{Season}E{Episode} from path",
                seasonNumber, episodeNumber);

            // Restore missing season/episode numbers so Jellyfin sorts correctly
            if (!item.ParentIndexNumber.HasValue)
                item.ParentIndexNumber = seasonNumber;
            if (!item.IndexNumber.HasValue)
                item.IndexNumber = episodeNumber;
        }

        // Get series TMDB ID from parent Series (episodes have their own Tmdb ID)
        var seriesTmdbId = item.FindParent<Series>()?.ProviderIds?.TryGetValue("Tmdb", out var sid) == true
            ? sid
            : null;

        _logger.LogInformation(
            "RussianMetadata (Episode): Series TMDB ID from FindParent: {TmdbId}",
            seriesTmdbId ?? "N/A");

        if (string.IsNullOrEmpty(seriesTmdbId))
        {
            _logger.LogWarning(
                "RussianMetadata (Episode): No TMDB ID for series of '{Name}', skipping",
                item.Name);
            return ItemUpdateType.None;
        }

        var config = CurrentPlugin.Configuration;
        var tmdbApiKey = TmdbApiKeyResolver.Resolve(config);

        if (string.IsNullOrEmpty(tmdbApiKey))
        {
            _logger.LogWarning(
                "RussianMetadata (Episode): Jellyfin TMDB integration is unavailable, skipping");
            return ItemUpdateType.None;
        }

        bool success = await TryTmdbEpisode(
            seriesTmdbId,
            seasonNumber,
            episodeNumber,
            config,
            tmdbApiKey,
            item,
            cancellationToken);

        if (success)
        {
            _logger.LogInformation(
                "RussianMetadata (Episode): Applied Russian data for S{Season}E{Episode} of series {TmdbId}",
                seasonNumber, episodeNumber, seriesTmdbId);
            return ItemUpdateType.MetadataEdit;
        }

        _logger.LogInformation("RussianMetadata (Episode): No Russian data for '{Name}'", item.Name);
        return ItemUpdateType.None;
    }

    private static bool TryParseSeasonEpisode(string? path, out int season, out int episode)
    {
        season = 0;
        episode = 0;
        if (string.IsNullOrEmpty(path)) return false;

        // Try S01E01 / S01.E01 / S01 E01 / S01-E01
        var match = SeasonEpisodeRegex().Match(path);
        if (match.Success)
        {
            season = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            episode = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            return true;
        }

        // Try 1x01 format
        var altMatch = SeasonEpisodeAltRegex().Match(path);
        if (altMatch.Success)
        {
            season = int.Parse(altMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            episode = int.Parse(altMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private async Task<bool> TryTmdbEpisode(
        string seriesTmdbId,
        int seasonNumber,
        int episodeNumber,
        Configuration.PluginConfiguration config,
        string tmdbApiKey,
        Episode item,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "RussianMetadata (Episode): Trying TMDB for series {TmdbId} S{Season}E{Episode}",
            seriesTmdbId, seasonNumber, episodeNumber);

        try
        {
            var handler = CreateProxyHandler(config);
            using var httpClient = new HttpClient(handler, disposeHandler: true);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            httpClient.Timeout = TimeSpan.FromSeconds(5);

            var detailsUrl = $"{TmdbApiBase}/tv/{seriesTmdbId}/season/{seasonNumber}/episode/{episodeNumber}?api_key={Uri.EscapeDataString(tmdbApiKey)}&language=ru-RU";
            _logger.LogInformation(
                "RussianMetadata (Episode): TMDB URL (without key): {Url}",
                detailsUrl.Replace(tmdbApiKey, "***"));

            using var response = await httpClient.GetAsync(detailsUrl, cancellationToken);
            _logger.LogInformation(
                "RussianMetadata (Episode): TMDB response status: {Status}",
                (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "RussianMetadata (Episode): TMDB failed ({Status}) for S{Season}E{Episode} of tmdbId {TmdbId}",
                    (int)response.StatusCode, seasonNumber, episodeNumber, seriesTmdbId);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var details = JsonSerializer.Deserialize<TmdbEpisodeDetails>(json, JsonOptions.Default);

            _logger.LogInformation(
                "RussianMetadata (Episode): TMDB details parsed — Name='{N}', OverviewLen={Ol}",
                details?.Name ?? "N", details?.Overview?.Length ?? 0);

            if (details == null) return false;

            bool changed = false;

            if (!string.IsNullOrEmpty(details.Name) && config.EnableRussianTitles)
            {
                item.Name = details.Name;
                _logger.LogInformation("RussianMetadata (Episode): TMDB — set name: {Name}", details.Name);
                changed = true;
            }

            if (!string.IsNullOrEmpty(details.Overview) && config.EnableRussianOverviews)
            {
                item.Overview = details.Overview;
                _logger.LogInformation("RussianMetadata (Episode): TMDB — set overview ({Len} chars)", details.Overview.Length);
                changed = true;
            }

            return changed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "RussianMetadata (Episode): TMDB failed for S{Season}E{Episode} of tmdbId {TmdbId}",
                seasonNumber, episodeNumber, seriesTmdbId);
            return false;
        }
    }

    private HttpClientHandler CreateProxyHandler(Configuration.PluginConfiguration config)
    {
        var handler = new HttpClientHandler();

        if (!string.IsNullOrEmpty(config.ProxyUrl))
        {
            var uri = new UriBuilder(config.ProxyUrl);
            if (uri.Scheme == "http" || uri.Scheme == "https")
            {
                var webProxy = new WebProxy(config.ProxyUrl);
                if (!string.IsNullOrEmpty(config.ProxyUsername))
                {
                    webProxy.Credentials = new NetworkCredential(config.ProxyUsername, config.ProxyPassword);
                }
                handler.Proxy = webProxy;
                handler.UseProxy = true;
                _logger.LogInformation("RussianMetadata (Episode): Using proxy {Proxy}", config.ProxyUrl);
            }
        }

        return handler;
    }

    // ───── Search ─────

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.AllowsPath(CompanionModule.Metadata, searchInfo.Path)) return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());

        // Episode search by name is not meaningful — episodes are identified by
        // season/episode number. Return empty list.
        return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}

internal class TmdbEpisodeDetails
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }
    [JsonPropertyName("episode_number")]
    public int EpisodeNumber { get; set; }
    [JsonPropertyName("still_path")]
    public string? StillPath { get; set; }
}

internal class EpisodeTmdbSearchResponse
{
    public List<EpisodeTmdbSearchItem>? Results { get; set; }
}

internal class EpisodeTmdbSearchItem
{
    public int Id { get; set; }
    public string? Name { get; set; }
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }
    public string? Overview { get; set; }
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }
}
