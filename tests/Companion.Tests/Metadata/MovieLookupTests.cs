using System.Collections.Generic;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace RussianMetadata.Tests;

public sealed class MovieLookupTests
{
    [Fact]
    public void MovieProvider_RunsAfterRemoteMergeAsCustomProvider()
    {
        Assert.True(
            typeof(ICustomMetadataProvider<Movie>).IsAssignableFrom(
                typeof(RussianMovieProvider)));
    }

    [Theory]
    [InlineData("(1967) You Only Live Twice", null, "You Only Live Twice", 1967)]
    [InlineData("You Only Live Twice (1967)", null, "You Only Live Twice", 1967)]
    [InlineData("\"007: Живёшь только дважды\"", 1967, "007: Живёшь только дважды", 1967)]
    [InlineData("1917", 2019, "1917", 2019)]
    [InlineData("2012", 2009, "2012", 2009)]
    public void Parse_PreservesNumericTitlesAndExtractsDecorativeYear(
        string rawName,
        int? suppliedYear,
        string expectedName,
        int expectedYear)
    {
        var lookup = MovieLookup.Parse(rawName, suppliedYear);

        Assert.Equal(expectedName, lookup.Name);
        Assert.Equal(expectedYear, lookup.Year);
    }

    [Fact]
    public void ExtractTmdbId_IsCaseInsensitive()
    {
        var providerIds = new Dictionary<string, string>
        {
            ["TMDB"] = "667"
        };

        Assert.Equal(667, MovieLookup.ExtractTmdbId(providerIds));
    }

    [Fact]
    public void SelectCandidate_PrefersExactTitleAndYear()
    {
        var candidates = new List<TmdbSearchItem>
        {
            new() { Id = 999, Title = "Дважды", ReleaseDate = "1967-01-01" },
            new()
            {
                Id = 667,
                Title = "007: Живёшь только дважды",
                OriginalTitle = "You Only Live Twice",
                ReleaseDate = "1967-06-13"
            },
            new()
            {
                Id = 123,
                Title = "You Only Live Twice",
                ReleaseDate = "1990-01-01"
            }
        };

        var selected = MovieLookup.SelectCandidate(
            candidates,
            new MovieLookup("You Only Live Twice", 1967));

        Assert.Equal(667, selected?.Id);
    }

    [Fact]
    public void SelectCandidate_PrefersMatchingTitleOverExactYear()
    {
        var candidates = new List<TmdbSearchItem>
        {
            new()
            {
                Id = 1966,
                Title = "11-й конкурс песни «Евровидение»",
                OriginalTitle = "Eurovision Song Contest 1966",
                ReleaseDate = "1966-03-05"
            },
            new()
            {
                Id = 20803,
                Title = "Кавказская пленница, или Новые приключения Шурика",
                ReleaseDate = "1967-04-03"
            }
        };

        var selected = MovieLookup.SelectCandidate(
            candidates,
            new MovieLookup(
                "Кавказская пленница, или Новые приключения Шурика",
                1966));

        Assert.Equal(20803, selected?.Id);
    }

    [Fact]
    public void SelectCandidate_RejectsUnrelatedTitleDespiteExactYear()
    {
        var candidates = new List<TmdbSearchItem>
        {
            new()
            {
                Id = 1966,
                Title = "11-й конкурс песни «Евровидение»",
                ReleaseDate = "1966-03-05"
            }
        };

        var selected = MovieLookup.SelectCandidate(
            candidates,
            new MovieLookup(
                "Кавказская пленница, или Новые приключения Шурика",
                1966));

        Assert.Null(selected);
    }

    [Fact]
    public void SelectCandidate_RejectsAmbiguousSameTitleAndYear()
    {
        var candidates = new List<TmdbSearchItem>
        {
            new() { Id = 1, Title = "Blade", ReleaseDate = "1998-08-21" },
            new() { Id = 2, Title = "Blade", ReleaseDate = "1998-01-01" }
        };

        var selected = MovieLookup.SelectCandidate(
            candidates,
            new MovieLookup("Blade", 1998));

        Assert.Null(selected);
    }

    [Fact]
    public void SelectCandidate_RejectsPartialContainsMatch()
    {
        var candidates = new List<TmdbSearchItem>
        {
            new()
            {
                Id = 85723,
                Title = "Blade: The Iron Cross",
                ReleaseDate = "2020-06-26"
            }
        };

        var selected = MovieLookup.SelectCandidate(
            candidates,
            new MovieLookup("Iron Cross", 2020));

        Assert.Null(selected);
    }

    [Fact]
    public void SelectCandidate_RejectsTitleMatchWithWrongYear()
    {
        var candidates = new List<TmdbSearchItem>
        {
            new() { Id = 999, Title = "Blade", ReleaseDate = "1973-01-01" }
        };

        var selected = MovieLookup.SelectCandidate(
            candidates,
            new MovieLookup("Blade", 1998));

        Assert.Null(selected);
    }

    [Fact]
    public void TmdbMovieDetails_DeserializesImdbId()
    {
        const string json = """{"id":667,"imdb_id":"tt0062512"}""";

        var details = System.Text.Json.JsonSerializer.Deserialize<TmdbMovieDetails>(
            json,
            JsonOptions.Default);

        Assert.Equal("tt0062512", details?.ImdbId);
    }
}
