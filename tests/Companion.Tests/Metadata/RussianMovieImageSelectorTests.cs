using System;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.Companion.Configuration;
using Xunit;

namespace RussianMetadata.Tests;

public sealed class RussianMovieImageSelectorTests
{
    [Fact]
    public void Select_RussianFirst_PutsRussianBeforeHigherRatedEnglish()
    {
        var images = ImagesWithBothLanguages();

        var result = ArtworkSelector.Select(
            images,
            ArtworkLanguagePreference.RussianFirst,
            ArtworkLanguagePreference.RussianFirst,
            "test");

        Assert.Equal(
            ["ru-poster.jpg", "en-poster.jpg", "ru-logo.png", "en-logo.png"],
            FileNames(result));
        Assert.DoesNotContain(
            result,
            image => image.Url.EndsWith(
                "untagged-poster.jpg",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Select_EnglishFirst_PutsEnglishBeforeRussian()
    {
        var result = ArtworkSelector.Select(
            ImagesWithBothLanguages(),
            ArtworkLanguagePreference.EnglishFirst,
            ArtworkLanguagePreference.EnglishFirst,
            "test");

        Assert.Equal(
            ["en-poster.jpg", "ru-poster.jpg", "en-logo.png", "ru-logo.png"],
            FileNames(result));
    }

    [Fact]
    public void Select_DisabledType_ReturnsOnlyEnabledType()
    {
        var result = ArtworkSelector.Select(
            ImagesWithBothLanguages(),
            ArtworkLanguagePreference.Disabled,
            ArtworkLanguagePreference.RussianFirst,
            "test");

        Assert.All(result, image => Assert.Equal(ImageType.Logo, image.Type));
    }

    [Fact]
    public void Select_OrdersWithinLanguageByRatingThenVotes()
    {
        var images = new TmdbArtworkImages
        {
            Posters =
            [
                Image("/second.jpg", "ru", 7.0, 2),
                Image("/third.jpg", "ru", 6.0, 500),
                Image("/first.jpg", "ru", 7.0, 20)
            ]
        };

        var result = ArtworkSelector.Select(
            images,
            ArtworkLanguagePreference.RussianFirst,
            ArtworkLanguagePreference.Disabled,
            "test");

        Assert.Equal(
            ["first.jpg", "second.jpg", "third.jpg"],
            FileNames(result));
    }

    [Fact]
    public void IsRussian_UsesOriginOrProductionCountry()
    {
        Assert.True(MovieOriginClassifier.IsRussian(new TmdbMovieArtworkResponse
        {
            OriginCountry = ["RU"],
            OriginalLanguage = "en"
        }));
        Assert.True(MovieOriginClassifier.IsRussian(new TmdbMovieArtworkResponse
        {
            ProductionCountries = [new TmdbProductionCountry { Code = "RU" }],
            OriginalLanguage = "en"
        }));
    }

    [Fact]
    public void IsRussian_UsesOriginalLanguageOnlyWhenCountryIsMissing()
    {
        Assert.True(MovieOriginClassifier.IsRussian(new TmdbMovieArtworkResponse
        {
            OriginalLanguage = "ru"
        }));
        Assert.False(MovieOriginClassifier.IsRussian(new TmdbMovieArtworkResponse
        {
            OriginCountry = ["US"],
            OriginalLanguage = "ru"
        }));
    }

    [Fact]
    public void ArtworkResponse_DeserializesCountryAndAppendedImages()
    {
        const string Json = """
            {
              "origin_country": ["RU"],
              "production_countries": [
                { "iso_3166_1": "RU", "name": "Russia" }
              ],
              "original_language": "ru",
              "images": {
                "posters": [{
                  "file_path": "/poster.jpg",
                  "iso_639_1": "ru",
                  "vote_average": 7.2,
                  "vote_count": 12
                }],
                "logos": [{
                  "file_path": "/logo.png",
                  "iso_639_1": "en"
                }]
              }
            }
            """;

        var movie = JsonSerializer.Deserialize<TmdbMovieArtworkResponse>(
            Json,
            JsonOptions.Default);

        Assert.NotNull(movie);
        Assert.True(MovieOriginClassifier.IsRussian(movie));
        Assert.Equal("ru", Assert.Single(movie.Images!.Posters!).Language);
        Assert.Equal("en", Assert.Single(movie.Images.Logos!).Language);
    }

    [Fact]
    public void CollectionArtwork_DeserializesPostersWithoutLogos()
    {
        const string Json = """
            {
              "images": {
                "posters": [{
                  "file_path": "/collection.jpg",
                  "iso_639_1": "ru"
                }],
                "backdrops": []
              }
            }
            """;

        var collection =
            JsonSerializer.Deserialize<TmdbCollectionArtworkResponse>(
                Json,
                JsonOptions.Default);
        var selected = ArtworkSelector.Select(
            collection!.Images,
            ArtworkLanguagePreference.RussianFirst,
            ArtworkLanguagePreference.Disabled,
            "test");

        Assert.Equal(
            ImageType.Primary,
            Assert.Single(selected).Type);
    }

    [Fact]
    public void FanartCollectionLogos_UseConfiguredLanguageThenResolution()
    {
        var artwork = new FanartMovieArtwork
        {
            HdMovieLogos =
            [
                FanartImage("ru-hd.png", "ru", "2", "800", "310"),
                FanartImage("en-hd.png", "en", "10", "800", "310")
            ],
            MovieLogos =
            [
                FanartImage("ru-sd.png", "ru", "100", "400", "155")
            ]
        };

        var result = FanartLogoSelector.Select(
            artwork,
            ArtworkLanguagePreference.RussianFirst,
            "test");

        Assert.Equal(
            ["ru-hd.png", "ru-sd.png", "en-hd.png"],
            FileNames(result));
        Assert.All(result, image => Assert.Equal(ImageType.Logo, image.Type));
    }

    private static TmdbArtworkImages ImagesWithBothLanguages()
    {
        return new TmdbArtworkImages
        {
            Posters =
            [
                Image("/ru-poster.jpg", "ru", 7.5, 10),
                Image("/en-poster.jpg", "en", 9.0, 100),
                Image("/untagged-poster.jpg", null, 10.0, 200)
            ],
            Logos =
            [
                Image("/ru-logo.png", "ru", 6.0, 5),
                Image("/en-logo.png", "en", 8.0, 50)
            ]
        };
    }

    private static string[] FileNames(
        System.Collections.Generic.IEnumerable<
            MediaBrowser.Model.Providers.RemoteImageInfo> images)
    {
        return images
            .Select(image => new Uri(image.Url).Segments.Last())
            .ToArray();
    }

    private static TmdbImageFile Image(
        string path,
        string? language,
        double rating,
        int votes)
    {
        return new TmdbImageFile
        {
            FilePath = path,
            Language = language,
            VoteAverage = rating,
            VoteCount = votes,
            Width = 1000,
            Height = 1500
        };
    }

    private static FanartImage FanartImage(
        string fileName,
        string language,
        string likes,
        string width,
        string height)
    {
        return new FanartImage
        {
            Url = $"https://assets.fanart.tv/{fileName}",
            Language = language,
            Likes = likes,
            Width = width,
            Height = height
        };
    }
}
