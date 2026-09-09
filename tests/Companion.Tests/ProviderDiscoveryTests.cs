using Jellyfin.Plugin.CustomArtwork;
using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RussianMetadata;

public sealed class ProviderDiscoveryTests
{
    [Fact]
    public async Task ProvidersAdvertiseTypesButDoNotFetchOutsideEnabledLibraries()
    {
        using var context = new CompanionTestContext(false);
        var http = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var index = new ArtworkIndex(NullLogger<ArtworkIndex>.Instance, http.Object, context.Manager.Object);
        var custom = new ArtworkImageProvider(index, NullLogger<ArtworkImageProvider>.Instance);
        using var movieProvider = new RussianMovieImageProvider(NullLogger<RussianMovieImageProvider>.Instance);
        using var collectionProvider = new ChooseYourMetaBoxSetImageProvider(NullLogger<ChooseYourMetaBoxSetImageProvider>.Instance);
        var movie = new Movie { Path = Path.Combine(context.Root, "dummy") };
        var collection = new BoxSet { Path = Path.Combine(context.Root, "dummy") };
        Assert.True(custom.Supports(movie));
        Assert.True(movieProvider.Supports(movie));
        Assert.True(collectionProvider.Supports(collection));
        Assert.Empty(await custom.GetImages(movie, default));
        Assert.Empty(await movieProvider.GetImages(movie, default));
        Assert.Empty(await collectionProvider.GetImages(collection, default));
        context.Config.Enabled = false;
        Assert.True(custom.Supports(movie));
        Assert.True(movieProvider.Supports(movie));
        Assert.Empty(await custom.GetImages(movie, default));
        Assert.Empty(await movieProvider.GetImages(movie, default));
        http.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MediaFolderModeAdvertisesCustomProviderWithoutRemoteMovieDownloads()
    {
        using var context = new CompanionTestContext();
        context.Config.StorageMode = PluginConfiguration.MediaFolderStorage;
        var http = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var index = new ArtworkIndex(NullLogger<ArtworkIndex>.Instance, http.Object, context.Manager.Object);
        var provider = new ArtworkImageProvider(index, NullLogger<ArtworkImageProvider>.Instance);
        var movie = new Movie { Path = Path.Combine(context.Root, "movie.mkv") };
        Assert.True(provider.Supports(movie));
        Assert.Empty(await provider.GetImages(movie, default));
        http.VerifyNoOtherCalls();
    }
}
