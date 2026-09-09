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
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace RussianMetadata;

public partial class RussianMovieProvider :
    IRemoteMetadataProvider<Movie, MovieInfo>,
    ICustomMetadataProvider<Movie>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RussianMovieProvider> _logger;
    private CompanionPlugin CurrentPlugin => CompanionPlugin.Instance!;

    private const string TmdbApiBase = "https://api.themoviedb.org/3";
    private const string WikidataSparqlEndpoint = "https://query.wikidata.org/sparql";

    [GeneratedRegex(@"\b(tt\d{7,8})\b")]
    private static partial Regex ImdbIdRegex();

    public RussianMovieProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<RussianMovieProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => CompanionPlugin.MetadataProviderName;

    // Jellyfin seeds the remote-provider merge target with the resolver/file
    // name. Remote providers may then fill only empty fields, so even the
    // first provider cannot replace that name. Custom providers run after the
    // remote merge and are the supported point for applying the localized
    // title.
    public async Task<ItemUpdateType> FetchAsync(
        Movie item,
        MetadataRefreshOptions options,
        CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.Allows(CompanionModule.Metadata, item) || item.IsLocked) return ItemUpdateType.None;

        var config = CurrentPlugin.Configuration;
        int? tmdbId = MovieLookup.ExtractTmdbId(item.ProviderIds);
        var imdbId = item.GetProviderId(MetadataProvider.Imdb);
        var identityChanged = false;

        var tmdbApiKey = TmdbApiKeyResolver.Resolve(config);
        if (string.IsNullOrWhiteSpace(tmdbApiKey))
        {
            _logger.LogWarning(
                "ChooseYourMeta: Post-refresh title skipped for TMDB {TmdbId}: TMDB integration unavailable",
                tmdbId);
            return ItemUpdateType.None;
        }

        try
        {
            using var handler = CreateProxyHandler(config);
            using var httpClient = new HttpClient(handler, disposeHandler: true);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept",
                "application/json");
            httpClient.Timeout = TimeSpan.FromSeconds(5);

            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                var findUrl = $"{TmdbApiBase}/find/{Uri.EscapeDataString(imdbId)}?api_key={Uri.EscapeDataString(tmdbApiKey)}&external_source=imdb_id";
                using var findResponse = await httpClient.GetAsync(findUrl, cancellationToken);
                if (findResponse.IsSuccessStatusCode)
                {
                    var findJson = await findResponse.Content.ReadAsStringAsync(cancellationToken);
                    var find = JsonSerializer.Deserialize<TmdbFindResult>(findJson, JsonOptions.Default);
                    if (find?.MovieResults is { Count: 1 })
                    {
                        var exactTmdbId = find.MovieResults[0].Id;
                        if (tmdbId != exactTmdbId)
                        {
                            _logger.LogWarning(
                                "ChooseYourMeta: correcting movie TMDB ID {OldTmdbId} -> {NewTmdbId} using IMDb {ImdbId}",
                                tmdbId,
                                exactTmdbId,
                                imdbId);
                            item.SetProviderId(MetadataProvider.Tmdb, exactTmdbId.ToString(CultureInfo.InvariantCulture));
                            tmdbId = exactTmdbId;
                            identityChanged = true;
                        }
                    }
                }
            }

            if (tmdbId is null)
            {
                _logger.LogInformation(
                    "ChooseYourMeta: Post-refresh title skipped for '{Name}': no verified TMDB ID",
                    item.Name);
                return ItemUpdateType.None;
            }

            var url = $"{TmdbApiBase}/movie/{tmdbId.Value}?api_key={Uri.EscapeDataString(tmdbApiKey)}&language=ru-RU";
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ChooseYourMeta: Post-refresh title request failed for TMDB {TmdbId} ({Status})",
                    tmdbId,
                    response.StatusCode);
                return ItemUpdateType.None;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var details = JsonSerializer.Deserialize<TmdbMovieDetails>(
                json,
                JsonOptions.Default);
            if (details is null)
            {
                return ItemUpdateType.None;
            }

            var changed = identityChanged
                | ApplyTmdbCollection(item, details.BelongsToCollection);
            var russianTitle = MovieTextLocalization.RussianOrNull(details.Title);
            if (config.EnableRussianTitles && !item.LockedFields.Contains(MetadataField.Name)
                && !string.IsNullOrWhiteSpace(russianTitle)
                && !string.Equals(item.Name, russianTitle, StringComparison.Ordinal))
            {
                var previousName = item.Name;
                item.Name = russianTitle;
                changed = true;
                _logger.LogInformation(
                    "ChooseYourMeta: Post-refresh title applied for TMDB {TmdbId}: '{PreviousName}' -> '{RussianTitle}'",
                    tmdbId,
                    previousName,
                    russianTitle);
            }

            changed |= MovieTextLocalization.ApplyDescription(item, details.Overview, details.Tagline, config);
            return changed ? ItemUpdateType.MetadataEdit : ItemUpdateType.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "ChooseYourMeta: Post-refresh title failed for TMDB {TmdbId}",
                tmdbId);
            return ItemUpdateType.None;
        }
    }

    public async Task<MetadataResult<Movie>> GetMetadata(
        MovieInfo info,
        CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.AllowsPath(CompanionModule.Metadata, info.Path)) return new MetadataResult<Movie>();

        var result = new MetadataResult<Movie>();
        string? imdbId = ExtractImdbId(info);
        int? tmdbId = MovieLookup.ExtractTmdbId(info.ProviderIds);
        var lookup = MovieLookup.Parse(info.Name, info.Year);

        _logger.LogInformation(
            "ChooseYourMeta: GetMetadata — Name='{Name}', TmdbId={TmdbId}, ImdbId={ImdbId}, SearchName='{SearchName}', Year={Year}",
            info.Name ?? "?",
            tmdbId?.ToString(CultureInfo.InvariantCulture) ?? "N/A",
            imdbId ?? "N/A",
            lookup.Name,
            lookup.Year?.ToString(CultureInfo.InvariantCulture) ?? "N/A");

        result.Item = new Movie();
        if (!string.IsNullOrEmpty(imdbId))
        {
            result.Item.SetProviderId("Imdb", imdbId);
        }

        result.ResultLanguage = "ru";

        var config = CurrentPlugin.Configuration;
        var tmdbApiKey = TmdbApiKeyResolver.Resolve(config);
        bool tmdbSuccess = false;
        if (!string.IsNullOrEmpty(tmdbApiKey))
        {
            tmdbSuccess = await TryTmdbMovie(
                tmdbId,
                imdbId,
                lookup,
                config,
                tmdbApiKey,
                result,
                cancellationToken);

            imdbId = result.Item.GetProviderId("Imdb") ?? imdbId;
        }

        // Wikidata is a field-level fallback. For example, TMDB can have a
        // Russian overview but leave the localized title in English.
        bool wikidataSuccess = false;
        if (!string.IsNullOrEmpty(imdbId))
        {
            wikidataSuccess = await TryWikidata(
                imdbId,
                config,
                result,
                cancellationToken);
        }

        if (!tmdbSuccess && !wikidataSuccess)
        {
            return new MetadataResult<Movie>();
        }

        // Keep the lookup name only when neither Russian source has a title.
        // The next metadata provider can still fill every missing field.
        if (string.IsNullOrWhiteSpace(result.Item.Name)
            && !string.IsNullOrWhiteSpace(info.Name))
        {
            result.Item.Name = info.Name;
        }

        result.HasMetadata = true;
        return result;
    }

    private async Task<bool> TryTmdbMovie(
        int? knownTmdbId,
        string? imdbId,
        MovieLookup lookup,
        Configuration.PluginConfiguration config,
        string tmdbApiKey,
        MetadataResult<Movie> result, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "ChooseYourMeta: Trying TMDB — TmdbId={TmdbId}, ImdbId={ImdbId}, Name='{Name}', Year={Year}",
            knownTmdbId?.ToString(CultureInfo.InvariantCulture) ?? "N/A",
            imdbId ?? "N/A",
            lookup.Name,
            lookup.Year?.ToString(CultureInfo.InvariantCulture) ?? "N/A");

        try
        {
            result.Item ??= new Movie();
            var handler = CreateProxyHandler(config);
            using var httpClient = new HttpClient(handler, disposeHandler: true);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            httpClient.Timeout = TimeSpan.FromSeconds(5);

            int? resolvedTmdbId = null;
            if (!string.IsNullOrEmpty(imdbId))
            {
                var findUrl = $"{TmdbApiBase}/find/{imdbId}?api_key={Uri.EscapeDataString(tmdbApiKey)}&external_source=imdb_id";
                using var findResponse = await httpClient.GetAsync(findUrl, cancellationToken);
                if (findResponse.IsSuccessStatusCode)
                {
                    var findJson = await findResponse.Content.ReadAsStringAsync(cancellationToken);
                    var findData = JsonSerializer.Deserialize<TmdbFindResult>(findJson, JsonOptions.Default);
                    resolvedTmdbId = findData?.MovieResults is { Count: 1 }
                        ? findData.MovieResults[0].Id
                        : null;
                    if (resolvedTmdbId is not null
                        && knownTmdbId is not null
                        && resolvedTmdbId != knownTmdbId)
                    {
                        _logger.LogWarning(
                            "ChooseYourMeta: correcting movie TMDB ID {OldTmdbId} -> {NewTmdbId} using IMDb {ImdbId}",
                            knownTmdbId,
                            resolvedTmdbId,
                            imdbId);
                    }
                }
            }

            // A TMDB ID already stored by Jellyfin is preferable to a name
            // search. An exact IMDb lookup above is the only automatic source
            // allowed to replace it.
            resolvedTmdbId ??= knownTmdbId;

            if (resolvedTmdbId is null && !string.IsNullOrWhiteSpace(lookup.Name))
            {
                var searchUrl = $"{TmdbApiBase}/search/movie?api_key={Uri.EscapeDataString(tmdbApiKey)}&language=ru-RU&query={Uri.EscapeDataString(lookup.Name)}";
                if (lookup.Year is not null)
                {
                    searchUrl += $"&year={lookup.Year.Value.ToString(CultureInfo.InvariantCulture)}";
                }

                using var searchResponse = await httpClient.GetAsync(searchUrl, cancellationToken);
                if (searchResponse.IsSuccessStatusCode)
                {
                    var searchJson = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
                    var searchData = JsonSerializer.Deserialize<TmdbSearchResponse>(searchJson, JsonOptions.Default);
                    resolvedTmdbId = MovieLookup.SelectCandidate(searchData?.Results, lookup)?.Id;
                }
            }

            if (resolvedTmdbId is null)
            {
                _logger.LogWarning(
                    "ChooseYourMeta: Could not resolve TMDB movie for '{Name}' ({Year})",
                    lookup.Name,
                    lookup.Year);
                return false;
            }

            var detailsUrl = $"{TmdbApiBase}/movie/{resolvedTmdbId.Value}?api_key={Uri.EscapeDataString(tmdbApiKey)}&language=ru-RU&append_to_response=credits";
            _logger.LogInformation("ChooseYourMeta: TMDB details for ID {TmdbId}", resolvedTmdbId);

            using var detailsResponse = await httpClient.GetAsync(detailsUrl, cancellationToken);
            if (!detailsResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("RussianMetadata: TMDB details failed ({Status})", detailsResponse.StatusCode);
                return false;
            }

            var detailsJson = await detailsResponse.Content.ReadAsStringAsync(cancellationToken);
            var movieDetails = JsonSerializer.Deserialize<TmdbMovieDetails>(detailsJson, JsonOptions.Default);
            _logger.LogInformation("RussianMetadata: TMDB details parsed — Title={T}, OriginalTitle={Ot}, OverviewLen={Ol}", movieDetails?.Title ?? "N", movieDetails?.OriginalTitle ?? "N", movieDetails?.Overview?.Length ?? 0);

            if (movieDetails == null) return false;

            if (config.EnableRussianTitles)
            {
                result.Item.Name = MovieTextLocalization.RussianOrNull(movieDetails.Title)
                    ?? movieDetails.Title?.Trim()
                    ?? movieDetails.OriginalTitle?.Trim()
                    ?? lookup.Name;
                _logger.LogInformation("ChooseYourMeta: TMDB — set title: {Title}", result.Item.Name);
            }

            var russianOverview = MovieTextLocalization.RussianOrNull(movieDetails.Overview);
            if (!string.IsNullOrEmpty(russianOverview) && config.EnableRussianOverviews)
            {
                result.Item.Overview = russianOverview;
                _logger.LogInformation("RussianMetadata: TMDB — set overview ({Len} chars)", russianOverview.Length);
            }

            var russianTagline = MovieTextLocalization.RussianOrNull(movieDetails.Tagline);
            if (!string.IsNullOrEmpty(russianTagline) && config.EnableRussianTaglines)
            {
                result.Item.Tagline = russianTagline;
            }

            if (config.EnableRussianGenres && movieDetails.Genres is { Count: > 0 })
            {
                result.Item.Genres = movieDetails.Genres
                    .Select(genre => genre.Name?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToArray();
            }

            if (config.EnableRussianStudios && movieDetails.ProductionCompanies is { Count: > 0 })
            {
                var companyIds = movieDetails.ProductionCompanies
                    .Where(company => company.Id > 0)
                    .Select(company => company.Id)
                    .ToArray();
                var russianCompanies = await FetchRussianLabelsByExternalIds(
                    "P11806",
                    companyIds,
                    cancellationToken);
                result.Item.Studios = movieDetails.ProductionCompanies
                    .Select(company => russianCompanies.GetValueOrDefault(company.Id)
                        ?? company.Name?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToArray();
            }

            if (config.EnableRussianPeople && movieDetails.Credits is not null)
            {
                await TmdbPeopleLocalization.AddLocalizedPeople(
                    movieDetails.Credits,
                    result,
                    _httpClientFactory,
                    _logger,
                    cancellationToken);
            }

            if (!string.IsNullOrEmpty(movieDetails.OriginalTitle))
            {
                result.Item.OriginalTitle = movieDetails.OriginalTitle;
            }

            result.Item.SetProviderId("Tmdb", resolvedTmdbId.Value.ToString(CultureInfo.InvariantCulture));
            ApplyTmdbCollection(result.Item, movieDetails.BelongsToCollection);
            if (!string.IsNullOrWhiteSpace(movieDetails.ImdbId))
            {
                result.Item.SetProviderId("Imdb", movieDetails.ImdbId);
            }

            _logger.LogInformation(
                "ChooseYourMeta: TMDB success — TmdbId={TmdbId}, ImdbId={ImdbId}, Title='{Title}'",
                resolvedTmdbId,
                movieDetails.ImdbId ?? imdbId ?? "N/A",
                movieDetails.Title);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RussianMetadata: TMDB failed for {ImdbId}, will fallback", imdbId);
            return false;
        }
    }

    internal static bool ApplyTmdbCollection(
        Movie movie,
        TmdbCollectionReference? collection)
    {
        var previousId = movie.GetProviderId(MetadataProvider.TmdbCollection);
        var previousName = movie.TmdbCollectionName;
        var collectionId = collection is { Id: > 0 }
            ? collection.Id.ToString(CultureInfo.InvariantCulture)
            : null;
        var collectionName = string.IsNullOrWhiteSpace(collection?.Name)
            ? null
            : collection.Name.Trim();

        if (collectionId is null)
        {
            movie.ProviderIds.Remove(MetadataProvider.TmdbCollection.ToString());
        }
        else
        {
            movie.SetProviderId(MetadataProvider.TmdbCollection, collectionId);
        }

        movie.TmdbCollectionName = collectionName;
        return !string.Equals(previousId, collectionId, StringComparison.Ordinal)
            || !string.Equals(previousName, collectionName, StringComparison.Ordinal);
    }

    private async Task<bool> TryWikidata(string imdbId, Configuration.PluginConfiguration config,
        MetadataResult<Movie> result, CancellationToken cancellationToken)
    {
        _logger.LogInformation("RussianMetadata: Falling back to Wikidata for {ImdbId}", imdbId);

        try
        {
            var entity = await FetchWikidataEntityByImdbId(imdbId, cancellationToken);
            if (entity == null)
            {
                _logger.LogWarning("RussianMetadata: No Wikidata entity for {ImdbId}", imdbId);
                return false;
            }

            result.Item ??= new Movie();
            bool changed = false;

            bool hasRussianLabel = !string.IsNullOrEmpty(entity.RussianLabel)
                && !entity.RussianLabel.StartsWith("Q", StringComparison.Ordinal);

            if (hasRussianLabel
                && config.EnableRussianTitles
                && string.IsNullOrEmpty(result.Item.Name))
            {
                result.Item.Name = entity.RussianLabel;
                changed = true;
                _logger.LogInformation("RussianMetadata: Wikidata — set Russian title: {Title}", entity.RussianLabel);
            }

            // Only set Russian overview; never overwrite with English (preserve NFO data)
            if (!string.IsNullOrEmpty(entity.RussianDescription)
                && config.EnableRussianOverviews
                && string.IsNullOrEmpty(result.Item.Overview))
            {
                result.Item.Overview = entity.RussianDescription;
                changed = true;
                _logger.LogInformation("RussianMetadata: Wikidata — set RU overview: {Desc}", entity.RussianDescription);
            }

            if (changed)
            {
                _logger.LogInformation("RussianMetadata: Wikidata RU data applied for {ImdbId}", imdbId);
            }
            else
            {
                _logger.LogInformation("RussianMetadata: Wikidata entity found for {ImdbId} but no RU data to apply", imdbId);
            }

            return changed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RussianMetadata: Wikidata error for {ImdbId}", imdbId);
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
                _logger.LogInformation("RussianMetadata: Using proxy {Proxy}", config.ProxyUrl);
            }
        }

        return handler;
    }

    /// <summary>
    /// Escape characters unsafe for SPARQL string literals (backslash, double-quote).
    /// Does NOT URL-encode – the SPARQL query is URL-encoded once at the HTTP layer.
    /// </summary>
    private static string EscapeSparqlString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private async Task<Dictionary<int, string>> FetchRussianLabelsByExternalIds(
        string propertyId,
        IReadOnlyCollection<int> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        using var httpClient = _httpClientFactory.CreateClient("RussianMetadata");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Jellyfin-RussianMetadata/1.3");
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        var values = string.Join(
            " ",
            ids.Distinct().Select(id =>
                $"\"{id.ToString(CultureInfo.InvariantCulture)}\""));
        var sparqlQuery = $@"
SELECT ?externalId ?ruLabel WHERE {{
  VALUES ?externalId {{ {values} }}
  ?item wdt:{propertyId} ?externalId.
  ?item rdfs:label ?ruLabel.
  FILTER(LANG(?ruLabel) = ""ru"")
}}";
        var url = $"{WikidataSparqlEndpoint}?format=json&query={Uri.EscapeDataString(sparqlQuery)}";

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<SparqlResult>(
                json,
                JsonOptions.Default);
            return result?.Results?.Bindings?
                .Where(binding =>
                    int.TryParse(
                        binding.ExternalId?.Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out _)
                    && !string.IsNullOrWhiteSpace(binding.RuLabel?.Value))
                .GroupBy(binding => binding.ExternalId!.Value!)
                .ToDictionary(
                    group => int.Parse(
                        group.Key,
                        CultureInfo.InvariantCulture),
                    group => group.First().RuLabel!.Value!,
                    EqualityComparer<int>.Default)
                ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "RussianMetadata: Wikidata label lookup failed for property {PropertyId}",
                propertyId);
            return [];
        }
    }

    // ───── IMDb ID extraction ─────

    private string? ExtractImdbId(MovieInfo info)
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

    // ───── Wikidata ─────

    private async Task<WikidataEntity?> FetchWikidataEntityByImdbId(string imdbId, CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient("RussianMetadata");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Jellyfin-RussianMetadata/1.0");
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        // Fetch both RU and EN labels/descriptions in one query
        var safeImdbId = EscapeSparqlString(imdbId);
        var sparqlQuery = $@"
SELECT ?item ?ruLabel ?enLabel ?ruDescription ?enDescription WHERE {{
  ?item wdt:P345 ""{safeImdbId}"".
  OPTIONAL {{ ?item rdfs:label ?ruLabel. FILTER(LANG(?ruLabel) = ""ru"") }}
  OPTIONAL {{ ?item rdfs:label ?enLabel. FILTER(LANG(?enLabel) = ""en"") }}
  OPTIONAL {{ ?item schema:description ?ruDescription. FILTER(LANG(?ruDescription) = ""ru"") }}
  OPTIONAL {{ ?item schema:description ?enDescription. FILTER(LANG(?enDescription) = ""en"") }}
}}
LIMIT 1";
        var url = $"{WikidataSparqlEndpoint}?format=json&query={Uri.EscapeDataString(sparqlQuery)}";

        _logger.LogInformation("RussianMetadata: Wikidata query for {ImdbId}", imdbId);

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var sparqlResult = JsonSerializer.Deserialize<SparqlResult>(json, JsonOptions.Default);
            var binding = sparqlResult?.Results?.Bindings?.Count > 0 ? sparqlResult.Results.Bindings[0] : null;

            if (binding == null) return null;

            return new WikidataEntity
            {
                EntityId = binding.Item?.Value,
                RussianLabel = binding.RuLabel?.Value ?? binding.ItemLabel?.Value,
                EnglishLabel = binding.EnLabel?.Value,
                RussianDescription = binding.RuDescription?.Value,
                EnglishDescription = binding.EnDescription?.Value
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RussianMetadata: Wikidata fetch error for {ImdbId}", imdbId);
            return null;
        }
    }

    // ───── Search ─────

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.AllowsMetadataSearch(searchInfo.Path)) return Array.Empty<RemoteSearchResult>();

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
                var searchUrl = $"{TmdbApiBase}/search/movie?api_key={Uri.EscapeDataString(tmdbApiKey)}&language=ru-RU&query={query}";

                using var response = await httpClient.GetAsync(searchUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var searchResult = JsonSerializer.Deserialize<TmdbSearchResponse>(json, JsonOptions.Default);
                    if (searchResult?.Results != null)
                    {
                        foreach (var m in searchResult.Results)
                        {
                            var sr = new RemoteSearchResult
                            {
                                Name = m.Title ?? m.OriginalTitle ?? "Unknown",
                                Overview = m.Overview,
                                SearchProviderName = Name,
                                ProductionYear = m.ReleaseDate?.Length >= 4
                                    && int.TryParse(m.ReleaseDate[..4], out var yr) ? yr : null
                            };
                            sr.SetProviderId("Tmdb", m.Id.ToString(CultureInfo.InvariantCulture));
                            results.Add(sr);
                        }
                        return results;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "RussianMetadata: TMDB search failed, fallback to Wikidata");
            }
        }

        // Fallback: Wikidata search (existing logic)
        try
        {
            using var httpClient = _httpClientFactory.CreateClient("RussianMetadata");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Jellyfin-RussianMetadata/1.0");
            httpClient.Timeout = TimeSpan.FromSeconds(5);

            var safeName = EscapeSparqlString(searchInfo.Name);
            var sparqlQuery = $@"
SELECT ?item ?itemLabel ?description WHERE {{
  ?item wdt:P31 wd:Q11424.
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
            _logger.LogError(ex, "RussianMetadata: Wikidata search error");
        }

        return results;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient("RussianMetadata");
        return httpClient.GetAsync(url, cancellationToken);
    }
}

