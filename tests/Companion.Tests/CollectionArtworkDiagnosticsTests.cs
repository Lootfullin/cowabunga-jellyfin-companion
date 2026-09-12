using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Jellyfin.Plugin.CustomArtwork;
using Moq;

public sealed class CollectionArtworkDiagnosticsTests
{
    [Fact]
    public async Task ReportFindsCollectionByTmdbShowsLockedReasonAndDoesNotWriteImagesOrQueue()
    {
        using var context=new CompanionTestContext();context.Config.CollectionArtworkEnabled=true;
        var item=new BoxSet {Id=Guid.NewGuid(),Name="Злая",IsLocked=true};item.SetProviderId(MetadataProvider.Tmdb,"968080");
        context.Manager.Setup(m=>m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([item]);
        var index=new ArtworkIndex(NullLogger<ArtworkIndex>.Instance,Mock.Of<IHttpClientFactory>(),context.Manager.Object);
        var providers=new Mock<IProviderManager>(MockBehavior.Strict);
        using var coordinator=new ArtworkCoordinator(context.Manager.Object,providers.Object,Mock.Of<IFileSystem>(),index,NullLogger<ArtworkCoordinator>.Instance);
        var controller=new CollectionArtworkDiagnostics(context.Manager.Object,index,coordinator);
        var report=Assert.IsType<OkObjectResult>(await controller.InspectCollections("968080",default));
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(report.Value));
        var found=Assert.Single(json.RootElement.GetProperty("Collections").EnumerateArray());
        Assert.Equal(item.Id,found.GetProperty("Id").GetGuid());
        Assert.All(found.GetProperty("Images").EnumerateArray(),image=>Assert.Equal("Метаданные коллекции заблокированы",image.GetProperty("Status").GetString()));
        Assert.Equal(0,coordinator.PendingCount);
        Assert.Empty(providers.Invocations);
        context.Manager.Verify(m=>m.UpdateItemAsync(It.IsAny<BaseItem>(),It.IsAny<BaseItem>(),It.IsAny<ItemUpdateType>(),It.IsAny<CancellationToken>()),Times.Never);
        Assert.IsType<BadRequestObjectResult>(await controller.InspectCollections("",default));
    }
}
