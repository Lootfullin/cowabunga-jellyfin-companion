using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace RussianMetadata;

internal static class TmdbPeopleLocalization
{
    private const string WikidataSparqlEndpoint =
        "https://query.wikidata.org/sparql";
    private const int LookupChunkSize = 25;
    private const int MaxLookupAttempts = 3;
    private static readonly TimeSpan MissingLabelLifetime =
        TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<int, string>
        RussianNameCache = new();
    private static readonly ConcurrentDictionary<int, DateTimeOffset>
        MissingNameCache = new();
    private static readonly SemaphoreSlim LookupGate = new(1, 1);

    public static async Task AddLocalizedPeople<TItem>(
        TmdbCredits credits,
        MetadataResult<TItem> result,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
        where TItem : BaseItem
    {
        var cast = credits.Cast?
            .Where(person =>
                person.Id > 0
                && !string.IsNullOrWhiteSpace(person.Name))
            .OrderBy(person => person.Order)
            .Take(50)
            .ToArray() ?? [];
        var crew = credits.Crew?
            .Where(person =>
                person.Id > 0
                && !string.IsNullOrWhiteSpace(person.Name)
                && MovieTextLocalization.MapCrewJob(person.Job) is not null)
            .ToArray() ?? [];
        var ids = cast.Select(person => person.Id)
            .Concat(crew.Select(person => person.Id))
            .Distinct()
            .ToArray();
        var russianNames = await FetchRussianLabels(
            ids,
            httpClientFactory,
            logger,
            cancellationToken);

        foreach (var person in CreateLocalizedPeople(
            new TmdbCredits
            {
                Cast = [.. cast],
                Crew = [.. crew]
            },
            russianNames))
        {
            result.AddPerson(person);
        }
    }

    internal static IReadOnlyList<PersonInfo> CreateLocalizedPeople(
        TmdbCredits credits,
        IReadOnlyDictionary<int, string> russianNames)
    {
        var people = new List<PersonInfo>();
        foreach (var actor in credits.Cast?
            .Where(person =>
                person.Id > 0
                && !string.IsNullOrWhiteSpace(person.Name))
            .OrderBy(person => person.Order)
            .Take(50) ?? [])
        {
            var person = new PersonInfo
            {
                Name = russianNames.GetValueOrDefault(actor.Id)
                    ?? actor.Name!.Trim(),
                Role = actor.Character?.Trim() ?? string.Empty,
                Type = PersonKind.Actor,
                SortOrder = actor.Order,
                ImageUrl = BuildProfileUrl(actor.ProfilePath)
            };
            person.SetProviderId(
                "Tmdb",
                actor.Id.ToString(CultureInfo.InvariantCulture));
            people.Add(person);
        }

        foreach (var member in credits.Crew?
            .Where(person =>
                person.Id > 0
                && !string.IsNullOrWhiteSpace(person.Name)
                && MovieTextLocalization.MapCrewJob(person.Job) is not null)
            ?? [])
        {
            var kind = MovieTextLocalization.MapCrewJob(member.Job);
            if (kind is null)
            {
                continue;
            }

            var person = new PersonInfo
            {
                Name = russianNames.GetValueOrDefault(member.Id)
                    ?? member.Name!.Trim(),
                Role = member.Job?.Trim() ?? string.Empty,
                Type = kind.Value,
                ImageUrl = BuildProfileUrl(member.ProfilePath)
            };
            person.SetProviderId(
                "Tmdb",
                member.Id.ToString(CultureInfo.InvariantCulture));
            people.Add(person);
        }

        return people;
    }

    private static async Task<Dictionary<int, string>> FetchRussianLabels(
        IReadOnlyCollection<int> ids,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var pendingIds = PendingIds(ids);
        if (pendingIds.Length == 0)
        {
            return CachedRussianNames(ids);
        }

        await LookupGate.WaitAsync(cancellationToken);
        try
        {
            pendingIds = PendingIds(ids);
            if (pendingIds.Length == 0)
            {
                return CachedRussianNames(ids);
            }

            using var httpClient = httpClientFactory.CreateClient(
                "RussianMetadata");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept",
                "application/json");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "ChooseYourMeta/1.4.1");
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            var localizedCount = 0;
            foreach (var chunk in pendingIds.Chunk(LookupChunkSize))
            {
                var fetched = await FetchChunk(
                    chunk,
                    httpClient,
                    logger,
                    cancellationToken);
                if (fetched is null)
                {
                    continue;
                }

                foreach (var pair in fetched)
                {
                    RussianNameCache[pair.Key] = pair.Value;
                    MissingNameCache.TryRemove(pair.Key, out _);
                    localizedCount++;
                }

                var missingUntil =
                    DateTimeOffset.UtcNow + MissingLabelLifetime;
                foreach (var id in chunk.Where(
                    id => !fetched.ContainsKey(id)))
                {
                    MissingNameCache[id] = missingUntil;
                }
            }

            logger.LogInformation(
                "ChooseYourMeta: Russian person lookup completed — requested {Requested}, localized {Localized}, cached total {Cached}",
                pendingIds.Length,
                localizedCount,
                RussianNameCache.Count);
            return CachedRussianNames(ids);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "ChooseYourMeta: Russian person label lookup failed");
            return CachedRussianNames(ids);
        }
        finally
        {
            LookupGate.Release();
        }
    }

    private static async Task<Dictionary<int, string>?> FetchChunk(
        IReadOnlyCollection<int> ids,
        HttpClient httpClient,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var values = string.Join(
            " ",
            ids.Select(id =>
                $"\"{id.ToString(CultureInfo.InvariantCulture)}\""));
        var query = $@"
SELECT ?externalId ?ruLabel WHERE {{
  VALUES ?externalId {{ {values} }}
  ?item wdt:P4985 ?externalId.
  ?item rdfs:label ?ruLabel.
  FILTER(LANG(?ruLabel) = ""ru"")
}}";
        var url =
            $"{WikidataSparqlEndpoint}?format=json&query={Uri.EscapeDataString(query)}";

        for (var attempt = 1; attempt <= MaxLookupAttempts; attempt++)
        {
            using var response = await httpClient.GetAsync(
                url,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(
                    cancellationToken);
                var data = JsonSerializer.Deserialize<SparqlResult>(
                    json,
                    JsonOptions.Default);
                return data?.Results?.Bindings?
                    .Where(binding =>
                        int.TryParse(
                            binding.ExternalId?.Value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out _)
                        && !string.IsNullOrWhiteSpace(
                            binding.RuLabel?.Value))
                    .GroupBy(binding => binding.ExternalId!.Value!)
                    .ToDictionary(
                        group => int.Parse(
                            group.Key,
                            CultureInfo.InvariantCulture),
                        group => group.First().RuLabel!.Value!)
                    ?? [];
            }

            if (!ShouldRetry(response.StatusCode)
                || attempt == MaxLookupAttempts)
            {
                logger.LogWarning(
                    "ChooseYourMeta: Russian person lookup returned HTTP {StatusCode} for {Count} people",
                    (int)response.StatusCode,
                    ids.Count);
                return null;
            }

            var delay = response.Headers.RetryAfter?.Delta
                ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            logger.LogWarning(
                "ChooseYourMeta: Russian person lookup returned HTTP {StatusCode}; retry {Attempt}/{MaxAttempts} in {DelayMs} ms",
                (int)response.StatusCode,
                attempt + 1,
                MaxLookupAttempts,
                delay.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken);
        }

        return null;
    }

    internal static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static int[] PendingIds(IEnumerable<int> ids)
    {
        var now = DateTimeOffset.UtcNow;
        return ids
            .Distinct()
            .Where(id => !RussianNameCache.ContainsKey(id))
            .Where(id =>
            {
                if (!MissingNameCache.TryGetValue(
                    id,
                    out var missingUntil))
                {
                    return true;
                }

                if (missingUntil > now)
                {
                    return false;
                }

                MissingNameCache.TryRemove(id, out _);
                return true;
            })
            .ToArray();
    }

    private static Dictionary<int, string> CachedRussianNames(
        IEnumerable<int> ids)
    {
        return ids
            .Distinct()
            .Where(id =>
                RussianNameCache.TryGetValue(id, out var value)
                && !string.IsNullOrWhiteSpace(value))
            .ToDictionary(id => id, id => RussianNameCache[id]);
    }

    private static string? BuildProfileUrl(string? profilePath)
    {
        return string.IsNullOrWhiteSpace(profilePath)
            ? null
            : $"https://image.tmdb.org/t/p/original{profilePath}";
    }
}
