using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.CustomArtwork;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

public sealed class NewCollectionArtworkTests
{
    [Fact]
    public async Task NewCollectionUsesDownloadedManifestWithoutLibraryRebuildOrAnotherHttpRequest()
    {
        using var context = new CompanionTestContext();
        context.Config.CollectionArtworkEnabled = true;
        context.Manager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
        var revision = new string('a', 64);
        var manifest = JsonSerializer.Serialize(new { schema_version=2,revision,files=new[] {
            new {path="Collections/Wicked/poster.jpg",sha256=new string('b',64),size=3,scope="collection",tmdb_id=968080,release_names=new[]{"Wicked Collection"}},
            new {path="Collections/Wicked/clearlogo.png",sha256=new string('c',64),size=3,scope="collection",tmdb_id=968080,release_names=new[]{"Wicked Collection"}}
        }});
        var handler = new ManifestHandler(JsonSerializer.Serialize(new {revision}),manifest);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();factory.Setup(f=>f.CreateClient(It.IsAny<string>())).Returns(client);
        var index = new ArtworkIndex(NullLogger<ArtworkIndex>.Instance,factory.Object,context.Manager.Object);
        await index.BuildAsync(null,default);
        Assert.Empty(index.Matches);
        var requests=handler.Requests;
        var collection = new BoxSet { Id=Guid.NewGuid(),Name="Злая (Коллекция)" };
        collection.SetProviderId(MetadataProvider.Tmdb,"968080");
        var provider = new ArtworkImageProvider(index,NullLogger<ArtworkImageProvider>.Instance);
        var images=(await provider.GetImages(collection,default)).ToArray();
        Assert.Equal(new[]{ImageType.Primary,ImageType.Logo},images.Select(i=>i.Type));
        Assert.Equal(requests,handler.Requests);
        context.Manager.Verify(m=>m.GetItemList(It.IsAny<InternalItemsQuery>()),Times.Once);
        context.Config.CollectionArtworkEnabled=false;
        Assert.Empty(await provider.GetImages(collection,default));
    }

    private sealed class ManifestHandler(string revision,string manifest) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(
                request.RequestUri!.AbsolutePath.Contains("revision")?revision:manifest)});
        }
    }
}
