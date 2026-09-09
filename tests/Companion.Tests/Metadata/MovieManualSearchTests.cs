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
        Assert.All(handler.Urls.Where(u => u.Contains("search/movie")), u => Assert.Contains("year=2004", u));
    }

    [Fact]
    public async Task ExactIdDoesNotSearchByAmbiguousName()
    {
        var handler = new SearchHandler();
        using var client = new HttpClient(handler);
        var info = new MovieInfo { Name = "Wrong name" };
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
