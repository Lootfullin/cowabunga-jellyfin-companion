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

public partial class RussianSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, ICustomMetadataProvider<Series>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RussianSeriesProvider> _logger;
    private CompanionPlugin CurrentPlugin => CompanionPlugin.Instance!;

    private const string TmdbApiBase = "https://api.themoviedb.org/3";
    private const string WikidataSparqlEndpoint = "https://query.wikidata.org/sparql";

    [GeneratedRegex(@"\b(tt\d{7,8})\b")]
    private static partial Regex ImdbIdRegex();

    public RussianSeriesProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<RussianSeriesProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => CompanionPlugin.MetadataProviderName;

    public async Task<MetadataResult<Series>> GetMetadata(
        SeriesInfo info,
        CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.AllowsPath(CompanionModule.Metadata, info.Path)) return new MetadataResult<Series>();

        var result = new MetadataResult<Series>();

        string? imdbId = ExtractImdbId(info);
        int? tmdbId = MovieLookup.ExtractTmdbId(info.ProviderIds);

        _logger.LogInformation(
            "RussianMetadata (Series): GetMetadata — Name='{Name}', Path='{Path}', TmdbId={TmdbId}, ImdbId={ImdbId}",
            info.Name,
            info.Path,
            tmdbId?.ToString(CultureInfo.InvariantCulture) ?? "N/A",
            imdbId ?? "N/A");

        var config = CurrentPlugin.Configuration;
        bool tmdbSuccess = false;
        var tmdbApiKey = TmdbApiKeyResolver.Resolve(config);
        if (!string.IsNullOrWhiteSpace(tmdbApiKey))
        {
            tmdbSuccess = await TryTmdbSeries(
                info.Name,
                info.Year,
                tmdbId,
                imdbId,
                config,
                tmdbApiKey,
                result,
                includePeople: true,
                cancellationToken);
        }

        bool wikidataSuccess = false;
        if (!tmdbSuccess && !string.IsNullOrWhiteSpace(imdbId))
        {
            wikidataSuccess = await TryWikidata(
                imdbId,
                config,
                result,
                cancellationToken);
        }

        if (!tmdbSuccess && !wikidataSuccess)
        {
            return new MetadataResult<Series>();
        }

        result.Item ??= new Series();
        if (string.IsNullOrWhiteSpace(result.Item.Name))
        {
            result.Item.Name = info.Name;
        }

        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            result.Item.SetProviderId("Imdb", imdbId);
        }

        result.HasMetadata = true;
        result.ResultLanguage = "ru";
        return result;
    }

    // ───── ICustomMetadataProvider: runs AFTER all remote providers to apply Russian data ─────

    public async Task<ItemUpdateType> FetchAsync(Series item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.Allows(CompanionModule.Metadata, item)) return ItemUpdateType.None;

        // Try to get IMDb ID from provider IDs (e.g., from NFO or other metadata provider)
        if (!item.TryGetProviderId("Imdb", out var imdbId) || string.IsNullOrEmpty(imdbId))
        {
            _logger.LogInformation("RussianMetadata (Series): No IMDb ID in ProviderIds for {Name}, trying path", item.Name);
        }

        // Fallback: extract IMDb ID from series folder path (e.g., ".../tt32603540 Landyshi.../")
        if (string.IsNullOrEmpty(imdbId) && !string.IsNullOrEmpty(item.Path))
        {
            var match = ImdbIdRegex().Match(item.Path);
            if (match.Success)
            {
                imdbId = match.Groups[1].Value;
                _logger.LogInformation("RussianMetadata (Series): Extracted IMDb ID {ImdbId} from path '{Path}'", imdbId, item.Path);
            }
        }

        // Derive series name from the actual folder path rather than item.Name,
        // which may incorrectly contain the parent/root library folder name.
        var config = CurrentPlugin.Configuration;
        var seriesName = item.Name;
        int? knownTmdbId = MovieLookup.ExtractTmdbId(item.ProviderIds);
        if (!string.IsNullOrEmpty(item.Path))
        {
            var folderName = GetFolderNameFromPath(item.Path);
            if (!string.IsNullOrEmpty(folderName))
            {
                seriesName = CleanSeriesName(folderName);
                if (seriesName != item.Name)
                {
                    _logger.LogInformation("RussianMetadata (Series): Using path-derived name '{DerivedName}' instead of item.Name '{ItemName}'", seriesName, item.Name);
                }
            }
        }

        var tempResult = new MetadataResult<Series>();
        bool tmdbSuccess = false;
        bool wikidataSuccess = false;
        var tmdbApiKey = TmdbApiKeyResolver.Resolve(config);

        // Step 1: Try TMDB
        if (!string.IsNullOrEmpty(tmdbApiKey))
        {
            _logger.LogInformation("RussianMetadata (Series): FetchAsync Step 1 — Trying TMDB for {Name}", seriesName);
            tmdbSuccess = await TryTmdbSeries(
                seriesName,
                item.ProductionYear,
                knownTmdbId,
                imdbId,
                config,
                tmdbApiKey,
                tempResult,
                includePeople: false,
                cancellationToken);
        }

        // Step 2: Wikidata by IMDb ID
        if (!tmdbSuccess && !string.IsNullOrEmpty(imdbId))
        {
            _logger.LogInformation("RussianMetadata (Series): FetchAsync Step 2 — Wikidata by ID {ImdbId}", imdbId);
            wikidataSuccess = await TryWikidata(imdbId, config, tempResult, cancellationToken);
        }

        if ((tmdbSuccess || wikidataSuccess) && tempResult.Item != null)
        {
            var resolvedTmdbId = MovieLookup.ExtractTmdbId(tempResult.Item.ProviderIds);
            var identityChanged = resolvedTmdbId is not null
                && resolvedTmdbId != knownTmdbId;
            if (identityChanged)
            {
                item.SetProviderId(
                    MetadataProvider.Tmdb,
                    resolvedTmdbId!.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (config.EnableRussianTitles && !string.IsNullOrEmpty(tempResult.Item.Name))
            {
                item.Name = tempResult.Item.Name;
                _logger.LogInformation("RussianMetadata (Series): FetchAsync — set name to {Name}", item.Name);
            }

            if (config.EnableRussianOverviews && !string.IsNullOrEmpty(tempResult.Item.Overview))
            {
                item.Overview = tempResult.Item.Overview;
                _logger.LogInformation("RussianMetadata (Series): FetchAsync — set overview ({Len} chars)", item.Overview.Length);
            }

            return ItemUpdateType.MetadataEdit;
        }

        _logger.LogInformation("RussianMetadata (Series): FetchAsync — no Russian data for {Name}", item.Name);
        return ItemUpdateType.None;
    }

    private async Task<bool> TryTmdbSeries(
        string name,
        int? year,
        int? knownTmdbId,
        string? imdbId,
        Configuration.PluginConfiguration config,
        string tmdbApiKey,
        MetadataResult<Series> result,
        bool includePeople,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("RussianMetadata (Series): Trying TMDB for {Name}", name);

        try
        {
            var handler = CreateProxyHandler(config);
            using var httpClient = new HttpClient(handler, disposeHandler: true);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            httpClient.Timeout = TimeSpan.FromSeconds(5);

            int? tmdbId = null;

            // Path A: Find by IMDb ID
            if (!string.IsNullOrEmpty(imdbId))
            {
                var findUrl = $"{TmdbApiBase}/find/{imdbId}?api_key={Uri.EscapeDataString(tmdbApiKey)}&external_source=imdb_id";
                _logger.LogInformation("RussianMetadata (Series): TMDB find URL (without key): {Url}", findUrl.Replace(tmdbApiKey, "***"));
                _logger.LogInformation("RussianMetadata (Series): TMDB find for {ImdbId}", imdbId);

                using var findResponse = await httpClient.GetAsync(findUrl, cancellationToken);
                _logger.LogInformation("RussianMetadata (Series): TMDB find response status: {Status}", (int)findResponse.StatusCode);
                if (findResponse.IsSuccessStatusCode)
                {
                    var findJson = await findResponse.Content.ReadAsStringAsync(cancellationToken);
                    var findData = JsonSerializer.Deserialize<TmdbFindResult>(findJson, JsonOptions.Default);
                    var tvRef = findData?.TvResults is { Count: 1 }
                        ? findData.TvResults[0]
                        : null;
                    tmdbId = tvRef?.Id;
                    if (tmdbId is not null
                        && knownTmdbId is not null
                        && tmdbId != knownTmdbId)
                    {
                        _logger.LogWarning(
                            "ChooseYourMeta: correcting series TMDB ID {OldTmdbId} -> {NewTmdbId} using IMDb {ImdbId}",
                            knownTmdbId,
                            tmdbId,
                            imdbId);
                    }
                    _logger.LogInformation("RussianMetadata (Series): TMDB find result — tvResults={Count}, tmdbId={Id}", findData?.TvResults?.Count ?? 0, tmdbId?.ToString() ?? "N/A");
                }
                else
                {
                    _logger.LogWarning("RussianMetadata (Series): TMDB find failed ({Status})", findResponse.StatusCode);
                }
            }

            // Preserve a known Jellyfin identity unless an exact IMDb mapping
            // above proved that it was stale.
            tmdbId ??= knownTmdbId;

            // Path B: No IMDb ID or not found by IMDb — search by name
            if (tmdbId == null && !string.IsNullOrEmpty(name))
            {
                var query = Uri.EscapeDataString(name);
                var searchUrl = $"{TmdbApiBase}/search/tv?api_key={Uri.EscapeDataString(tmdbApiKey)}&language=ru-RU&query={query}";
                _logger.LogInformation("RussianMetadata (Series): TMDB search URL (without key): {Url}", searchUrl.Replace(tmdbApiKey, "***"));
                _logger.LogInformation("RussianMetadata (Series): TMDB search by name: {Name}", name);

                using var searchResponse = await httpClient.GetAsync(searchUrl, cancellationToken);
                _logger.LogInformation("RussianMetadata (Series): TMDB search response status: {Status}", (int)searchResponse.StatusCode);
                if (searchResponse.IsSuccessStatusCode)
                {
                    var searchJson = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
                    var searchResult = JsonSerializer.Deserialize<SeriesTmdbTvSearchResponse>(searchJson, JsonOptions.Default);
                    var selected = SeriesLookup.SelectCandidate(
                        searchResult?.Results,
                        new SeriesLookup(name, year));
                    if (selected is not null)
                    {
                        tmdbId = selected.Id;
                        _logger.LogInformation(
                            "RussianMetadata (Series): TMDB search — {Name} ({Year}) safely matched ID={Id}",
                            name,
                            year,
                            tmdbId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "RussianMetadata (Series): TMDB search by name was empty or ambiguous for {Name} ({Year})",
                            name,
                            year);
                    }
                }
                else
                {
                    _logger.LogWarning("RussianMetadata (Series): TMDB search failed ({Status})", searchResponse.StatusCode);
                }
            }

            if (tmdbId == null)
            {
                _logger.LogWarning("RussianMetadata (Series): No TMDB TV entry for {Name}", name);
                return false;
            }

            // Step 2: Fetch details in Russian
            var detailsUrl = $"{TmdbApiBase}/tv/{tmdbId}?api_key={Uri.EscapeDataString(tmdbApiKey)}&language=ru-RU";
            if (includePeople)
            {
                detailsUrl += "&append_to_response=credits";
            }
            _logger.LogInformation("RussianMetadata (Series): TMDB details URL (without key): {Url}", detailsUrl.Replace(tmdbApiKey, "***"));
            _logger.LogInformation("RussianMetadata (Series): TMDB details for ID {TmdbId}", tmdbId);

            using var detailsResponse = await httpClient.GetAsync(detailsUrl, cancellationToken);
            _logger.LogInformation("RussianMetadata (Series): TMDB details response status: {Status}", (int)detailsResponse.StatusCode);
            if (!detailsResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("RussianMetadata (Series): TMDB details failed ({Status})", detailsResponse.StatusCode);
                return false;
            }

            var detailsJson = await detailsResponse.Content.ReadAsStringAsync(cancellationToken);
            var tvDetails = JsonSerializer.Deserialize<SeriesTmdbTvDetails>(detailsJson, JsonOptions.Default);
            _logger.LogInformation("RussianMetadata (Series): TMDB details parsed — Name={N}, OriginalName={On}, OverviewLen={Ol}", tvDetails?.Name ?? "N", tvDetails?.OriginalName ?? "N", tvDetails?.Overview?.Length ?? 0);

            if (tvDetails == null) return false;

            // Initialize result item (may be null if no IMDb ID was available)
            result.HasMetadata = true;
            result.Item ??= new Series();

            // TMDB runs first now, so always apply its data
            if (!string.IsNullOrEmpty(tvDetails.Name) && config.EnableRussianTitles)
            {
                result.Item.Name = tvDetails.Name;
                _logger.LogInformation("RussianMetadata (Series): TMDB — set name: {Name}", tvDetails.Name);
            }

            if (!string.IsNullOrEmpty(tvDetails.Overview) && config.EnableRussianOverviews)
            {
                result.Item.Overview = tvDetails.Overview;
                _logger.LogInformation("RussianMetadata (Series): TMDB — set overview ({Len} chars)", tvDetails.Overview.Length);
            }

            if (!string.IsNullOrEmpty(tvDetails.OriginalName))
            {
                result.Item.OriginalTitle = tvDetails.OriginalName;
            }

            if (config.EnableRussianGenres
                && tvDetails.Genres is { Count: > 0 })
            {
                result.Item.Genres = tvDetails.Genres
                    .Select(genre => genre.Name?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToArray();
            }

            if (includePeople
                && config.EnableRussianPeople
                && tvDetails.Credits is not null)
            {
                await TmdbPeopleLocalization.AddLocalizedPeople(
                    tvDetails.Credits,
                    result,
                    _httpClientFactory,
                    _logger,
                    cancellationToken);
            }

            result.Item.SetProviderId("Tmdb", tmdbId.Value.ToString(CultureInfo.InvariantCulture));

            _logger.LogInformation("RussianMetadata (Series): TMDB success for {Name} -> {Title}", name, tvDetails.Name);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RussianMetadata (Series): TMDB failed for {Name}, will fallback", name);
            return false;
        }
    }

    private async Task<bool> TryWikidata(string imdbId, Configuration.PluginConfiguration config,
        MetadataResult<Series> result, CancellationToken cancellationToken)
    {
        _logger.LogInformation("RussianMetadata (Series): Wikidata lookup for {ImdbId}", imdbId);

        try
        {
            var entity = await FetchWikidataEntityByImdbId(imdbId, cancellationToken);
            if (entity == null)
            {
                _logger.LogWarning("RussianMetadata (Series): No Wikidata entity for {ImdbId}", imdbId);
                return false;
            }

            result.Item ??= new Series();
            bool changed = false;

            // Try Russian label first
            bool hasRussianLabel = !string.IsNullOrEmpty(entity.RussianLabel)
                && !entity.RussianLabel.StartsWith("Q", StringComparison.Ordinal);

            if (hasRussianLabel && config.EnableRussianTitles)
            {
                result.Item.Name = entity.RussianLabel;
                changed = true;
                _logger.LogInformation("RussianMetadata (Series): Wikidata — set Russian name: {Name}", entity.RussianLabel);
            }

            // Only set Russian overview; never English (preserve NFO data)
            if (!string.IsNullOrEmpty(entity.RussianDescription) && config.EnableRussianOverviews)
            {
                result.Item.Overview = entity.RussianDescription;
                changed = true;
                _logger.LogInformation("RussianMetadata (Series): Wikidata — set RU overview: {Desc}", entity.RussianDescription);
            }

            // Return true only if we applied RUSSIAN data
            // For English-only data, return false to preserve NFO/other provider data
            if (changed)
            {
                _logger.LogInformation("RussianMetadata (Series): Wikidata RU data applied for {ImdbId}", imdbId);
            }
            else
            {
                _logger.LogInformation("RussianMetadata (Series): Wikidata entity found for {ImdbId} but no RU data to apply (keeping NFO/other data)", imdbId);
            }

            return changed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RussianMetadata (Series): Wikidata error for {ImdbId}", imdbId);
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
                _logger.LogInformation("RussianMetadata (Series): Using proxy {Proxy}", config.ProxyUrl);
            }
        }

        return handler;
    }

    // ───── IMDb ID extraction ─────

    private string? ExtractImdbId(SeriesInfo info)
    {
        if (info.ProviderIds.TryGetValue("Imdb", out var id))
            return id;

        if (!string.IsNullOrEmpty(info.Path))
        {
            var match = ImdbIdRegex().Match(info.Path);
            if (match.Success)
                return match.Groups[1].Value;
        }

        if (!string.IsNullOrEmpty(info.Name))
        {
            var match = ImdbIdRegex().Match(info.Name);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Escape characters unsafe for SPARQL string literals (backslash, double-quote).
    /// Does NOT URL-encode – the SPARQL query is URL-encoded once at the HTTP layer.
    /// </summary>
    private static string EscapeSparqlString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Strip "ttXXXXXX " prefix from series folder names like "tt9169516 Run Away" -> "Run Away"
    /// </summary>
    private static string CleanSeriesName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name ?? string.Empty;
        // Remove "ttXXXXXXXX " prefix
        var cleaned = ImdbIdRegex().Replace(name, "").TrimStart();
        return string.IsNullOrWhiteSpace(cleaned) ? name : cleaned;
    }

    /// <summary>
    /// Extract the last directory/folder name from a file path.
    /// Works on both Windows (\\) and Unix (/) paths.
    /// e.g., "/data/tvshows/Сериалы бабушки/tt32603540 Landyshi. Takaya nezhnaya lyubov/"
    ///       -> "tt32603540 Landyshi. Takaya nezhnaya lyubov"
    /// </summary>
    private static string GetFolderNameFromPath(string path)
    {
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
    }

    // ───── Wikidata ─────

    private async Task<WikidataEntity?> FetchWikidataEntityByImdbId(string imdbId, CancellationToken cancellationToken)
    {
        // Use raw HttpClient to avoid any IHttpClientFactory issues
        using var httpClient = _httpClientFactory.CreateClient("RussianMetadata");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Jellyfin-RussianMetadata/1.0");
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        // Fetch both RU and EN labels/descriptions in one query
        var sparqlQuery = $@"
SELECT ?item ?ruLabel ?enLabel ?ruDescription ?enDescription WHERE {{
  ?item wdt:P345 ""{imdbId}"".
  OPTIONAL {{ ?item rdfs:label ?ruLabel. FILTER(LANG(?ruLabel) = ""ru"") }}
  OPTIONAL {{ ?item rdfs:label ?enLabel. FILTER(LANG(?enLabel) = ""en"") }}
  OPTIONAL {{ ?item schema:description ?ruDescription. FILTER(LANG(?ruDescription) = ""ru"") }}
  OPTIONAL {{ ?item schema:description ?enDescription. FILTER(LANG(?enDescription) = ""en"") }}
}}
LIMIT 1";
        var url = $"{WikidataSparqlEndpoint}?format=json&query={Uri.EscapeDataString(sparqlQuery)}";

        _logger.LogInformation("RussianMetadata (Series): Wikidata query for {ImdbId}", imdbId);

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("RussianMetadata (Series): Wikidata HTTP {Status} for {ImdbId}", (int)response.StatusCode, imdbId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var sparqlResult = JsonSerializer.Deserialize<SparqlResult>(json, JsonOptions.Default);
            var binding = sparqlResult?.Results?.Bindings?.Count > 0 ? sparqlResult.Results.Bindings[0] : null;

            if (binding == null)
            {
                _logger.LogWarning("RussianMetadata (Series): No Wikidata bindings for {ImdbId}", imdbId);
                return null;
            }

            _logger.LogInformation("RussianMetadata (Series): Wikidata found EN label '{label}', RU label '{ru}' for {ImdbId}",
                binding.EnLabel?.Value ?? "?", binding.RuLabel?.Value ?? "?", imdbId);

            return new WikidataEntity
            {
                EntityId = binding.Item?.Value,
                RussianLabel = binding.RuLabel?.Value,
                EnglishLabel = binding.EnLabel?.Value,
                RussianDescription = binding.RuDescription?.Value,
                EnglishDescription = binding.EnDescription?.Value
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RussianMetadata (Series): Wikidata fetch error for {ImdbId}", imdbId);
            return null;
        }
    }

    // ───── Search ─────

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.AllowsPath(CompanionModule.Metadata, searchInfo.Path)) return Array.Empty<RemoteSearchResult>();

        var results = new List<RemoteSearchResult>();

        var config = CurrentPlugin.Configuration;
        var tmdbApiKey = TmdbApiKeyResolver.Resolve(config);

        // Try TMDB search first
        if (!string.IsNullOrEmpty(tmdbApiKey))
        {
            try
            {
                var handler = CreateProxyHandler(config);
                using var httpClient = new HttpClient(handler, disposeHandler: true);
                httpClient.Timeout = TimeSpan.FromSeconds(5);

                var query = Uri.EscapeDataString(searchInfo.Name);
                var searchUrl = $"{TmdbApiBase}/search/tv?api_key={Uri.EscapeDataString(tmdbApiKey)}&language=ru-RU&query={query}";

                using var response = await httpClient.GetAsync(searchUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var searchResult = JsonSerializer.Deserialize<SeriesTmdbTvSearchResponse>(json, JsonOptions.Default);
                    if (searchResult?.Results != null)
                    {
                        foreach (var s in searchResult.Results)
                        {
                            var sr = new RemoteSearchResult
                            {
                                Name = s.Name ?? s.OriginalName ?? "Unknown",
                                Overview = s.Overview,
                                SearchProviderName = Name,
                                ProductionYear = s.FirstAirDate?.Length >= 4
                                    && int.TryParse(s.FirstAirDate[..4], out var yr) ? yr : null
                            };
                            sr.SetProviderId("Tmdb", s.Id.ToString(CultureInfo.InvariantCulture));
                            results.Add(sr);
                        }
                        return results;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "RussianMetadata (Series): TMDB search failed, fallback to Wikidata");
            }
        }

        // Fallback: Wikidata search for TV series (Q15416 = television series)
        try
        {
            using var httpClient = _httpClientFactory.CreateClient("RussianMetadata");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Jellyfin-RussianMetadata/1.0");
            httpClient.Timeout = TimeSpan.FromSeconds(5);

            var safeName = EscapeSparqlString(searchInfo.Name);
            var sparqlQuery = $@"
SELECT ?item ?itemLabel ?description WHERE {{
  ?item wdt:P31/wdt:P279* wd:Q15416.
  ?item rdfs:label ?itemLabel.
  FILTER(LANG(?itemLabel) = ""ru"")
  FILTER(CONTAINS(LCASE(?itemLabel), LCASE(""{safeName}"")))
}}
LIMIT 10";
            var url = $"{WikidataSparqlEndpoint}?format=json&query={Uri.EscapeDataString(sparqlQuery)}";

            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var sparqlResult = JsonSerializer.Deserialize<SparqlResult>(json, JsonOptions.Default);
                if (sparqlResult?.Results?.Bindings != null)
                {
                    foreach (var binding in sparqlResult.Results.Bindings)
                    {
                        var sr = new RemoteSearchResult
                        {
                            Name = binding.ItemLabel?.Value ?? "Unknown",
                            Overview = binding.Description?.Value,
                            SearchProviderName = Name,
                        };
                        results.Add(sr);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RussianMetadata (Series): Wikidata search error");
        }

        return results;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient("RussianMetadata");
        return httpClient.GetAsync(url, cancellationToken);
    }
}

// ───── TMDB JSON models for TV ─────

internal class SeriesTmdbTvRef
{
    public int Id { get; set; }
}

internal class SeriesTmdbTvDetails
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }
    public List<TmdbGenre>? Genres { get; set; }
    public TmdbCredits? Credits { get; set; }
}

internal class SeriesTmdbTvSearchResponse
{
    public List<SeriesTmdbTvSearchItem>? Results { get; set; }
}

internal class SeriesTmdbTvSearchItem
{
    public int Id { get; set; }
    public string? Name { get; set; }
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }
    public string? Overview { get; set; }
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }
}

