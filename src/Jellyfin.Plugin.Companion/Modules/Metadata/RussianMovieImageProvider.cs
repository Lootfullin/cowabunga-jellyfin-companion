using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Companion.Configuration;

namespace RussianMetadata;

/// <summary>
/// Applies the configured RU/EN TMDb artwork priority separately to Russian
/// and foreign movies. Local images remain under Jellyfin's normal precedence.
/// </summary>
public sealed class RussianMovieImageProvider : IRemoteImageProvider, IDisposable
{
    private const string TmdbApiBase = "https://api.themoviedb.org/3";
    private readonly object _httpClientLock = new();
    private readonly ILogger<RussianMovieImageProvider> _logger;
    private readonly Dictionary<string, HttpClient> _httpClients = [];

    public RussianMovieImageProvider(ILogger<RussianMovieImageProvider> logger)
    {
        _logger = logger;
    }

    public string Name => CompanionPlugin.MetadataImageProviderName;

    private PluginConfiguration Configuration =>
        CompanionPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public bool Supports(BaseItem item) => CompanionPlugin.GetConfiguration().MetadataImagesEnabled && LibraryPolicy.Allows(CompanionModule.Metadata, item) && (item is Movie);

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        var config = Configuration;
        if (config.ForeignMoviePosterPreference
                != ArtworkLanguagePreference.Disabled
            || config.RussianMoviePosterPreference
                != ArtworkLanguagePreference.Disabled)
        {
            yield return ImageType.Primary;
        }

        if (config.ForeignMovieLogoPreference
                != ArtworkLanguagePreference.Disabled
            || config.RussianMovieLogoPreference
                != ArtworkLanguagePreference.Disabled)
        {
            yield return ImageType.Logo;
        }
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        if (!Supports(item)) return Array.Empty<RemoteImageInfo>();

        var config = Configuration;
        var tmdbApiKey = TmdbApiKeyResolver.Resolve(config);
        if (string.IsNullOrWhiteSpace(tmdbApiKey)
            || (config.ForeignMoviePosterPreference
                    == ArtworkLanguagePreference.Disabled
                && config.ForeignMovieLogoPreference
                    == ArtworkLanguagePreference.Disabled
                && config.RussianMoviePosterPreference
                    == ArtworkLanguagePreference.Disabled
                && config.RussianMovieLogoPreference
                    == ArtworkLanguagePreference.Disabled))
        {
            return [];
        }

        try
        {
            var httpClient = GetHttpClient(config);

            var tmdbId = await ResolveTmdbId(
                item,
                tmdbApiKey,
                httpClient,
                cancellationToken);
            if (tmdbId <= 0)
            {
                return [];
            }

            var detailsUrl =
                $"{TmdbApiBase}/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}"
                + $"?api_key={Uri.EscapeDataString(tmdbApiKey)}"
                + "&language=ru-RU"
                + "&append_to_response=images"
                + "&include_image_language=ru,en";
            using var response = await httpClient.GetAsync(
                detailsUrl,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "RussianMetadata images: TMDB request failed for movie {TmdbId} ({Status})",
                    tmdbId,
                    response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var movie = JsonSerializer.Deserialize<TmdbMovieArtworkResponse>(
                json,
                JsonOptions.Default);
            var isRussianMovie = MovieOriginClassifier.IsRussian(movie);
            var posterPreference = isRussianMovie
                ? config.RussianMoviePosterPreference
                : config.ForeignMoviePosterPreference;
            var logoPreference = isRussianMovie
                ? config.RussianMovieLogoPreference
                : config.ForeignMovieLogoPreference;
            var result = ArtworkSelector.Select(
                movie?.Images,
                posterPreference,
                logoPreference,
                Name);

            _logger.LogInformation(
                "ChooseYourMeta images: movie {TmdbId} classified as {Origin}; found {Count} RU/EN candidates",
                tmdbId,
                isRussianMovie ? "Russian" : "Foreign",
                result.Count);
            CompanionPlugin.Images?.RegisterCandidates(item, result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "ChooseYourMeta images: failed; the next Jellyfin image provider remains available");
            return [];
        }
    }

