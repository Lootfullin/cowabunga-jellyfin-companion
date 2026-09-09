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

public sealed class ChooseYourMetaBoxSetImageProvider
    : IRemoteImageProvider,
        IDisposable
{
    private const string TmdbApiBase = "https://api.themoviedb.org/3";
    private const string FanartApiBase =
        "https://webservice.fanart.tv/v3.2/movies";
    private readonly Dictionary<string, HttpClient> _httpClients = [];
    private readonly object _httpClientLock = new();
    private readonly ILogger<ChooseYourMetaBoxSetImageProvider> _logger;

    public ChooseYourMetaBoxSetImageProvider(
        ILogger<ChooseYourMetaBoxSetImageProvider> logger)
    {
        _logger = logger;
    }

    public string Name => CompanionPlugin.MetadataImageProviderName;

    private PluginConfiguration Configuration =>
        CompanionPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public bool Supports(BaseItem item) => CompanionPlugin.GetConfiguration().MetadataImagesEnabled && LibraryPolicy.Allows(CompanionModule.Metadata, item) && (item is BoxSet);

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        if (Configuration.CollectionPosterPreference
            != ArtworkLanguagePreference.Disabled)
        {
            yield return ImageType.Primary;
        }

        if (Configuration.CollectionLogoPreference
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
        var tmdbId = ParseTmdbId(item.GetProviderId(MetadataProvider.Tmdb));
        if (tmdbId <= 0)
        {
            return [];
        }

        var result = new List<RemoteImageInfo>();
        if (config.CollectionPosterPreference
            != ArtworkLanguagePreference.Disabled)
        {
            result.AddRange(await GetTmdbPosters(
                tmdbId,
                config,
                cancellationToken));
        }

        if (config.CollectionLogoPreference
            != ArtworkLanguagePreference.Disabled)
        {
            result.AddRange(await GetFanartLogos(
                tmdbId,
                config,
                cancellationToken));
        }

        result = SelectPreferredLanguages(result, config).ToList();
        CompanionPlugin.Images?.RegisterCandidates(item, result);
        return result;
    }

    // Jellyfin reorders remote images by the item's metadata language after GetImages.
    // Returning both languages lets that override our independent artwork preference.
    internal static IEnumerable<RemoteImageInfo> SelectPreferredLanguages(
        IEnumerable<RemoteImageInfo> images, PluginConfiguration config)
    {
        foreach (var group in images.GroupBy(image => image.Type))
        {
            var preference = group.Key == ImageType.Primary
                ? config.CollectionPosterPreference : config.CollectionLogoPreference;
            if (preference == ArtworkLanguagePreference.Disabled) continue;
            var language = preference == ArtworkLanguagePreference.EnglishFirst ? "en" : "ru";
            var preferred = group.Where(image => string.Equals(image.Language, language, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var image in preferred.Count > 0 ? preferred : group.ToList()) yield return image;
        }
    }

    private async Task<IEnumerable<RemoteImageInfo>> GetTmdbPosters(
        int tmdbId,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var apiKey = TmdbApiKeyResolver.Resolve(config);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        try
        {
            var url =
                $"{TmdbApiBase}/collection/{tmdbId.ToString(CultureInfo.InvariantCulture)}"
                + $"?api_key={Uri.EscapeDataString(apiKey)}"
                + "&language=ru-RU"
                + "&append_to_response=images"
                + "&include_image_language=ru,en";
            using var response = await GetHttpClient(config).GetAsync(
                url,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(
                    cancellationToken);
                var collection =
                    JsonSerializer.Deserialize<TmdbCollectionArtworkResponse>(
                        json,
                        JsonOptions.Default);
                return ArtworkSelector.Select(
                    collection?.Images,
                    config.CollectionPosterPreference,
                    ArtworkLanguagePreference.Disabled,
                    Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "ChooseYourMeta: TMDB collection posters failed");
        }

        return [];
    }

    private async Task<IEnumerable<RemoteImageInfo>> GetFanartLogos(
        int tmdbId,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var apiKey = FanartApiKeyResolver.Resolve();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        try
        {
            var url =
                $"{FanartApiBase}/{tmdbId.ToString(CultureInfo.InvariantCulture)}"
                + $"?api_key={Uri.EscapeDataString(apiKey)}";
            using var response = await GetHttpClient(config).GetAsync(
                url,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(
                    cancellationToken);
                var artwork = JsonSerializer.Deserialize<FanartMovieArtwork>(
                    json,
                    JsonOptions.Default);
                return FanartLogoSelector.Select(
                    artwork,
                    config.CollectionLogoPreference,
                    Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "ChooseYourMeta: Fanart collection logos failed");
        }

        return [];
    }

    public async Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken)
    {
        var response = await GetHttpClient(Configuration).GetAsync(url, cancellationToken).ConfigureAwait(false);
        return CompanionPlugin.Images is { } images
            ? await images.TrackDownloadAsync(url, Name, response, cancellationToken).ConfigureAwait(false)
            : response;
    }

    public void Dispose()
    {
        lock (_httpClientLock)
        {
            foreach (var client in _httpClients.Values)
            {
                client.Dispose();
            }

            _httpClients.Clear();
        }
    }

    private HttpClient GetHttpClient(PluginConfiguration config)
    {
        var key = string.Join(
            "\n",
            config.ProxyUrl,
            config.ProxyUsername,
            config.ProxyPassword);
        lock (_httpClientLock)
        {
            if (_httpClients.TryGetValue(key, out var existingClient))
            {
                return existingClient;
            }

            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(config.ProxyUrl)
                && Uri.TryCreate(
                    config.ProxyUrl,
                    UriKind.Absolute,
                    out var proxyUri)
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

            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClients.Add(key, client);
            return client;
        }
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
}

internal sealed class TmdbCollectionArtworkResponse
{
    public TmdbArtworkImages? Images { get; set; }
}

internal sealed class FanartMovieArtwork
{
    [JsonPropertyName("hdmovielogo")]
    public List<FanartImage>? HdMovieLogos { get; set; }

    [JsonPropertyName("movielogo")]
    public List<FanartImage>? MovieLogos { get; set; }
}

internal sealed class FanartImage
{
    public string? Url { get; set; }

    [JsonPropertyName("lang")]
    public string? Language { get; set; }

    public string? Likes { get; set; }

    public string? Width { get; set; }

    public string? Height { get; set; }
}

internal static class FanartLogoSelector
{
    internal static IEnumerable<RemoteImageInfo> Select(
        FanartMovieArtwork? artwork,
        ArtworkLanguagePreference preference,
        string providerName)
    {
        if (preference == ArtworkLanguagePreference.Disabled)
        {
            return [];
        }

        return Convert(artwork?.HdMovieLogos, isHd: true)
            .Concat(Convert(artwork?.MovieLogos, isHd: false))
            .Where(image =>
                string.Equals(
                    image.Source.Language,
                    "ru",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    image.Source.Language,
                    "en",
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(image => GetLanguageRank(
                image.Source.Language,
                preference))
            .ThenByDescending(image => image.IsHd)
            .ThenByDescending(image => ParseInt(image.Source.Likes))
            .Select(image => new RemoteImageInfo
            {
                ProviderName = providerName,
                Url = image.Source.Url,
                ThumbnailUrl = image.Source.Url,
                Height = ParseNullableInt(image.Source.Height),
                Width = ParseNullableInt(image.Source.Width),
                CommunityRating = ParseInt(image.Source.Likes),
                Language = image.Source.Language!.ToLowerInvariant(),
                Type = ImageType.Logo,
                RatingType = RatingType.Likes
            });
    }

    private static IEnumerable<(FanartImage Source, bool IsHd)> Convert(
        IEnumerable<FanartImage>? images,
        bool isHd)
    {
        return images?
            .Where(image => !string.IsNullOrWhiteSpace(image.Url))
            .Select(image => (image, isHd))
            ?? [];
    }

    private static int GetLanguageRank(
        string? language,
        ArtworkLanguagePreference preference)
    {
        var preferred =
            preference == ArtworkLanguagePreference.RussianFirst ? "ru" : "en";
        return string.Equals(
            language,
            preferred,
            StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : 0;
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : null;
    }
}
