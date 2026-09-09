using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.Companion.Configuration;
using Xunit;

namespace RussianMetadata.Tests;

public sealed class MovieTextLocalizationTests
{
    [Theory]
    [InlineData("Парк Юрского периода III", true)]
    [InlineData("This time, it's not just a walk in the park!", false)]
    [InlineData("Jurassic Park III", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsCyrillic_DetectsRussianText(
        string? value,
        bool expected)
    {
        Assert.Equal(expected, MovieTextLocalization.ContainsCyrillic(value));
    }

    [Fact]
    public void RussianOrNull_RejectsEnglishFallback()
    {
        Assert.Null(MovieTextLocalization.RussianOrNull("Jurassic Park III"));
        Assert.Equal(
            "Парк Юрского периода III",
            MovieTextLocalization.RussianOrNull(" Парк Юрского периода III "));
    }

    [Theory]
    [InlineData("Director", PersonKind.Director)]
    [InlineData("Screenplay", PersonKind.Writer)]
    [InlineData("Original Music Composer", PersonKind.Composer)]
    public void MapCrewJob_MapsSupportedCredits(
        string job,
        PersonKind expected)
    {
        Assert.Equal(expected, MovieTextLocalization.MapCrewJob(job));
    }

    [Fact]
    public void TmdbMovieDetails_DeserializesLocalizedFieldsAndCredits()
    {
        const string Json = """
            {
              "title": "Парк Юрского периода III",
              "tagline": "Этим летом — не ходите в высокую траву",
              "genres": [{ "id": 12, "name": "Приключения" }],
              "production_companies": [{ "id": 33, "name": "Universal Pictures" }],
              "credits": {
                "cast": [{
                  "id": 2231,
                  "name": "Sam Neill",
                  "character": "Dr. Alan Grant",
                  "order": 0,
                  "profile_path": "/sam.jpg"
                }]
              }
            }
            """;

        var details = JsonSerializer.Deserialize<TmdbMovieDetails>(
            Json,
            JsonOptions.Default);

        Assert.NotNull(details);
        Assert.Equal("Парк Юрского периода III", details.Title);
        Assert.Equal("Приключения", Assert.Single(details.Genres!).Name);
        Assert.Equal(33, Assert.Single(details.ProductionCompanies!).Id);
        Assert.Equal(2231, Assert.Single(details.Credits!.Cast!).Id);
    }

    [Fact]
    public void TmdbMovieDetails_DeserializesCollectionMembership()
    {
        const string Json = """
            {
              "id": 348,
              "title": "Чужой",
              "belongs_to_collection": {
                "id": 8091,
                "name": "Alien Collection"
              }
            }
            """;

        var details = JsonSerializer.Deserialize<TmdbMovieDetails>(
            Json,
            JsonOptions.Default);

        Assert.NotNull(details?.BelongsToCollection);
        Assert.Equal(8091, details.BelongsToCollection.Id);
        Assert.Equal("Alien Collection", details.BelongsToCollection.Name);
    }

    [Fact]
    public void ApplyTmdbCollection_PreservesBoxSetManagerIdentity()
    {
        var movie = new Movie();

        var changed = RussianMovieProvider.ApplyTmdbCollection(
            movie,
            new TmdbCollectionReference
            {
                Id = 8091,
                Name = " Alien Collection "
            });

        Assert.True(changed);
        Assert.Equal(
            "8091",
            movie.GetProviderId(MetadataProvider.TmdbCollection));
        Assert.Equal("Alien Collection", movie.TmdbCollectionName);
        Assert.False(RussianMovieProvider.ApplyTmdbCollection(
            movie,
            new TmdbCollectionReference
            {
                Id = 8091,
                Name = "Alien Collection"
            }));
    }

    [Fact]
    public void ApplyTmdbCollection_RemovesStaleMembership()
    {
        var movie = new Movie { TmdbCollectionName = "Alien Collection" };
        movie.SetProviderId(MetadataProvider.TmdbCollection, "8091");

        var changed = RussianMovieProvider.ApplyTmdbCollection(movie, null);

        Assert.True(changed);
        Assert.Null(movie.GetProviderId(MetadataProvider.TmdbCollection));
        Assert.Null(movie.TmdbCollectionName);
    }

    [Fact]
    public void Configuration_EnablesFieldLevelLocalizationByDefault()
    {
        var configuration = new PluginConfiguration();

        Assert.True(configuration.EnableRussianTitles);
        Assert.True(configuration.EnableRussianOverviews);
        Assert.True(configuration.EnableRussianTaglines);
        Assert.True(configuration.EnableRussianGenres);
        Assert.True(configuration.EnableRussianStudios);
        Assert.True(configuration.EnableRussianPeople);
        Assert.Equal(
            ArtworkLanguagePreference.EnglishFirst,
            configuration.ForeignMoviePosterPreference);
        Assert.Equal(
            ArtworkLanguagePreference.EnglishFirst,
            configuration.ForeignMovieLogoPreference);
        Assert.Equal(
            ArtworkLanguagePreference.RussianFirst,
            configuration.RussianMoviePosterPreference);
        Assert.Equal(
            ArtworkLanguagePreference.RussianFirst,
            configuration.RussianMovieLogoPreference);
        Assert.Equal(
            ArtworkLanguagePreference.EnglishFirst,
            configuration.CollectionPosterPreference);
        Assert.Equal(
            ArtworkLanguagePreference.EnglishFirst,
            configuration.CollectionLogoPreference);
    }
}
