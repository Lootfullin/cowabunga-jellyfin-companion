using System.Net;
using MediaBrowser.Controller.Providers;
using RussianMetadata;

public class MovieManualSearchTests
{
    [Fact]
    public async Task EnglishOnlySearchMatchGetsRussianTitleByIdAndKeepsYear()
    {
        var handler = new SearchHandler();
        using var client = new HttpClient(handler);
        var results = await MovieManualSearch.SearchAsync(client, "test", new MovieInfo { Name = "Downfall", Year = 2004 }, "Companion", default);
        var bunker = Assert.Single(results, r => r.ProviderIds["Tmdb"] == "613");
        Assert.Equal("Бункер", bunker.Name);
        Assert.Equal("Русское описание", bunker.Overview);
        Assert.Equal(2004, bunker.ProductionYear);
        Assert.EndsWith("/bunker.jpg", bunker.ImageUrl);
        Assert.Equal("Бункер", results[0].Name);
        Assert.All(handler.Urls.Where(u => u.Contains("search/movie")), u => Assert.Contains("primary_release_year=2004", u));
    }

    [Fact]
    public async Task ExactIdDoesNotSearchByAmbiguousName()
    {
        var handler = new SearchHandler();
        using var client = new HttpClient(handler);
        var info = new MovieInfo { Name = "Wrong name", Year = 2020 };
        info.ProviderIds["Tmdb"] = "613";
        var result = Assert.Single(await MovieManualSearch.SearchAsync(client, "test", info, "Companion", default));
        Assert.Equal("Бункер", result.Name);
        Assert.DoesNotContain(handler.Urls, u => u.Contains("search/movie"));
    }

    [Fact]
    public async Task TranslationFailureKeepsDiscoveredMovie()
    {
        using var client = new HttpClient(new SearchHandler { FailDetails = true });
        var results = await MovieManualSearch.SearchAsync(client, "test", new MovieInfo { Name = "Downfall" }, "Companion", default);
        Assert.Equal("Downfall", Assert.Single(results, r => r.ProviderIds["Tmdb"] == "613").Name);
    }

    [Theory]
    [InlineData("Hellboy", 2019, 1487, 2004, 456740, false)]
    [InlineData("Aladdin", 2019, 812, 1992, 420817, false)]
    [InlineData("The Lion King", 2019, 8587, 1994, 420818, false)]
    [InlineData("Hellboy", 2019, 1487, 2004, 456740, true)]
    public async Task RequestedYearOutranksOriginalEvenIfTmdbReturnsItFirst(
        string title, int year, int oldId, int oldYear, int correctId, bool needsFallback)
    {
        var handler = new RemakeHandler(title, year, oldId, oldYear, correctId, needsFallback);
        using var client = new HttpClient(handler);
        var results = await MovieManualSearch.SearchAsync(client, "test",
            new MovieInfo { Name = title, Year = year }, "Companion", default);

        Assert.Equal(new[] { correctId.ToString(), oldId.ToString() }, results.Select(r => r.ProviderIds["Tmdb"]));
        Assert.Equal(year, results[0].ProductionYear);
        Assert.Equal(needsFallback ? 2 : 0, handler.UnfilteredSearches);
    }

    private sealed class RemakeHandler(string title, int year, int oldId, int oldYear, int correctId, bool needsFallback)
        : HttpMessageHandler
    {
        public int UnfilteredSearches { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            object Entry(int id, int releaseYear) => new { id, title, release_date = $"{releaseYear}-05-01" };
            object response;
            if (uri.AbsolutePath.EndsWith("search/movie"))
            {
                var filtered = uri.Query.Contains($"primary_release_year={year}");
                if (!filtered) UnfilteredSearches++;
                response = new { results = needsFallback && filtered
                    ? new[] { Entry(oldId, oldYear) }
                    : new[] { Entry(oldId, oldYear), Entry(correctId, year) } };
            }
            else
            {
                response = uri.AbsolutePath.EndsWith("/" + correctId) ? Entry(correctId, year) : Entry(oldId, oldYear);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(response)
            });
        }
    }

    private sealed class SearchHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public bool FailDetails { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            if (!url.Contains("search/movie") && FailDetails)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var json = url.Contains("search/movie")
                ? (url.Contains("en-US")
                    ? """{"results":[{"id":613,"title":"Downfall","release_date":"2004-09-16"},{"id":13190,"title":"Dead Space: Downfall"}]}"""
                    : """{"results":[{"id":13190,"title":"Космос: Территория смерти"}]}""")
                : (url.Contains("movie/613?")
                    ? """{"id":613,"title":"Бункер","overview":"Русское описание","release_date":"2004-09-16","poster_path":"/bunker.jpg"}"""
                    : """{"id":13190,"title":"Космос: Территория смерти"}""");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