// ───── TMDB JSON models ─────

internal class TmdbFindResult
{
    [JsonPropertyName("movie_results")]
    public List<TmdbMovieRef>? MovieResults { get; set; }
    [JsonPropertyName("tv_results")]
    public List<TmdbTvRef>? TvResults { get; set; }
}

internal class TmdbMovieRef
{
    public int Id { get; set; }
}

internal class TmdbTvRef
{
    public int Id { get; set; }
}

internal class TmdbMovieDetails
{
    public int Id { get; set; }
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }
    public string? Title { get; set; }
    public string? Overview { get; set; }
    public string? Tagline { get; set; }
    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
    public List<TmdbGenre>? Genres { get; set; }
    [JsonPropertyName("production_companies")]
    public List<TmdbCompany>? ProductionCompanies { get; set; }
    [JsonPropertyName("belongs_to_collection")]
    public TmdbCollectionReference? BelongsToCollection { get; set; }
    public TmdbCredits? Credits { get; set; }
}

internal sealed class TmdbCollectionReference
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

internal class TmdbGenre
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

internal class TmdbCompany
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

internal class TmdbCredits
{
    public List<TmdbCastMember>? Cast { get; set; }
    public List<TmdbCrewMember>? Crew { get; set; }
}

internal class TmdbCastMember
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Character { get; set; }
    public int Order { get; set; }
    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }
}

