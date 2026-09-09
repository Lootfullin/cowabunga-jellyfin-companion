using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace RussianMetadata;

internal static class MovieManualSearch
{
    internal static async Task<IReadOnlyList<RemoteSearchResult>> SearchAsync(
        HttpClient client, string apiKey, MovieInfo info, string providerName, CancellationToken token)
    {
        var prefix = "https://api.themoviedb.org/3/";
        var key = Uri.EscapeDataString(apiKey);
        async Task<T?> Read<T>(string path) where T : class
        {
            try
            {
                using var response = await client.GetAsync(prefix + path, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false), JsonOptions.Default);
            }
            catch (HttpRequestException) { return null; }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { return null; }
        }

        var lookup = MovieLookup.Parse(info.Name, info.Year);
        var tmdbId = MovieLookup.ExtractTmdbId(info.ProviderIds);
        List<SearchMovie> candidates;
        if (tmdbId is > 0)
        {
            var exact = await Read<SearchMovie>($"movie/{tmdbId}?api_key={key}&language=ru-RU");
            candidates = exact is null ? [] : [exact];
        }
        else
        {
            if (string.IsNullOrWhiteSpace(lookup.Name)) return [];
            var query = Uri.EscapeDataString(lookup.Name);
            var year = lookup.Year.HasValue ? "&year=" + lookup.Year.Value.ToString(CultureInfo.InvariantCulture) : "";
            var languages = MovieTextLocalization.ContainsCyrillic(lookup.Name) ? new[] { "ru-RU", "en-US" } : new[] { "en-US", "ru-RU" };
            var responses = await Task.WhenAll(languages.Select(language => Read<SearchResponse>(
                $"search/movie?api_key={key}&language={language}&query={query}{year}")));
            candidates = responses.SelectMany(response => response?.Results ?? [])
                .Where(movie => movie.Id > 0).DistinctBy(movie => movie.Id).Take(40).ToList();
        }

        // Search language can change the set of matches, not just their labels.
        // Localize by exact ID after discovery; never match translated titles by text.
        using var gate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(candidates.Select(async candidate =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var localized = await Read<SearchMovie>($"movie/{candidate.Id}?api_key={key}&language=ru-RU");
                var name = MovieTextLocalization.RussianOrNull(localized?.Title)
                    ?? candidate.Title ?? candidate.OriginalTitle ?? "Unknown";
                var date = localized?.ReleaseDate ?? candidate.ReleaseDate;
                var poster = localized?.PosterPath ?? candidate.PosterPath;
                var result = new RemoteSearchResult
                {
                    Name = name,
                    Overview = MovieTextLocalization.RussianOrNull(localized?.Overview) ?? candidate.Overview,
                    SearchProviderName = providerName,
                    ProductionYear = date?.Length >= 4 && int.TryParse(date[..4], out var year) ? year : null,
                    ImageUrl = string.IsNullOrWhiteSpace(poster) ? null : "https://image.tmdb.org/t/p/w500" + poster
                };
                result.ProviderIds["Tmdb"] = candidate.Id.ToString(CultureInfo.InvariantCulture);
                return result;
            }
            finally { gate.Release(); }
        }));
        return results;
    }

    private sealed class SearchResponse { public List<SearchMovie>? Results { get; set; } }
    private sealed class SearchMovie
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Overview { get; set; }
        [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
        [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    }
}
