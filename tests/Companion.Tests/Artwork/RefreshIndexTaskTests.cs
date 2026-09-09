using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.CustomArtwork.Tests;

public sealed class RefreshIndexTaskTests
{
    [Fact]
    public void LogoOnlyArtworkDoesNotReplacePoster()
    {
        var artwork = new ArtworkSet
        {
            Logo = new ArtworkManifestFile { Path = "clearlogo.png" },
        };

        var types = RefreshIndexTask.GetRefreshImageTypes(
            artwork,
            new PluginConfiguration { Posters = true, Logos = true });

        Assert.Equal([ImageType.Logo], types);
    }

    [Fact]
    public void PosterOnlyArtworkDoesNotReplaceLogo()
    {
        var artwork = new ArtworkSet
        {
            Poster = new ArtworkManifestFile { Path = "poster.jpg" },
        };

        var types = RefreshIndexTask.GetRefreshImageTypes(
            artwork,
            new PluginConfiguration { Posters = true, Logos = true });

        Assert.Equal([ImageType.Primary], types);
    }

    [Fact]
    public void RemovedArtworkRestoresEnabledFallbackRoles()
    {
        var types = RefreshIndexTask.GetRefreshImageTypes(
            artwork: null,
            new PluginConfiguration { Posters = true, Logos = false });

        Assert.Equal([ImageType.Primary], types);
    }

    [Theory]
    [InlineData("poster-old|logo", "poster-new|logo", ImageType.Primary)]
    [InlineData("poster|logo-old", "poster|logo-new", ImageType.Logo)]
    [InlineData("poster|logo", "|logo", ImageType.Primary)]
    [InlineData("poster|logo", null, ImageType.Primary, ImageType.Logo)]
    public void IndexTracksChangedImageRoles(
        string previous,
        string? current,
        params ImageType[] expected)
    {
        Assert.Equal(
            expected,
            ArtworkIndex.GetChangedImageTypes(previous, current));
    }

    [Fact]
    public void CollectionRefreshesOnlyRoleRemovedFromCloud()
    {
        var artwork = new ArtworkSet
        {
            Logo = new ArtworkManifestFile { Path = "clearlogo.png" },
        };

        var result = RefreshIndexTask.GetRemovedImageTypes(
            artwork,
            [ImageType.Primary, ImageType.Logo]);

        Assert.Equal([ImageType.Primary], result);
    }

    [Fact]
    public void RefreshQueueDropsEmptyIdsAndMergesDuplicateRoles()
    {
        var itemId = Guid.NewGuid();
        var requests = new[]
        {
            new ArtworkRefreshRequest(Guid.Empty, [ImageType.Primary]),
            new ArtworkRefreshRequest(itemId, [ImageType.Primary]),
            new ArtworkRefreshRequest(itemId, [ImageType.Primary, ImageType.Logo]),
        };

        var merged = RefreshIndexTask.MergeRefreshRequests(requests);

        var request = Assert.Single(merged);
        Assert.Equal(itemId, request.ItemId);
        Assert.Equal([ImageType.Primary, ImageType.Logo], request.ImageTypes);
    }
}