internal class TmdbCrewMember
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Job { get; set; }
    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }
}

internal class TmdbSearchResponse
{
    public List<TmdbSearchItem>? Results { get; set; }
}

internal class TmdbSearchItem
{
    public int Id { get; set; }
    public string? Title { get; set; }
    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
}

internal sealed record MovieLookup(string Name, int? Year)
{
    private static readonly Regex LeadingYear =
        new(@"^\s*\((?<year>\d{4})\)\s*", RegexOptions.Compiled);
    private static readonly Regex TrailingYear =
        new(@"\s*\((?<year>\d{4})\)\s*$", RegexOptions.Compiled);

    public static MovieLookup Parse(string? rawName, int? suppliedYear)
    {
        var name = (rawName ?? string.Empty).Trim().Trim('"').Trim();
        int? year = suppliedYear;

        var leading = LeadingYear.Match(name);
        if (leading.Success)
        {
            year ??= int.Parse(leading.Groups["year"].Value, CultureInfo.InvariantCulture);
            name = name[leading.Length..].Trim();
        }

        var trailing = TrailingYear.Match(name);
        if (trailing.Success)
        {
            year ??= int.Parse(trailing.Groups["year"].Value, CultureInfo.InvariantCulture);
            name = name[..trailing.Index].Trim();
        }

        return new MovieLookup(name.Trim('"').Trim(), year);
    }

