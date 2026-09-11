using System.Net;
using Jellyfin.Plugin.CustomArtwork;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Controller.Library;

public sealed class ArtworkCoordinatorTests : IDisposable
{
    private readonly CompanionTestContext _context = new();
    private readonly Mock<IProviderManager> _providers = new();
    private readonly Mock<IFileSystem> _files = new();
    private readonly Mock<IRemoteImageProvider> _remote = new();
    private readonly ArtworkCoordinator _coordinator;
    private readonly Movie _movie;
    public ArtworkCoordinatorTests()
    {
        _context.Config.ArtworkEnabled=false;
        _movie=new Movie { Id=Guid.NewGuid(),Path=Path.Combine(_context.Root,"media","movie.mkv"),Name="Test" };
        _context.Manager.Setup(value=>value.GetItemById(_movie.Id)).Returns(_movie);
        var http=new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var index=new ArtworkIndex(NullLogger<ArtworkIndex>.Instance,http.Object,_context.Manager.Object);
        _coordinator=new ArtworkCoordinator(_context.Manager.Object,_providers.Object,_files.Object,index,NullLogger<ArtworkCoordinator>.Instance);
        _remote.SetupGet(value=>value.Name).Returns(CompanionPlugin.MetadataImageProviderName);
        _remote.Setup(value=>value.Supports(_movie)).Returns(true);
        _remote.Setup(value=>value.GetImages(_movie,It.IsAny<CancellationToken>())).ReturnsAsync(new[] {new RemoteImageInfo { Type=ImageType.Primary,Url="https://example.test/poster.jpg" }});
        _remote.Setup(value=>value.GetImageResponse(It.IsAny<string>(),It.IsAny<CancellationToken>()))
            .ReturnsAsync(()=>new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent([1,2,3,4])});
        _providers.Setup(value=>value.GetImageProviders(_movie,It.IsAny<ImageRefreshOptions>())).Returns([_remote.Object]);
        _providers.Setup(value=>value.SaveImage(_movie,It.IsAny<string>(),It.IsAny<string>(),ImageType.Primary,0,false,It.IsAny<CancellationToken>()))
            .Returns((BaseItem item,string path,string mime,ImageType type,int? number,bool? local,CancellationToken token)=>
            {
                var target=Path.Combine(_context.Root,"poster.jpg");File.Copy(path,target,true);
                item.ImageInfos=[new ItemImageInfo {Type=type,Path=target}];return Task.CompletedTask;
            });
    }
    public void Dispose() { _coordinator.Dispose();_context.Dispose(); }
    private PendingArtwork Request()=>new() { ItemId=_movie.Id,Type=ImageType.Primary,Module=CompanionModule.Metadata };

    [Fact]
    public async Task SavedImageReferenceIsPersistedBeforeReportingSuccess()
    {
        Assert.True(await _coordinator.ApplyAsync(Request(),default));
        _context.Manager.Verify(value => value.UpdateItemAsync(_movie, It.IsAny<BaseItem>(),
            ItemUpdateType.ImageUpdate, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailedImagePersistenceDoesNotReportSuccess()
    {
        _context.Manager.Setup(value => value.UpdateItemAsync(_movie, It.IsAny<BaseItem>(),
            ItemUpdateType.ImageUpdate, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Repository unavailable"));
        await Assert.ThrowsAsync<IOException>(() => _coordinator.ApplyAsync(Request(), default));
    }

    [Fact]
    public async Task DisabledOrExcludedQueuedWorkDoesNotCallProviders()
    {
        Assert.True(_coordinator.Queue(_movie.Id,[ImageType.Primary],CompanionModule.Metadata));
        _context.Config.MetadataEnabled=false;
        Assert.True(await _coordinator.ApplyAsync(Request(),default));
        _context.Config.MetadataEnabled=true;_context.Config.AllLibraries=false;
        Assert.True(await _coordinator.ApplyAsync(Request(),default));
        _providers.Verify(value=>value.GetImageProviders(It.IsAny<BaseItem>(),It.IsAny<ImageRefreshOptions>()),Times.Never);
        Assert.False(_coordinator.Queue(_movie.Id,[ImageType.Primary],CompanionModule.Metadata));
    }
    [Fact]
    public async Task ManualReplacementIsProtectedAfterCompanionSavedImage()
    {
        Assert.True(await _coordinator.ApplyAsync(Request(),default));
        var path=_movie.GetImageInfo(ImageType.Primary,0).Path;
        File.WriteAllBytes(path,[9,8,7]);
        _providers.Invocations.Clear();_remote.Invocations.Clear();
        Assert.True(await _coordinator.ApplyAsync(Request(),default));
        Assert.Equal(new byte[]{9,8,7},File.ReadAllBytes(path));
        _remote.Verify(value=>value.GetImages(It.IsAny<BaseItem>(),It.IsAny<CancellationToken>()),Times.Never);
    }
    [Fact]
    public async Task FailedDownloadKeepsExistingImageForRetry()
    {
        Assert.True(await _coordinator.ApplyAsync(Request(),default));
        _remote.Setup(value=>value.GetImageResponse(It.IsAny<string>(),It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(()=>_coordinator.ApplyAsync(Request(),default));
        Assert.Equal(new byte[]{1,2,3,4},File.ReadAllBytes(_movie.GetImageInfo(ImageType.Primary,0).Path));
    }
    [Fact]
    public async Task ScopeIsRecheckedAfterDownloadBeforeSaving()
    {
        _remote.Setup(value=>value.GetImageResponse(It.IsAny<string>(),It.IsAny<CancellationToken>()))
            .ReturnsAsync(()=> { _context.Config.MetadataEnabled=false; return new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent([1,2,3])}; });
        Assert.True(await _coordinator.ApplyAsync(Request(),default));
        _providers.Verify(value=>value.SaveImage(It.IsAny<BaseItem>(),It.IsAny<string>(),It.IsAny<string>(),It.IsAny<ImageType>(),It.IsAny<int?>(),It.IsAny<bool?>(),It.IsAny<CancellationToken>()),Times.Never);
    }
    [Fact]
    public void QueueDeduplicatesRolesAndSurvivesRestart()
    {
        _coordinator.Queue(_movie.Id,[ImageType.Primary,ImageType.Primary],CompanionModule.Metadata);
        _coordinator.Queue(_movie.Id,[ImageType.Primary],CompanionModule.Metadata);
        Assert.Equal(1,_coordinator.PendingCount);
        using var second=new ArtworkCoordinator(_context.Manager.Object,_providers.Object,_files.Object,
            new ArtworkIndex(NullLogger<ArtworkIndex>.Instance,Mock.Of<IHttpClientFactory>(),_context.Manager.Object),NullLogger<ArtworkCoordinator>.Instance);
        Assert.Equal(1,second.PendingCount);
    }
    [Theory]
    [InlineData(null,null,false,true)] [InlineData("manual",null,false,false)]
    [InlineData("same","same",false,true)] [InlineData("new","old",false,false)] [InlineData("manual",null,true,true)]
    public void OwnershipControlsReplacement(string? current,string? managed,bool overwrite,bool expected)=>Assert.Equal(expected,ArtworkCoordinator.MayReplace(current,managed,overwrite));
    [Fact]
    public void RetriesBackOffAndAreBounded()
    {
        Assert.Equal(TimeSpan.FromMinutes(1),ArtworkCoordinator.RetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(2),ArtworkCoordinator.RetryDelay(2));
        Assert.Equal(TimeSpan.FromHours(24),ArtworkCoordinator.RetryDelay(100));
    }
    [Fact]
    public async Task NativeProviderDownloadsBecomeManagedOnlyIfSavedBytesMatch()
    {
        var url="https://example.test/native.jpg";
        _coordinator.RegisterCandidates(_movie,[new RemoteImageInfo {Type=ImageType.Primary,Url=url}]);
        using var response=await _coordinator.TrackDownloadAsync(url,CompanionPlugin.MetadataImageProviderName,
            new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent([5,6,7])},default);
        var path=Path.Combine(_context.Root,"native.jpg");File.WriteAllBytes(path,[5,6,7]);
        _movie.ImageInfos=[new ItemImageInfo {Type=ImageType.Primary,Path=path}];
        Assert.True(await _coordinator.ApplyAsync(Request(),default));
        _remote.Verify(value=>value.GetImages(_movie,It.IsAny<CancellationToken>()),Times.Once);
    }
    [Fact]
    public async Task NativeCustomDownloadRejectsHashMismatch()
    {
        var url="https://example.test/corrupt.jpg";
        _coordinator.RegisterHash(url,new string('a',64));
        await Assert.ThrowsAsync<InvalidDataException>(()=>_coordinator.TrackDownloadAsync(url,CompanionPlugin.ArtworkProviderName,
            new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent([5,6,7])},default));
    }
    [Fact]
    public async Task ImportedArtworkOwnershipPreservesManualChanges()
    {
        var bytes=new byte[]{5,6,7};var hash=Convert.ToHexString(SHA256.HashData(bytes));
        var state=Path.Combine(_context.Root,"legacy.json");
        File.WriteAllText(state,JsonSerializer.Serialize(new ArtworkState {Entries=new() { ["item:"+_movie.Id]=new ArtworkStateEntry {ItemId=_movie.Id,Fingerprint=hash+"|"} }}));
        _coordinator.ImportLegacyOwnership(state);
        var path=Path.Combine(_context.Root,"manual.jpg");File.WriteAllBytes(path,[9,9,9]);
        _movie.ImageInfos=[new ItemImageInfo {Type=ImageType.Primary,Path=path}];
        Assert.True(await _coordinator.ApplyAsync(Request(),default));
        _remote.Verify(value=>value.GetImages(_movie,It.IsAny<CancellationToken>()),Times.Never);
        File.WriteAllBytes(path,bytes);
        Assert.True(await _coordinator.ApplyAsync(Request(),default));
        _remote.Verify(value=>value.GetImages(_movie,It.IsAny<CancellationToken>()),Times.Once);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CustomArtworkWinsAndCloudFailureDoesNotFallBack(bool failDownload)
    {
        _context.Config.ArtworkEnabled=true;
        _movie.SetProviderId(MetadataProvider.Tmdb,"123");
        _context.Manager.Setup(value=>value.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([_movie]);
        var hash=Convert.ToHexString(SHA256.HashData(new byte[]{7,7,7})).ToLowerInvariant();
        var revision=new string('a',64);
        var manifest=JsonSerializer.Serialize(new {schema_version=2,revision,files=new[] {new {path="Movies/Test/poster.jpg",sha256=hash,size=3,release_names=new[]{"movie"},scope="item",tmdb_id=123}}});
        using var client=new HttpClient(new StubHandler(request=>request.RequestUri!.AbsolutePath switch
        {
            "/artwork-index.revision.json"=>new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(JsonSerializer.Serialize(new {revision}))},
            "/artwork-index.v1.json"=>new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(manifest)},
            _=>new HttpResponseMessage(failDownload?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK) {Content=new ByteArrayContent([7,7,7])}
        }));
        var factory=new Mock<IHttpClientFactory>();factory.Setup(value=>value.CreateClient(It.IsAny<string>())).Returns(client);
        var index=new ArtworkIndex(NullLogger<ArtworkIndex>.Instance,factory.Object,_context.Manager.Object);
        using var coordinator=new ArtworkCoordinator(_context.Manager.Object,_providers.Object,_files.Object,index,NullLogger<ArtworkCoordinator>.Instance);
        if(failDownload) await Assert.ThrowsAsync<HttpRequestException>(()=>coordinator.ApplyAsync(Request(),default));
        else
        {
            Assert.True(await coordinator.ApplyAsync(Request(),default));
            Assert.Equal(new byte[]{7,7,7},File.ReadAllBytes(_movie.GetImageInfo(ImageType.Primary,0).Path));
        }
        _remote.Verify(value=>value.GetImages(It.IsAny<BaseItem>(),It.IsAny<CancellationToken>()),Times.Never);
    }
    private sealed class StubHandler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(respond(request));
    }
}
