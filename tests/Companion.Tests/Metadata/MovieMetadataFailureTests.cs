using System.Net;
using System.Text.Json;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using RussianMetadata;

public sealed class MovieMetadataFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StudiosComeFromTmdbWithoutTranslationRequests(bool localizePeople)
    {
        using var context = new CompanionTestContext();
        context.Config.TmdbApiKey = "test";
        context.Config.EnableRussianPeople = localizePeople;
        var handler = new MetadataHandler("studio", 2300, "Космический Джем", 1996);
        var info = new MovieInfo { Path = Path.Combine(context.Root, "movie.mkv") };
        info.ProviderIds["Tmdb"] = "2300";

        var result = await CreateProvider(handler).GetMetadata(info, default);

        Assert.True(result.HasMetadata);
        Assert.Equal("Studio", Assert.Single(result.Item.Studios));
        Assert.DoesNotContain(handler.RequestedUrls, url => url.Contains("P11806"));
        Assert.False(handler.FailedRequest);
        Assert.Equal(localizePeople, handler.RequestedUrls.Any(url => url.Contains("P4985")));
        if (!localizePeople)
            Assert.DoesNotContain(handler.RequestedUrls, url => url.Contains("wikidata.org"));
    }

    [Theory]
    [InlineData("people", 788, "Миссис Даутфайр", 1993)]
    [InlineData("fallback", 2300, "Космический Джем", 1996)]
    public async Task OptionalTimeoutKeepsVerifiedMovie(string failure, int id, string title, int year)
    {
        using var context = new CompanionTestContext();
        context.Config.TmdbApiKey = "test";
        var handler = new MetadataHandler(failure, id, title, year);
        var provider = CreateProvider(handler);
        var info = new MovieInfo { Name = "Unlocalized title", Path = Path.Combine(context.Root, "movie.mkv"), Year = 2026 };
        info.ProviderIds["Tmdb"] = id.ToString();

        var result = await provider.GetMetadata(info, default);

        Assert.True(result.HasMetadata);
        Assert.Equal(title, result.Item.Name);
        Assert.Equal(id.ToString(), result.Item.GetProviderId("Tmdb"));
        Assert.Equal(year, result.Item.ProductionYear);
        Assert.Equal(year, result.Item.PremiereDate?.Year);
        Assert.Equal(failure == "fallback" ? null : "Русское описание", result.Item.Overview);
        Assert.Contains(result.People, p => p.GetProviderId("Tmdb") == handler.PersonId.ToString());
        Assert.True(handler.FailedRequest);
    }

    [Fact]
    public async Task CompleteRussianTextDoesNotRequestWikidataFallback()
    {
        using var context = new CompanionTestContext();
        context.Config.TmdbApiKey = "test";
        context.Config.EnableRussianPeople = false;
        var handler = new MetadataHandler("fallback", 788, "Миссис Даутфайр", 1993) { CompleteText = true };
        var info = new MovieInfo { Path = Path.Combine(context.Root, "movie.mkv") };
        info.ProviderIds["Tmdb"] = "788";
        Assert.True((await CreateProvider(handler).GetMetadata(info, default)).HasMetadata);
        Assert.False(handler.FailedRequest);
    }

    [Theory]
    [InlineData("people")]
    [InlineData("fallback")]
    public async Task ActualRefreshCancellationStillPropagates(string failure)
    {
        using var context = new CompanionTestContext();
        context.Config.TmdbApiKey = "test";
        using var cancellation = new CancellationTokenSource();
        var handler = new MetadataHandler(failure, 2300, "Космический Джем", 1996) { CancelCaller = cancellation };
        var info = new MovieInfo { Path = Path.Combine(context.Root, "movie.mkv") };
        info.ProviderIds["Tmdb"] = "2300";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateProvider(handler).GetMetadata(info, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostRefreshCorrectsSeededYearUnlessLocked(bool locked)
    {
        using var context = new CompanionTestContext();
        context.Config.TmdbApiKey = "test";
        var handler = new MetadataHandler("none", 2300, "Космический Джем", 1996);
        var movie = new Movie { Name = "Space Jam", Path = Path.Combine(context.Root, "movie.mkv"), ProductionYear = 2026, IsLocked = locked };
        movie.SetProviderId("Tmdb", "2300");
        await CreateProvider(handler).FetchAsync(movie, new MetadataRefreshOptions(Moq.Mock.Of<IDirectoryService>()), default);
        Assert.Equal(locked ? 2026 : 1996, movie.ProductionYear);
        Assert.Equal(locked ? "Space Jam" : "Космический Джем", movie.Name);
    }

    private static RussianMovieProvider CreateProvider(MetadataHandler handler) =>
        new(new ClientFactory(handler), NullLogger<RussianMovieProvider>.Instance, _ => new HttpClient(handler, false));

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class MetadataHandler(string failure, int id, string title, int year) : HttpMessageHandler
    {
        private static int _nextPersonId = 900000;
        public int PersonId { get; } = Interlocked.Increment(ref _nextPersonId);
        public bool CompleteText { get; init; }
        public bool FailedRequest { get; private set; }
        public List<string> RequestedUrls { get; } = [];
        public CancellationTokenSource? CancelCaller { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            RequestedUrls.Add(url);
            string json;
            if (url.Contains("api.themoviedb.org"))
                json = JsonSerializer.Serialize(new { id, title, imdb_id = "tt0117705", release_date = $"{year}-11-15", overview = failure == "fallback" && !CompleteText ? "English fallback" : "Русское описание", production_companies = new[] { new { id = 17, name = "Studio" } }, credits = new { cast = new[] { new { id = PersonId, name = "Actor", order = 0 } } } });
            else
            {
                if ((failure == "studio" && url.Contains("P11806")) || (failure == "people" && url.Contains("P4985")) || (failure == "fallback" && url.Contains("P345")))
                {
                    FailedRequest = true;
                    CancelCaller?.Cancel();
                    throw new TaskCanceledException("Simulated HttpClient.Timeout", new TimeoutException(), token);
                }
                json = """{"results":{"bindings":[]}}""";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
