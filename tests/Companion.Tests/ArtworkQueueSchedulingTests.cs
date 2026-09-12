using Jellyfin.Plugin.CustomArtwork;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
using Jellyfin.Plugin.Companion.Configuration;

public sealed class ArtworkQueueSchedulingTests
{
    [Fact]
    public async Task RecheckingCompletedItemsCannotOvertakeItemsStillWaiting()
    {
        using var context = new CompanionTestContext();
        // Locked test items finish without reading files or accessing remote providers.
        var items = Enumerable.Range(0,100).Select(i => new Movie { Id=Guid.NewGuid(),
            Name="Queue item "+i, Path=Path.Combine(context.Root,i+".mkv"), IsLocked=true }).ToArray();
        foreach(var item in items) context.Manager.Setup(m=>m.GetItemById(item.Id)).Returns(item);
        using var worker = CreateWorker(context);
        void QueueAll() => worker.QueueMany(items.Select(item=>(item.Id,(IEnumerable<ImageType>)new[]{ImageType.Primary})),CompanionModule.Artwork);
        QueueAll();
        await worker.ProcessBatchAsync(default);
        Assert.NotNull(worker.Inspect(items[^1].Id,ImageType.Primary).Pending);
        QueueAll(); // The periodic index task submits all matches again.
        await worker.ProcessBatchAsync(default);
        Assert.Null(worker.Inspect(items[^1].Id,ImageType.Primary).Pending);
    }

    private static ArtworkCoordinator CreateWorker(CompanionTestContext context) => new(context.Manager.Object,
        Mock.Of<IProviderManager>(),Mock.Of<IFileSystem>(),
        new ArtworkIndex(NullLogger<ArtworkIndex>.Instance,Mock.Of<IHttpClientFactory>(),context.Manager.Object),
        NullLogger<ArtworkCoordinator>.Instance);

    [Fact]
    public async Task LegacyBacklogOf3306JobsDelegatesMediaFilesAndReachesBothCollectionImagesInFirstBatch()
    {
        using var context = new CompanionTestContext();
        context.Config.StorageMode = PluginConfiguration.MediaFolderStorage;
        context.Config.CollectionArtworkEnabled = true;
        var movie = new Movie {Id=Guid.NewGuid(),Path=Path.Combine(context.Root,"movie.mkv")};
        var collection = new BoxSet {Id=Guid.NewGuid(),Name="Waiting collection",IsLocked=true};
        context.Manager.Setup(m=>m.GetItemById(It.IsAny<Guid>())).Returns((Guid id)=>id==collection.Id?collection:movie);
        var state = new ArtworkQueueState();
        for(var i=0;i<3304;i++)
        {
            var id=Guid.NewGuid();state.Pending.Add(id.ToString("N")+"/Primary/Artwork",new() {ItemId=id,Type=ImageType.Primary,Module=CompanionModule.Artwork});
        }
        foreach(var type in new[]{ImageType.Primary,ImageType.Logo})
            state.Pending.Add(collection.Id.ToString("N")+"/"+type+"/Artwork",new(){ItemId=collection.Id,Type=type,Module=CompanionModule.Artwork});
        Directory.CreateDirectory(context.Plugin.DataFolderPath);
        File.WriteAllText(Path.Combine(context.Plugin.DataFolderPath,"companion-artwork.v1.json"),JsonSerializer.Serialize(state));
        using var worker=CreateWorker(context);
        Assert.Equal(3306,worker.PendingCount);
        await worker.ProcessBatchAsync(default);
        Assert.Equal(0,worker.PendingCount);
        Assert.Equal(2,worker.WorkerStatus.ProcessedSinceStart);
        Assert.False(worker.Queue(movie.Id,[ImageType.Primary],CompanionModule.Artwork));
        Assert.True(worker.Queue(collection.Id,[ImageType.Primary],CompanionModule.Artwork));
        Assert.True(worker.Queue(movie.Id,[ImageType.Primary],CompanionModule.Metadata));
    }

    [Fact]
    public async Task DelegatedFileRequestDoesNotReadImageOrContactCloud()
    {
        using var context=new CompanionTestContext();context.Config.StorageMode=PluginConfiguration.MediaFolderStorage;
        context.Config.ReplaceExistingImages=true;
        var movie=new Movie {Id=Guid.NewGuid(),Path=Path.Combine(context.Root,"movie.mkv")};
        context.Manager.Setup(m=>m.GetItemById(movie.Id)).Returns(movie);
        var factory=new Mock<IHttpClientFactory>(MockBehavior.Strict);
        using var worker=new ArtworkCoordinator(context.Manager.Object,Mock.Of<IProviderManager>(),Mock.Of<IFileSystem>(),
            new ArtworkIndex(NullLogger<ArtworkIndex>.Instance,factory.Object,context.Manager.Object),NullLogger<ArtworkCoordinator>.Instance);
        Assert.True(await worker.ApplyAsync(new(){ItemId=movie.Id,Type=ImageType.Primary,Module=CompanionModule.Artwork},default));
        Assert.Empty(factory.Invocations);
    }

    [Fact]
    public async Task OrderAndRetryDelaySurviveRestartAndDuplicateEnqueue()
    {
        using var context=new CompanionTestContext();
        var first=new Movie {Id=Guid.NewGuid(),Path=Path.Combine(context.Root,"first.mkv"),IsLocked=true};
        var second=new Movie {Id=Guid.NewGuid(),Path=Path.Combine(context.Root,"second.mkv"),IsLocked=true};
        context.Manager.Setup(m=>m.GetItemById(first.Id)).Returns(first);context.Manager.Setup(m=>m.GetItemById(second.Id)).Returns(second);
        var later=DateTime.UtcNow.AddHours(2);
        var state=new ArtworkQueueState();
        state.Pending.Add(first.Id.ToString("N")+"/Primary/Artwork",new(){ItemId=first.Id,Type=ImageType.Primary,Module=CompanionModule.Artwork,Attempts=3,NextAttemptUtc=later});
        state.Pending.Add(second.Id.ToString("N")+"/Primary/Artwork",new(){ItemId=second.Id,Type=ImageType.Primary,Module=CompanionModule.Artwork});
        Directory.CreateDirectory(context.Plugin.DataFolderPath);File.WriteAllText(Path.Combine(context.Plugin.DataFolderPath,"companion-artwork.v1.json"),JsonSerializer.Serialize(state));
        long firstOrder;
        using(var worker=CreateWorker(context))
        {
            worker.Queue(first.Id,[ImageType.Primary],CompanionModule.Artwork);
            firstOrder=worker.Inspect(first.Id,ImageType.Primary).Pending!.QueueOrder;
            Assert.True(firstOrder>0);
        }
        using var resumed=CreateWorker(context);
        Assert.Equal(firstOrder,resumed.Inspect(first.Id,ImageType.Primary).Pending!.QueueOrder);
        await resumed.ProcessBatchAsync(default);
        Assert.Null(resumed.Inspect(second.Id,ImageType.Primary).Pending);
        var retry=resumed.Inspect(first.Id,ImageType.Primary).Pending!;
        Assert.Equal(3,retry.Attempts);Assert.Equal(later,retry.NextAttemptUtc);
    }
}
