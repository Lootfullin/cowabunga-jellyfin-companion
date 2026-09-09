using Jellyfin.Plugin.Companion.Configuration;
using Xunit;

namespace RussianMetadata.Tests;

public sealed class ArtworkPreferenceMigrationTaskTests
{
    [Fact]
    public void MigrationQueuesOnlyConfiguredImageFamilies()
    {
        var request = ArtworkPreferenceMigrationTask.CreateRefreshRequest(
            new PluginConfiguration
            {
                ForeignMoviePosterPreference = ArtworkLanguagePreference.Disabled,
                RussianMoviePosterPreference = ArtworkLanguagePreference.Disabled,
                ForeignMovieLogoPreference = ArtworkLanguagePreference.EnglishFirst,
                RussianMovieLogoPreference = ArtworkLanguagePreference.Disabled,
                CollectionPosterPreference = ArtworkLanguagePreference.EnglishFirst,
                CollectionLogoPreference = ArtworkLanguagePreference.Disabled,
            });

        Assert.False(request.MoviePosters);
        Assert.True(request.MovieLogos);
        Assert.True(request.CollectionPosters);
        Assert.False(request.CollectionLogos);
    }
}
