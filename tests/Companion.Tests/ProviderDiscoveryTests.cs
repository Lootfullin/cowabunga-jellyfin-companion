using Jellyfin.Plugin.CustomArtwork;
using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RussianMetadata;

public sealed class ProviderDiscoveryTests
{
    [Fact]
    public void LanguageArtworkHidesForDisabledRealItemsButRemainsDiscoverable()
    {
        using var context = new CompanionTestContext();
        using var movies = new RussianMovieImageProvider(NullLogger<RussianMovieImageProvider>.Instance);
        using var collections = new ChooseYourMetaBoxSetImageProvider(NullLogger<ChooseYourMetaBoxSetImageProvider>.Instance);
        var movie = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(context.Root, "movie.mkv") };
        var collection = new BoxSet { Id = Guid.NewGuid() };
        context.Config.CollectionMetadataEnabled = true;
        Assert.True(movies.Supports(movie));
        Assert.True(collections.Supports(collection));
        context.Config.MetadataImagesEnabled = false;
        Assert.False(movies.Supports(movie));
        Assert.False(collections.Supports(collection));
        Assert.True(movies.Supports(new Movie()));
        Assert.True(collections.Supports(new BoxSet()));
        context.Config.MetadataImagesEnabled = true;
        context.Config.AllLibraries = false;
        context.Config.CollectionMetadataEnabled = false;
        Assert.False(movies.Supports(movie));
        Assert.False(collections.Supports(collection));
        context.Config.AllLibraries = true;
        context.Config.MetadataEnabled = false;
        Assert.False(movies.Supports(movie));
    }

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

    [Theory]
    [InlineData("Movie")]
    [InlineData("Series")]
    [InlineData("Season")]
    public async Task MediaFolderModeListsCloudImagesWithoutWritingMediaFiles(string kind)
    {
        using var context = new CompanionTestContext();
        context.Config.StorageMode = PluginConfiguration.MediaFolderStorage;
        var http = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var index = new ArtworkIndex(NullLogger<ArtworkIndex>.Instance, http.Object, context.Manager.Object);
        var provider = new ArtworkImageProvider(index, NullLogger<ArtworkImageProvider>.Instance);
        MediaBrowser.Controller.Entities.BaseItem item = kind switch
        {
            "Series" => new MediaBrowser.Controller.Entities.TV.Series(),
            "Season" => new MediaBrowser.Controller.Entities.TV.Season(),
            _ => new Movie()
        };
        item.Id = Guid.NewGuid();
        item.Path = Path.Combine(context.Root, "movie.mkv");
        var poster = new ArtworkManifestFile { Path = "Movies/Heat/poster.jpg", Sha256 = new string('a', 64), Size = 123 };
        var logo = new ArtworkManifestFile { Path = "Movies/Heat/clearlogo.png", Sha256 = new string('b', 64), Size = 123 };
        typeof(ArtworkIndex).GetField("_byItemId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(index, new Dictionary<Guid, ArtworkSet> { [item.Id] = new() { Poster = poster, Logo = logo } });
        typeof(ArtworkIndex).GetProperty(nameof(ArtworkIndex.BuiltUtc))!.SetValue(index, DateTime.UtcNow);
        var before = Directory.GetFiles(context.Root, "*", SearchOption.AllDirectories);
        Assert.True(provider.Supports(item));
        var images = (await provider.GetImages(item, default)).ToList();
        Assert.Equal(2, images.Count);
        Assert.Contains(images, image => image.Type == MediaBrowser.Model.Entities.ImageType.Primary && image.Url.EndsWith("poster.jpg"));
        Assert.Contains(images, image => image.Type == MediaBrowser.Model.Entities.ImageType.Logo && image.Url.EndsWith("clearlogo.png"));
        Assert.Equal(before, Directory.GetFiles(context.Root, "*", SearchOption.AllDirectories));
        context.Config.AllLibraries = false;
        Assert.Empty(await provider.GetImages(item, default));
        http.VerifyNoOtherCalls();
    }
}