    public async Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken)
    {
        var response = await GetHttpClient(Configuration).GetAsync(
            url,
            cancellationToken);
        return CompanionPlugin.Images is { } images
            ? await images.TrackDownloadAsync(url, Name, response, cancellationToken).ConfigureAwait(false)
            : response;
    }

    public void Dispose()
    {
        lock (_httpClientLock)
        {
            foreach (var httpClient in _httpClients.Values)
            {
                httpClient.Dispose();
            }

            _httpClients.Clear();
        }
    }

    private static async Task<int> ResolveTmdbId(
        BaseItem item,
        string apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        if (int.TryParse(
            item.GetProviderId(MetadataProvider.Tmdb),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var tmdbId)
            && tmdbId > 0)
        {
            return tmdbId;
        }

        var imdbId = item.GetProviderId(MetadataProvider.Imdb);
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return 0;
        }

        var findUrl =
            $"{TmdbApiBase}/find/{Uri.EscapeDataString(imdbId)}"
            + $"?api_key={Uri.EscapeDataString(apiKey)}"
            + "&external_source=imdb_id";
        using var response = await httpClient.GetAsync(findUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var find = JsonSerializer.Deserialize<TmdbFindResult>(
            json,
            JsonOptions.Default);
        return find?.MovieResults?.FirstOrDefault()?.Id ?? 0;
    }

    private static HttpClientHandler CreateProxyHandler(
        PluginConfiguration config)
    {
        var handler = new HttpClientHandler();
        if (string.IsNullOrWhiteSpace(config.ProxyUrl)
            || !Uri.TryCreate(config.ProxyUrl, UriKind.Absolute, out var proxyUri)
            || (proxyUri.Scheme != Uri.UriSchemeHttp
                && proxyUri.Scheme != Uri.UriSchemeHttps))
        {
            return handler;
        }

        var proxy = new WebProxy(proxyUri);
        if (!string.IsNullOrWhiteSpace(config.ProxyUsername))
        {
            proxy.Credentials = new NetworkCredential(
                config.ProxyUsername,
                config.ProxyPassword);
        }

        handler.Proxy = proxy;
        handler.UseProxy = true;
        return handler;
    }

    private HttpClient GetHttpClient(PluginConfiguration config)
    {
        var configurationKey = string.Join(
            "\n",
            config.ProxyUrl,
            config.ProxyUsername,
            config.ProxyPassword);

        lock (_httpClientLock)
        {
            if (_httpClients.TryGetValue(
                configurationKey,
                out var existingClient))
            {
                return existingClient;
            }

            var httpClient = new HttpClient(
                CreateProxyHandler(config),
                disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept",
                "application/json");
            _httpClients.Add(configurationKey, httpClient);
            return httpClient;
        }
    }
}

internal static class ArtworkSelector
{
    private const string OriginalImageBase = "https://image.tmdb.org/t/p/original";
    private const string PosterThumbnailBase = "https://image.tmdb.org/t/p/w342";
    private const string LogoThumbnailBase = "https://image.tmdb.org/t/p/w500";

    internal static List<RemoteImageInfo> Select(
        TmdbArtworkImages? images,
        ArtworkLanguagePreference posterPreference,
        ArtworkLanguagePreference logoPreference,
        string providerName)
    {
        var result = new List<RemoteImageInfo>();
        if (posterPreference != ArtworkLanguagePreference.Disabled)
        {
            result.AddRange(Convert(
                images?.Posters,
                ImageType.Primary,
                PosterThumbnailBase,
                posterPreference,
                providerName));
        }

        if (logoPreference != ArtworkLanguagePreference.Disabled)
        {
            result.AddRange(Convert(
                images?.Logos,
                ImageType.Logo,
                LogoThumbnailBase,
                logoPreference,
                providerName));
        }

        return result;
    }

    private static IEnumerable<RemoteImageInfo> Convert(
        IEnumerable<TmdbImageFile>? images,
        ImageType type,
        string thumbnailBase,
        ArtworkLanguagePreference preference,
        string providerName)
    {
        return images?
            .Where(image =>
                IsSupportedLanguage(image.Language)
                && !string.IsNullOrWhiteSpace(image.FilePath))
            .OrderBy(image => GetLanguageRank(image.Language, preference))
            .ThenByDescending(image => image.VoteAverage ?? 0)
            .ThenByDescending(image => image.VoteCount ?? 0)
            .Select(image => new RemoteImageInfo
            {
                ProviderName = providerName,
                Url = OriginalImageBase + image.FilePath,
                ThumbnailUrl = thumbnailBase + image.FilePath,
                Height = image.Height,
                Width = image.Width,
                CommunityRating = image.VoteAverage,
                VoteCount = image.VoteCount,
                Language = image.Language!.ToLowerInvariant(),
                Type = type,
                RatingType = RatingType.Score
            })
            ?? [];
    }

    private static bool IsSupportedLanguage(string? language)
    {
        return string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetLanguageRank(
        string? language,
        ArtworkLanguagePreference preference)
    {
        var preferredLanguage =
            preference == ArtworkLanguagePreference.RussianFirst ? "ru" : "en";
        return string.Equals(
            language,
            preferredLanguage,
            StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;
    }
}

internal static class MovieOriginClassifier
{
    internal static bool IsRussian(TmdbMovieArtworkResponse? movie)
    {
        if (movie is null)
        {
            return false;
        }

        var countries = (movie.OriginCountry ?? [])
            .Concat(movie.ProductionCountries?
                .Select(country => country.Code)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                ?? [])
            .ToArray();
        if (countries.Any(country =>
            string.Equals(country, "RU", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Country data is authoritative when TMDb supplies it. Original
        // language is only a fallback for older or incomplete movie records.
        return countries.Length == 0
            && string.Equals(
                movie.OriginalLanguage,
                "ru",
                StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class TmdbMovieArtworkResponse
{
    [JsonPropertyName("origin_country")]
    public List<string>? OriginCountry { get; set; }

    [JsonPropertyName("production_countries")]
    public List<TmdbProductionCountry>? ProductionCountries { get; set; }

    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; set; }

    public TmdbArtworkImages? Images { get; set; }
}

internal sealed class TmdbProductionCountry
{
    [JsonPropertyName("iso_3166_1")]
    public string? Code { get; set; }
}

internal sealed class TmdbArtworkImages
{
    public List<TmdbImageFile>? Posters { get; set; }

    public List<TmdbImageFile>? Logos { get; set; }
}

internal sealed class TmdbImageFile
{
    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }

    [JsonPropertyName("iso_639_1")]
    public string? Language { get; set; }

    public int? Height { get; set; }

    public int? Width { get; set; }

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; set; }
}
