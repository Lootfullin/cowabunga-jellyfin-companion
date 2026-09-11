using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.CustomArtwork;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

public sealed class CollectionArtworkStorageTests
{
    [Theory]
    [InlineData(ImageType.Primary, false)]
    [InlineData(ImageType.Primary, true)]
    [InlineData(ImageType.Logo, false)]
    [InlineData(ImageType.Logo, true)]
    public async Task NativeCollectionReplacementRetiresOldAlternativesEvenWhenCurrentCloudFileAlreadyMatches(ImageType type, bool alreadyCloud)
    {
        using var context = new CompanionTestContext();
        context.Config.ReplaceExistingImages = true;
        context.Config.CollectionArtworkEnabled = true;
        context.Config.StorageMode = "MediaFolder";
        var item = new BoxSet { Id = Guid.NewGuid(), Path = Path.Combine(context.Plugin.CollectionsPath, "Test"), Name = "Test" };
        item.SetProviderId(MetadataProvider.Tmdb, "870339");
        Directory.CreateDirectory(item.Path);
        var alias = Path.Combine(item.Path, type == ImageType.Primary ? "folder.png" : "logo.jpg");
        File.WriteAllBytes(alias, [9,9,9]);
        var untouched = Path.Combine(item.Path, "backdrop.jpg");
        File.WriteAllBytes(untouched, [8,8,8]);
        var existing = alias;
        if (alreadyCloud)
        {
            existing = Path.Combine(context.Root, "old-internal-image.jpg");
            File.WriteAllBytes(existing, [7,7,7]);
        }
        item.ImageInfos = [new ItemImageInfo { Type = type, Path = existing }];
        context.Manager.Setup(m => m.GetItemById(item.Id)).Returns(item);
        context.Manager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([item]);
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] {7,7,7}));
        var revision = new string('a',64);
        var filename = type == ImageType.Primary ? "poster.jpg" : "clearlogo.png";
        var manifest = JsonSerializer.Serialize(new { schema_version=2, revision, files=new[] {
            new {path="Collections/Test/"+filename, sha256=hash, size=3, release_names=new[]{"Test"}, scope="collection", tmdb_id=870339}
        }});
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch {
            "/artwork-index.revision.json" => new StringContent(JsonSerializer.Serialize(new {revision})),
            "/artwork-index.v1.json" => new StringContent(manifest),
            _ => new ByteArrayContent([7,7,7])
        }));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        var index = new ArtworkIndex(NullLogger<ArtworkIndex>.Instance, factory.Object, context.Manager.Object);
        var provider = new Mock<IProviderManager>();
        var saved = Path.Combine(item.Path, filename);
        provider.Setup(p => p.SaveImage(item, It.IsAny<string>(), It.IsAny<string>(), type, 0, null, It.IsAny<CancellationToken>()))
            .Returns((BaseItem i,string path,string mime,ImageType t,int? n,bool? local,CancellationToken token) => {
                File.Copy(path,saved,true);i.ImageInfos=[new ItemImageInfo {Type=t,Path=saved}];return Task.CompletedTask;
            });
        using var coordinator = new ArtworkCoordinator(context.Manager.Object, provider.Object, Mock.Of<IFileSystem>(), index, NullLogger<ArtworkCoordinator>.Instance);
        Assert.True(await coordinator.ApplyAsync(new() { ItemId=item.Id, Type=type, Module=CompanionModule.Artwork }, default));
        Assert.Equal(new byte[]{7,7,7}, File.ReadAllBytes(saved));
        Assert.False(File.Exists(alias));
        Assert.Equal(new byte[]{8,8,8}, File.ReadAllBytes(untouched));
        Assert.Equal(new byte[]{9,9,9}, File.ReadAllBytes(Directory.GetFiles(Path.Combine(context.Plugin.DataFolderPath,"collection-image-backups",item.Id.ToString("N"))).Single()));
        provider.Verify(p => p.SaveImage(item,It.IsAny<string>(),It.IsAny<string>(),type,0,null,It.IsAny<CancellationToken>()),Times.Once);
    }

    [Fact]
    public void ExternalAndSiblingCollectionFoldersNeverUseNativeStorageOrRetireFiles()
    {
        using var context = new CompanionTestContext();
        foreach (var path in new[] { Path.Combine(context.Root,"media","Test"), context.Plugin.CollectionsPath+"-other", context.Plugin.CollectionsPath })
        {
            var item = new BoxSet { Path=path, Id=Guid.NewGuid() };Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path,"poster.jpg"),"keep");
            Assert.False(CollectionArtworkStorage.UsesNativeFolder(item));
            CollectionArtworkStorage.RetireAliases(item,ImageType.Primary,Path.Combine(path,"folder.jpg"));
            Assert.True(File.Exists(Path.Combine(path,"poster.jpg")));
        }
    }

    [Fact]
    public async Task UnmanagedCollectionFilesRemainProtectedWithoutReplacementPermission()
    {
        using var context = new CompanionTestContext();
        context.Config.CollectionArtworkEnabled = true;
        var item = new BoxSet { Id=Guid.NewGuid(), Path=Path.Combine(context.Plugin.CollectionsPath,"Protected") };
        Directory.CreateDirectory(item.Path);File.WriteAllBytes(Path.Combine(item.Path,"logo.jpg"),[9,9,9]);
        context.Manager.Setup(m=>m.GetItemById(item.Id)).Returns(item);
        var providers = new Mock<IProviderManager>(MockBehavior.Strict);
        using var coordinator = new ArtworkCoordinator(context.Manager.Object,providers.Object,Mock.Of<IFileSystem>(),
            new ArtworkIndex(NullLogger<ArtworkIndex>.Instance,Mock.Of<IHttpClientFactory>(),context.Manager.Object),NullLogger<ArtworkCoordinator>.Instance);
        Assert.True(await coordinator.ApplyAsync(new() {ItemId=item.Id,Type=ImageType.Logo,Module=CompanionModule.Artwork},default));
        Assert.Equal(new byte[]{9,9,9},File.ReadAllBytes(Path.Combine(item.Path,"logo.jpg")));
        Assert.Empty(providers.Invocations);
    }

    private sealed class Handler(Func<HttpRequestMessage,HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=content(request) });
    }
}