internal sealed record SeriesLookup(string Name, int? Year)
{
    public static SeriesTmdbTvSearchItem? SelectCandidate(
        IReadOnlyList<SeriesTmdbTvSearchItem>? candidates,
        SeriesLookup lookup)
    {
        if (candidates is not { Count: > 0 })
        {
            return null;
        }

        var expected = Normalize(lookup.Name);
        var ranked = candidates
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Index = index,
                TitleScore = Math.Max(
                    Score(expected, Normalize(candidate.Name)),
                    Score(expected, Normalize(candidate.OriginalName))),
                YearScore = ScoreYear(lookup.Year, GetYear(candidate.FirstAirDate)),
            })
            .Where(item => item.TitleScore >= 2)
            .Where(item => lookup.Year is null || item.YearScore > 0)
            .OrderByDescending(item => item.TitleScore)
            .ThenByDescending(item => item.YearScore)
            .ThenBy(item => item.Index)
            .ToArray();

        if (ranked.Length == 0)
        {
            return null;
        }

        var best = ranked[0];
        var ambiguous = ranked.Skip(1).Any(item =>
            item.TitleScore == best.TitleScore
            && item.YearScore == best.YearScore);
        return ambiguous ? null : best.Candidate;
    }

    private static int Score(string expected, string actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
        {
            return 0;
        }

        if (expected == actual)
        {
            return 3;
        }

        return actual.StartsWith(expected, StringComparison.Ordinal)
            || expected.StartsWith(actual, StringComparison.Ordinal)
            ? 2
            : 0;
    }

    private static int ScoreYear(int? expected, int? actual)
    {
        if (expected is null)
        {
            return 0;
        }

        if (actual is null)
        {
            return 0;
        }

        var difference = Math.Abs(expected.Value - actual.Value);
        return difference switch
        {
            0 => 2,
            1 => 1,
            _ => 0,
        };
    }

    private static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit));

    private static int? GetYear(string? date) =>
        date?.Length >= 4
        && int.TryParse(
            date[..4],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var year)
            ? year
            : null;
}
