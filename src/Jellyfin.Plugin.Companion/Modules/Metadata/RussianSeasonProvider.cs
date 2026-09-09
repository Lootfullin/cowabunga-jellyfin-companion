using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace RussianMetadata;

public sealed class RussianSeasonProvider :
    IRemoteMetadataProvider<Season, SeasonInfo>,
    ICustomMetadataProvider<Season>
{
    private const string TmdbApiBase = "https://api.themoviedb.org/3";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RussianSeasonProvider> _logger;
    private CompanionPlugin CurrentPlugin => CompanionPlugin.Instance!;

    public RussianSeasonProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<RussianSeasonProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => CompanionPlugin.MetadataProviderName;

    public async Task<MetadataResult<Season>> GetMetadata(
        SeasonInfo info,
        CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.AllowsPath(CompanionModule.Metadata, info.Path)) return new MetadataResult<Season>();

        var result = new MetadataResult<Season>();
        int? seriesTmdbId = ExtractTmdbId(info.SeriesProviderIds);
        if (seriesTmdbId is null || info.IndexNumber is null)
        {
            _logger.LogInformation(
                "ChooseYourMeta (Season): Missing series TMDB ID or season number for '{Name}'",
                info.Name);
            return result;
        }

        var details = await FetchSeason(
            seriesTmdbId.Value,
            info.IndexNumber.Value,
            includePeople: true,
            cancellationToken);
        if (details is null)
        {
            return result;
        }

        var config = CurrentPlugin.Configuration;
        result.Item = new Season
        {
            IndexNumber = info.IndexNumber,
            Name = config.EnableRussianTitles
                ? MovieTextLocalization.RussianOrNull(details.Name)
                    ?? details.Name
                : info.Name,
            Overview = config.EnableRussianOverviews
                ? details.Overview
                : null,
            PremiereDate = ParseDate(details.AirDate),
            ProductionYear = ParseDate(details.AirDate)?.Year
        };
        result.Item.SetProviderId(
            "Tmdb",
            details.Id.ToString(CultureInfo.InvariantCulture));
        result.ResultLanguage = "ru";

        if (config.EnableRussianPeople && details.Credits is not null)
        {
            await TmdbPeopleLocalization.AddLocalizedPeople(
                details.Credits,
                result,
                _httpClientFactory,
                _logger,
                cancellationToken);
        }

        result.HasMetadata = true;
        _logger.LogInformation(
            "ChooseYourMeta (Season): Loaded TMDB {SeriesId} season {Season}: '{Name}', people={People}",
            seriesTmdbId,
            info.IndexNumber,
            result.Item.Name,
            result.People?.Count ?? 0);
        return result;
    }

    public async Task<ItemUpdateType> FetchAsync(
        Season item,
        MetadataRefreshOptions options,
        CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.Allows(CompanionModule.Metadata, item)) return ItemUpdateType.None;

        var series = item.FindParent<Series>();
        int? seriesTmdbId = series is null
            ? null
            : ExtractTmdbId(series.ProviderIds);
        if (seriesTmdbId is null || item.IndexNumber is null)
        {
            return ItemUpdateType.None;
        }

        var details = await FetchSeason(
            seriesTmdbId.Value,
            item.IndexNumber.Value,
            includePeople: false,
            cancellationToken);
        if (details is null)
        {
            return ItemUpdateType.None;
        }

        var config = CurrentPlugin.Configuration;
        bool changed = false;
        var russianName = MovieTextLocalization.RussianOrNull(details.Name);
        if (config.EnableRussianTitles
            && !string.IsNullOrWhiteSpace(russianName)
            && !string.Equals(
                item.Name,
                russianName,
                StringComparison.Ordinal))
        {
            item.Name = russianName;
            changed = true;
        }

        if (config.EnableRussianOverviews
            && !string.IsNullOrWhiteSpace(details.Overview)
            && !string.Equals(
                item.Overview,
                details.Overview,
                StringComparison.Ordinal))
        {
            item.Overview = details.Overview;
            changed = true;
        }

        if (changed)
        {
            _logger.LogInformation(
                "ChooseYourMeta (Season): Applied Russian data to TMDB {SeriesId} season {Season}: '{Name}'",
                seriesTmdbId,
                item.IndexNumber,
                item.Name);
            return ItemUpdateType.MetadataEdit;
        }

        return ItemUpdateType.None;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        SeasonInfo searchInfo,
        CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.AllowsPath(CompanionModule.Metadata, searchInfo.Path)) return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());

        return Task.FromResult(Enumerable.Empty<RemoteSearchResult>());
    }

    public Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken)
    {
        return _httpClientFactory.CreateClient("RussianMetadata")
            .GetAsync(url, cancellationToken);
    }

    private async Task<TmdbSeasonDetails?> FetchSeason(
        int seriesTmdbId,
        int seasonNumber,
        bool includePeople,
        CancellationToken cancellationToken)
    {
        var config = CurrentPlugin.Configuration;
        var apiKey = TmdbApiKeyResolver.Resolve(config);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning(
                "ChooseYourMeta (Season): TMDB integration unavailable");
            return null;
        }

        try
        {
            using var handler = CreateProxyHandler(config);
            using var httpClient = new HttpClient(
                handler,
                disposeHandler: true);
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept",
                "application/json");

            var url =
                $"{TmdbApiBase}/tv/{seriesTmdbId}/season/{seasonNumber}?api_key={Uri.EscapeDataString(apiKey)}&language=ru-RU";
            if (includePeople)
            {
                url += "&append_to_response=credits";
            }
            using var response = await httpClient.GetAsync(
                url,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ChooseYourMeta (Season): TMDB {SeriesId} season {Season} failed ({Status})",
                    seriesTmdbId,
                    seasonNumber,
                    response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);
            return JsonSerializer.Deserialize<TmdbSeasonDetails>(
                json,
                JsonOptions.Default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "ChooseYourMeta (Season): Failed TMDB {SeriesId} season {Season}",
                seriesTmdbId,
                seasonNumber);
            return null;
        }
    }

    private HttpClientHandler CreateProxyHandler(
        Configuration.PluginConfiguration config)
    {
        var handler = new HttpClientHandler();
        if (string.IsNullOrWhiteSpace(config.ProxyUrl))
        {
            return handler;
        }

        var proxyUri = new UriBuilder(config.ProxyUrl);
        if (proxyUri.Scheme is not ("http" or "https"))
        {
            return handler;
        }

        var proxy = new WebProxy(config.ProxyUrl);
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

    private static int? ExtractTmdbId(
        IReadOnlyDictionary<string, string> providerIds)
    {
        foreach (var pair in providerIds)
        {
            if (pair.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(
                    pair.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var id)
                && id > 0)
            {
                return id;
            }
        }

        return null;
    }

    private static DateTime? ParseDate(string? value)
    {
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var date)
            ? date
            : null;
    }
}

internal sealed class TmdbSeasonDetails
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    public TmdbCredits? Credits { get; set; }
}
