using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace RussianMetadata.Tests;

public sealed class SeriesSeasonMetadataTests
{
    [Fact]
    public void SeasonProvider_HandlesRemoteAndPostRefreshMetadata()
    {
        Assert.True(
            typeof(IRemoteMetadataProvider<Season, SeasonInfo>)
                .IsAssignableFrom(typeof(RussianSeasonProvider)));
        Assert.True(
            typeof(ICustomMetadataProvider<Season>)
                .IsAssignableFrom(typeof(RussianSeasonProvider)));
    }

    [Fact]
    public void SeriesDetails_DeserializesGenresAndCredits()
    {
        const string json = """
            {
              "id": 1399,
              "name": "Игра престолов",
              "genres": [{ "id": 18, "name": "драма" }],
              "credits": {
                "cast": [{
                  "id": 48,
                  "name": "Sean Bean",
                  "character": "Eddard Stark",
                  "order": 0
                }]
              }
            }
            """;

        var details = JsonSerializer.Deserialize<SeriesTmdbTvDetails>(
            json,
            JsonOptions.Default);

        Assert.Equal("драма", Assert.Single(details!.Genres!).Name);
        Assert.Equal(48, Assert.Single(details.Credits!.Cast!).Id);
    }

    [Fact]
    public void SeasonDetails_DeserializesRussianDataAndCredits()
    {
        const string json = """
            {
              "id": 3624,
              "name": "Сезон 1",
              "overview": "Первый сезон сериала.",
              "air_date": "2011-04-17",
              "credits": {
                "cast": [{
                  "id": 48,
                  "name": "Sean Bean",
                  "character": "Eddard Stark",
                  "order": 0
                }]
              }
            }
            """;

        var details = JsonSerializer.Deserialize<TmdbSeasonDetails>(
            json,
            JsonOptions.Default);

        Assert.Equal("Сезон 1", details?.Name);
        Assert.Equal("2011-04-17", details?.AirDate);
        Assert.Equal(48, Assert.Single(details!.Credits!.Cast!).Id);
    }

    [Fact]
    public void CanonicalPersonMapping_UsesSameRussianNameAndTmdbId()
    {
        var credits = new TmdbCredits
        {
            Cast =
            [
                new TmdbCastMember
                {
                    Id = 738,
                    Name = "Sean Connery",
                    Character = "James Bond",
                    Order = 0
                }
            ]
        };
        var russianNames = new Dictionary<int, string>
        {
            [738] = "Шон Коннери"
        };

        var person = Assert.Single(
            TmdbPeopleLocalization.CreateLocalizedPeople(
                credits,
                russianNames));

        Assert.Equal("Шон Коннери", person.Name);
        Assert.Equal("738", person.ProviderIds["Tmdb"]);
    }

    [Fact]
    public void SeriesLookup_SelectsUniqueTitleAndYearMatch()
    {
        var candidates = new List<SeriesTmdbTvSearchItem>
        {
            new() { Id = 100, Name = "Шерлок Холмс", FirstAirDate = "1984-01-01" },
            new()
            {
                Id = 19885,
                Name = "Шерлок",
                OriginalName = "Sherlock",
                FirstAirDate = "2010-07-25"
            }
        };

        var selected = SeriesLookup.SelectCandidate(
            candidates,
            new SeriesLookup("Sherlock", 2010));

        Assert.Equal(19885, selected?.Id);
    }

    [Fact]
    public void SeriesLookup_RejectsFirstResultWhenYearDoesNotMatch()
    {
        var candidates = new List<SeriesTmdbTvSearchItem>
        {
            new() { Id = 100, Name = "Sherlock", FirstAirDate = "1984-01-01" }
        };

        var selected = SeriesLookup.SelectCandidate(
            candidates,
            new SeriesLookup("Sherlock", 2010));

        Assert.Null(selected);
    }

    [Fact]
    public void SeriesLookup_RejectsAmbiguousSameTitleWithoutYear()
    {
        var candidates = new List<SeriesTmdbTvSearchItem>
        {
            new() { Id = 1, Name = "The Office", FirstAirDate = "2001-07-09" },
            new() { Id = 2, Name = "The Office", FirstAirDate = "2005-03-24" }
        };

        var selected = SeriesLookup.SelectCandidate(
            candidates,
            new SeriesLookup("The Office", null));

        Assert.Null(selected);
    }

    [Fact]
    public void SeriesLookup_RejectsPartialContainsMatch()
    {
        var candidates = new List<SeriesTmdbTvSearchItem>
        {
            new() { Id = 1, Name = "Star Wars Rebels", FirstAirDate = "2014-10-03" }
        };

        var selected = SeriesLookup.SelectCandidate(
            candidates,
            new SeriesLookup("Rebels", 2014));

        Assert.Null(selected);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void PersonLookup_RetriesOnlyTransientHttpFailures(
        HttpStatusCode statusCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            TmdbPeopleLocalization.ShouldRetry(statusCode));
    }
}
