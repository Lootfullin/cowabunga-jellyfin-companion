using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Companion.Configuration;

namespace RussianMetadata;

public sealed class ChooseYourMetaBoxSetProvider
    : IRemoteMetadataProvider<BoxSet, BoxSetInfo>,
      ICustomMetadataProvider<BoxSet>
{
    private const string TmdbApiBase = "https://api.themoviedb.org/3";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChooseYourMetaBoxSetProvider> _logger;
    private readonly ConcurrentDictionary<int, int?> _movieCollectionIds = new();

    public ChooseYourMetaBoxSetProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<ChooseYourMetaBoxSetProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => CompanionPlugin.MetadataProviderName;

    private PluginConfiguration Configuration =>
        CompanionPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public async Task<MetadataResult<BoxSet>> GetMetadata(
        BoxSetInfo info,
        CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.AllowsCollection(CompanionModule.Metadata)) return new MetadataResult<BoxSet>();

        var result = new MetadataResult<BoxSet>();
        var apiKey = TmdbApiKeyResolver.Resolve(Configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return result;
        }

        try
        {
            using var httpClient = CreateHttpClient(Configuration);
            // Never identify a collection by taking the first title-search
            // result. TMDB contains unrelated collections with identical
            // names (for example two different "Blade Collection" entries).
            // Automatic identification is repaired from the linked movies in
            // FetchAsync, where we have enough evidence to choose safely.
            var tmdbId = ParseTmdbId(info.GetProviderId(MetadataProvider.Tmdb));

            var collection = await GetCollection(
                tmdbId,
                apiKey,
                httpClient,
                cancellationToken);
            if (collection is null)
            {
                return result;
            }

            var item = new BoxSet();
            ApplyRussianLanguagePreference(item);
            var russianName = MovieTextLocalization.RussianOrNull(
                collection.Name);
            var russianOverview = MovieTextLocalization.RussianOrNull(
                collection.Overview);
            if (Configuration.EnableRussianTitles
                && !string.IsNullOrWhiteSpace(russianName))
            {
                item.Name = russianName;
            }
            else if (!string.IsNullOrWhiteSpace(info.Name))
            {
                item.Name = info.Name;
            }

            if (Configuration.EnableRussianOverviews
                && !string.IsNullOrWhiteSpace(russianOverview))
            {
                item.Overview = russianOverview;
            }

            item.SetProviderId(
                MetadataProvider.Tmdb,
                collection.Id.ToString(CultureInfo.InvariantCulture));
            result.Item = item;
            result.HasMetadata = true;
            result.ResultLanguage = "ru";
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "ChooseYourMeta: TMDB collection metadata failed");
            return result;
        }
    }

    public async Task<ItemUpdateType> FetchAsync(
        BoxSet item,
        MetadataRefreshOptions options,
        CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.Allows(CompanionModule.Metadata, item)) return ItemUpdateType.None;

        var config = Configuration;
        var apiKey = TmdbApiKeyResolver.Resolve(config);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ItemUpdateType.None;
        }

        var tmdbId = ParseTmdbId(item.GetProviderId(MetadataProvider.Tmdb));
        try
        {
            using var httpClient = CreateHttpClient(config);
            var memberCollectionId = await ResolveCollectionIdFromMembers(
                item,
                apiKey,
                httpClient,
                cancellationToken);
            var changed = false;
            if (memberCollectionId is > 0 && memberCollectionId.Value != tmdbId)
            {
                _logger.LogInformation(
                    "ChooseYourMeta: corrected collection {Name} TMDB ID from {OldTmdbId} to {NewTmdbId} using linked movies",
                    item.Name,
                    tmdbId > 0 ? tmdbId : null,
                    memberCollectionId.Value);
                tmdbId = memberCollectionId.Value;
                item.SetProviderId(
                    MetadataProvider.Tmdb,
                    tmdbId.ToString(CultureInfo.InvariantCulture));
                changed = true;
            }

            if (tmdbId <= 0)
            {
                return changed ? ItemUpdateType.MetadataEdit : ItemUpdateType.None;
            }

            if (!config.EnableRussianTitles && !config.EnableRussianOverviews)
            {
                return changed ? ItemUpdateType.MetadataEdit : ItemUpdateType.None;
            }

            ApplyRussianLanguagePreference(item);
            var collection = await GetCollection(tmdbId, apiKey, httpClient, cancellationToken);
            if (collection is null)
            {
                return changed ? ItemUpdateType.MetadataEdit : ItemUpdateType.None;
            }

            var russianName = MovieTextLocalization.RussianOrNull(collection.Name);
            if (config.EnableRussianTitles && !string.IsNullOrWhiteSpace(russianName) && item.Name != russianName)
            {
                item.Name = russianName;
                changed = true;
            }

            var russianOverview = MovieTextLocalization.RussianOrNull(collection.Overview);
            if (config.EnableRussianOverviews
                && !string.IsNullOrWhiteSpace(russianOverview)
                && item.Overview != russianOverview)
            {
                item.Overview = russianOverview;
                changed = true;
            }

            return changed ? ItemUpdateType.MetadataEdit : ItemUpdateType.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ChooseYourMeta: final BoxSet localization failed for TMDB {TmdbId}", tmdbId);
            return ItemUpdateType.None;
        }
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        BoxSetInfo searchInfo,
        CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.AllowsCollection(CompanionModule.Metadata)) return Array.Empty<RemoteSearchResult>();

        var apiKey = TmdbApiKeyResolver.Resolve(Configuration);
        if (string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(searchInfo.Name))
        {
            return [];
        }

        try
        {
            using var httpClient = CreateHttpClient(Configuration);
            var url = $"{TmdbApiBase}/search/collection"
                + $"?api_key={Uri.EscapeDataString(apiKey)}"
                + "&language=ru-RU"
                + $"&query={Uri.EscapeDataString(searchInfo.Name)}";
            using var response = await httpClient.GetAsync(
                url,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);
            var search = JsonSerializer.Deserialize<TmdbCollectionSearchResponse>(
                json,
                JsonOptions.Default);
            return search?.Results?
                .Select(collection =>
                {
                    var result = new RemoteSearchResult
                    {
                        Name = collection.Name ?? searchInfo.Name,
                        Overview = collection.Overview,
                        SearchProviderName = Name,
                        ImageUrl = string.IsNullOrWhiteSpace(
                            collection.PosterPath)
                            ? null
                            : "https://image.tmdb.org/t/p/original"
                                + collection.PosterPath
                    };
                    result.SetProviderId(
                        MetadataProvider.Tmdb,
                        collection.Id.ToString(CultureInfo.InvariantCulture));
                    return result;
                })
                .ToArray()
                ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "ChooseYourMeta: TMDB collection search failed");
            return [];
        }
    }

    public Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken)
    {
        return _httpClientFactory
            .CreateClient("ChooseYourMeta")
            .GetAsync(url, cancellationToken);
    }

    private async Task<int?> ResolveCollectionIdFromMembers(
        BoxSet item,
        string apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var movieIds = item.GetLinkedChildren()
            .OfType<Movie>()
            .Select(movie => ParseTmdbId(movie.GetProviderId(MetadataProvider.Tmdb)))
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (movieIds.Length < 2)
        {
            return null;
        }

        var collectionIds = new List<int?>(movieIds.Length);
        foreach (var movieId in movieIds)
        {
            collectionIds.Add(await GetMovieCollectionId(
                movieId,
                apiKey,
                httpClient,
                cancellationToken));
        }

        return ResolveMemberConsensus(collectionIds);
    }

    internal static int? ResolveMemberConsensus(IEnumerable<int?> collectionIds)
    {
        var ids = collectionIds.ToArray();
        if (ids.Length < 2 || ids.Any(id => id is null or <= 0))
        {
            return null;
        }

        var first = ids[0]!.Value;
        return ids.All(id => id == first) ? first : null;
    }

    private async Task<int?> GetMovieCollectionId(
        int movieId,
        string apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        if (_movieCollectionIds.TryGetValue(movieId, out var cached))
        {
            return cached;
        }

        var url = $"{TmdbApiBase}/movie/{movieId.ToString(CultureInfo.InvariantCulture)}"
            + $"?api_key={Uri.EscapeDataString(apiKey)}"
            + "&language=en-US";
        using var response = await httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var movie = JsonSerializer.Deserialize<BoxSetTmdbMovieDetails>(json, JsonOptions.Default);
        var collectionId = movie?.BelongsToCollection?.Id;
        _movieCollectionIds[movieId] = collectionId;
        return collectionId;
    }

    private static async Task<TmdbCollectionDetails?> GetCollection(
        int tmdbId,
        string apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        if (tmdbId <= 0)
        {
            return null;
        }

        var url = $"{TmdbApiBase}/collection/{tmdbId.ToString(CultureInfo.InvariantCulture)}"
            + $"?api_key={Uri.EscapeDataString(apiKey)}"
            + "&language=ru-RU";
        using var response = await httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<TmdbCollectionDetails>(
            json,
            JsonOptions.Default);
    }

    private static int ParseTmdbId(string? value)
    {
        return int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var id)
            ? id
            : 0;
    }

    private static void ApplyRussianLanguagePreference(BoxSet item)
    {
        item.PreferredMetadataLanguage = "ru";
        item.PreferredMetadataCountryCode = "RU";
    }

    private static HttpClient CreateHttpClient(PluginConfiguration config)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(config.ProxyUrl)
            && Uri.TryCreate(config.ProxyUrl, UriKind.Absolute, out var proxyUri)
            && (proxyUri.Scheme == Uri.UriSchemeHttp
                || proxyUri.Scheme == Uri.UriSchemeHttps))
        {
            var proxy = new WebProxy(proxyUri);
            if (!string.IsNullOrWhiteSpace(config.ProxyUsername))
            {
                proxy.Credentials = new NetworkCredential(
                    config.ProxyUsername,
                    config.ProxyPassword);
            }

            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
}

internal sealed class TmdbCollectionSearchResponse
{
    public List<TmdbCollectionDetails>? Results { get; set; }
}

internal sealed class TmdbCollectionDetails
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public string? Overview { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
}

internal sealed class BoxSetTmdbMovieDetails
{
    [JsonPropertyName("belongs_to_collection")]
    public BoxSetTmdbCollectionReference? BelongsToCollection { get; set; }
}

internal sealed class BoxSetTmdbCollectionReference
{
    public int Id { get; set; }
}
