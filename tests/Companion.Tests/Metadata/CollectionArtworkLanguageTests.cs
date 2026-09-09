using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Extensions;
using MediaBrowser.Model.Providers;
using RussianMetadata;
using Xunit;

namespace Companion.Tests;

public class CollectionArtworkLanguageTests
{
    [Theory]
    [InlineData(ArtworkLanguagePreference.EnglishFirst, "en")]
    [InlineData(ArtworkLanguagePreference.RussianFirst, "ru")]
    public void MetadataLanguageCannotPromoteFallbackOverSelectedLanguage(ArtworkLanguagePreference preference, string expected)
    {
        var config = new PluginConfiguration { CollectionPosterPreference = preference, CollectionLogoPreference = preference };
        var images = new[] { Image("ru", ImageType.Primary), Image("en", ImageType.Primary), Image("ru", ImageType.Logo), Image("en", ImageType.Logo) };
        var metadataLanguage = expected == "en" ? "ru" : "en";
        Assert.NotEqual(expected, images.OrderByLanguageDescending(metadataLanguage).First().Language);
        var selected = ChooseYourMetaBoxSetImageProvider.SelectPreferredLanguages(images, config).OrderByLanguageDescending(metadataLanguage).ToList();
        Assert.Equal(2, selected.Count);
        Assert.All(selected, image => Assert.Equal(expected, image.Language));
        Assert.Contains(selected, image => image.Type == ImageType.Primary);
        Assert.Contains(selected, image => image.Type == ImageType.Logo);
    }

    [Fact]
    public void MissingEnglishLogoFallsBackWithoutChangingEnglishPosterSelection()
    {
        var images = new[] { Image("ru", ImageType.Primary), Image("en", ImageType.Primary), Image("ru", ImageType.Logo) };
        var selected = ChooseYourMetaBoxSetImageProvider.SelectPreferredLanguages(images, new PluginConfiguration()).ToList();
        Assert.Equal("en", Assert.Single(selected, image => image.Type == ImageType.Primary).Language);
        Assert.Equal("ru", Assert.Single(selected, image => image.Type == ImageType.Logo).Language);
    }

    [Fact]
    public void PosterAndLogoPreferencesAreIndependentAndDisabledRoleIsOmitted()
    {
        var config = new PluginConfiguration { CollectionPosterPreference = ArtworkLanguagePreference.Disabled, CollectionLogoPreference = ArtworkLanguagePreference.RussianFirst };
        var images = new[] { Image("en", ImageType.Primary), Image("en", ImageType.Logo), Image("ru", ImageType.Logo) };
        var selected = Assert.Single(ChooseYourMetaBoxSetImageProvider.SelectPreferredLanguages(images, config));
        Assert.Equal(ImageType.Logo, selected.Type);
        Assert.Equal("ru", selected.Language);
    }

    private static RemoteImageInfo Image(string language, ImageType type) => new() { Language = language, Type = type, Url = "https://example.test/" + language + type };
}