    public static int? ExtractTmdbId(IReadOnlyDictionary<string, string> providerIds)
    {
        foreach (var pair in providerIds)
        {
            if (pair.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && id > 0)
            {
                return id;
            }
        }

        return null;
    }

    public static TmdbSearchItem? SelectCandidate(
        IReadOnlyList<TmdbSearchItem>? candidates,
        MovieLookup lookup)
    {
        if (candidates is not { Count: > 0 })
        {
            return null;
        }

        var normalizedLookup = Normalize(lookup.Name);
        var ranked = candidates
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Index = index,
                YearScore = ScoreYear(
                    lookup.Year,
                    GetYear(candidate.ReleaseDate)),
                TitleScore = Math.Max(
                    Score(normalizedLookup, Normalize(candidate.Title)),
                    Score(normalizedLookup, Normalize(candidate.OriginalTitle)))
            })
            .Where(item => item.TitleScore >= 2)
            .Where(item => lookup.Year is null
                || item.YearScore > 0)
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

    private static int ScoreYear(int? expected, int? actual)
    {
        if (expected is null || actual is null)
        {
            return 0;
        }

        var difference = Math.Abs(expected.Value - actual.Value);
        return difference switch
        {
            0 => 2,
            1 => 1,
            _ => 0
        };
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

        if (actual.StartsWith(expected, StringComparison.Ordinal)
            || expected.StartsWith(actual, StringComparison.Ordinal))
        {
            return 2;
        }

        return actual.Contains(expected, StringComparison.Ordinal)
            || expected.Contains(actual, StringComparison.Ordinal)
            ? 1
            : 0;
    }

    private static string Normalize(string? value)
    {
        return string.Concat((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit));
    }

    private static int? GetYear(string? releaseDate)
    {
        return releaseDate?.Length >= 4
            && int.TryParse(releaseDate[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }
}

// ───── Wikidata / SPARQL models ─────

internal class WikidataEntity
{
    public string? EntityId { get; set; }
    public string? RussianLabel { get; set; }
    public string? EnglishLabel { get; set; }
    public string? RussianDescription { get; set; }
    public string? EnglishDescription { get; set; }
}

internal class SparqlResult
{
    public SparqlHead? Head { get; set; }
    public SparqlResults? Results { get; set; }
}

internal class SparqlHead
{
    public List<string>? Vars { get; set; }
}

internal class SparqlResults
{
    public List<SparqlBinding>? Bindings { get; set; }
}

internal class SparqlBinding
{
    public SparqlValue? Item { get; set; }
    public SparqlValue? ItemLabel { get; set; }
    public SparqlValue? Description { get; set; }
    // Extended Wikidata binding with separate RU/EN fields
    public SparqlValue? RuLabel { get; set; }
    public SparqlValue? EnLabel { get; set; }
    public SparqlValue? RuDescription { get; set; }
    public SparqlValue? EnDescription { get; set; }
    public SparqlValue? ExternalId { get; set; }
    // IMDb ID in search results
    [JsonPropertyName("imdbId")]
    public SparqlValue? ImdbId { get; set; }
}

internal class SparqlValue
{
    public string? Type { get; set; }
    public string? Value { get; set; }
    [JsonPropertyName("xml:lang")]
    public string? Lang { get; set; }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
