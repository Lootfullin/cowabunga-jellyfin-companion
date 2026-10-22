using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.CustomArtwork;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

public sealed class ArtworkPreviewTests
{
    private static ArtworkPreview Preview(string path = "_Previews/aa/hash/poster_mid.webp") => new()
    {
        Path = path, Sha256 = new string('b', 64), Size = 123, Width = 342, Height = 513, Mime = "image/webp",
    };

    [Theory]
    [InlineData(ImageType.Primary, "poster_mid")]
    [InlineData(ImageType.Logo, "logo_mid")]
    public void CandidateKeepsOriginalUrlAndUsesPreviewOnlyForThumbnail(ImageType type, string name)
    {
        var index = new ArtworkIndex(null!, null!, null!);
        var file = new ArtworkManifestFile { Path = "Movies/Test/poster.jpg", Sha256 = new string('a', 64),
            Previews = new() { [name] = Preview() } };
        var images = new List<RemoteImageInfo>();
        new ArtworkImageProvider(index, NullLogger<ArtworkImageProvider>.Instance).Add(images, file, type);
        var image = Assert.Single(images);
        Assert.Equal(index.GetArtworkUri(file.Path).AbsoluteUri, image.Url);
        Assert.Equal(index.GetArtworkUri(file.Previews[name]!.Path).AbsoluteUri, image.ThumbnailUrl);
    }

    [Theory]
    [InlineData("../secret.webp")]
    [InlineData("_Previews/../secret.webp")]
    [InlineData("https://other.example/secret.webp")]
    [InlineData("_Previews/bad.jpg")]
    public void InvalidPreviewFallsBackToOriginal(string path)
    {
        var index = new ArtworkIndex(null!, null!, null!);
        var file = new ArtworkManifestFile { Path = "Movies/Test/poster.jpg", Previews = new() { ["poster_mid"] = Preview(path) } };
        var images = new List<RemoteImageInfo>();
        new ArtworkImageProvider(index, NullLogger<ArtworkImageProvider>.Instance).Add(images, file, ImageType.Primary);
        var image = Assert.Single(images);
        Assert.Equal(image.Url, image.ThumbnailUrl);
    }

    [Fact]
    public void MissingOrMalformedPreviewDoesNotHideOriginal()
    {
        var index = new ArtworkIndex(null!, null!, null!);
        var file = new ArtworkManifestFile { Path = "Movies/Test/poster.jpg" };
        var provider = new ArtworkImageProvider(index, NullLogger<ArtworkImageProvider>.Instance);
        foreach (var previews in new Dictionary<string, ArtworkPreview?>?[] { null, new(), new() { ["poster_mid"] = null },
            new() { ["poster_mid"] = new ArtworkPreview { Path = null! } } })
        {
            file.Previews = previews;
            var images = new List<RemoteImageInfo>();
            provider.Add(images, file, ImageType.Primary);
            var image = Assert.Single(images);
            Assert.Equal(image.Url, image.ThumbnailUrl);
        }
    }

    [Fact]
    public async Task ManifestPreviewIsAllowedButUnlistedAndExternalUrlsAreRejectedAndPreviewChangesDoNotQueueImages()
    {
        using var context = new CompanionTestContext();
        context.Config.CollectionArtworkEnabled = true;
        var collection = new BoxSet { Id = Guid.NewGuid(), Name = "Example" };
        collection.SetProviderId(MetadataProvider.Tmdb, "10");
        context.Manager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { collection });
        var revision = new string('a', 64);
        var file = new ArtworkManifestFile { Path = "Collections/Example/poster.jpg", Sha256 = new string('c', 64),
            Size = 123, Scope = "collection", TmdbId = 10, ReleaseNames = ["Example"], Previews = new() { ["poster_mid"] = Preview() } };
        var manifest = new ArtworkManifest { SchemaVersion = 2, PreviewSchemaVersion = 1, Revision = revision, Files = [file] };
        // Simulate an old plugin cache that discarded the preview fields at the same revision.
        Directory.CreateDirectory(context.Plugin.DataFolderPath);
        File.WriteAllText(Path.Combine(context.Plugin.DataFolderPath, "artwork-index.v1.json"),
            JsonSerializer.Serialize(new ArtworkManifest { SchemaVersion = 2, Revision = revision, Files = [new ArtworkManifestFile {
                Path = file.Path, Sha256 = file.Sha256, Size = file.Size, Scope = file.Scope, TmdbId = file.TmdbId, ReleaseNames = file.ReleaseNames }] }));
        var handler = new Handler(() => manifest);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        var index = new ArtworkIndex(NullLogger<ArtworkIndex>.Instance, factory.Object, context.Manager.Object);
        await index.BuildAsync(null, default);
        index.AcknowledgeChanges();
        using var response = await index.GetArtworkResponseAsync(index.GetArtworkUri(file.Previews["poster_mid"]!.Path).AbsoluteUri, default);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => index.GetArtworkResponseAsync(index.GetArtworkUri("_Previews/unlisted.webp").AbsoluteUri, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => index.GetArtworkResponseAsync("https://other.example/Media/" + file.Previews["poster_mid"]!.Path, default));
        manifest.Revision = new string('d', 64);
        file.Previews["poster_mid"] = Preview("_Previews/dd/hash/poster_mid.webp");
        await index.BuildAsync(null, default);
        Assert.Empty(index.ChangedImageTypes);
        using var updated = await index.GetArtworkResponseAsync(index.GetArtworkUri(file.Previews["poster_mid"]!.Path).AbsoluteUri, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => index.GetArtworkResponseAsync(index.GetArtworkUri("_Previews/aa/hash/poster_mid.webp").AbsoluteUri, default));
    }

    private sealed class Handler(Func<ArtworkManifest> manifest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var contents = path.Contains("revision") ? JsonSerializer.Serialize(new { revision = manifest().Revision, preview_schema_version = manifest().PreviewSchemaVersion })
                : path.EndsWith(".json") ? JsonSerializer.Serialize(manifest()) : "preview bytes";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(contents) });
        }
    }
}
