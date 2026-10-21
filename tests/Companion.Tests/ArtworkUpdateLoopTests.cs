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

public sealed class ArtworkUpdateLoopTests : IDisposable
{
    private readonly CompanionTestContext _context = new();
    private readonly List<BaseItem> _items = [];
    private readonly Dictionary<Guid, BaseItem> _persisted = [];
    private readonly Mock<IProviderManager> _providers = new();
    private readonly Dictionary<(int Id, ImageType Type), byte[]> _cloud = [];
    private readonly HashSet<int> _collections = [];
    private readonly HttpClient _client;
    private readonly Mock<IHttpClientFactory> _http = new();
    private readonly ArtworkIndex _index;
    private readonly ArtworkCoordinator _worker;
    private readonly RefreshIndexTask _task;
    private int _revision;
    private int _downloads;
    private int _updates;

    public ArtworkUpdateLoopTests()
    {
        _context.Config.CollectionArtworkEnabled = true;
        _context.Config.ReplaceExistingImages = true;
        _context.Manager.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.FirstOrDefault(i => i.Id == id));
        _context.Manager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) =>
            q.ItemIds.Length > 0 ? q.ItemIds.Where(_persisted.ContainsKey).Select(id => _persisted[id]).ToArray() : _items.ToArray());
        _context.Manager.Setup(m => m.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns((BaseItem item, BaseItem parent, ItemUpdateType reason, CancellationToken token) =>
            {
                _updates++;
                Persist(item);
                return Task.CompletedTask;
            });
        _providers.Setup(p => p.SaveImage(It.IsAny<BaseItem>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ImageType>(), 0, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Returns((BaseItem item, string source, string mime, ImageType type, int? number, bool? local, CancellationToken token) =>
            {
                var target = Path.Combine(item is BoxSet ? item.Path : _context.Root, item.Id + "-" + type + ".jpg");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
                item.ImageInfos = item.ImageInfos.Where(i => i.Type != type).Append(new ItemImageInfo { Type = type, Path = target }).ToArray();
                return Task.CompletedTask;
            });
        _client = new HttpClient(new Handler(Respond));
        _http.Setup(h => h.CreateClient(It.IsAny<string>())).Returns(_client);
        _index = new ArtworkIndex(NullLogger<ArtworkIndex>.Instance, _http.Object, _context.Manager.Object);
        _worker = CreateWorker();
        CompanionPlugin.Images = _worker;
        _task = CreateTask();
    }

    public void Dispose() { _worker.Dispose(); _client.Dispose(); _context.Dispose(); }
    private ArtworkCoordinator CreateWorker() => new(_context.Manager.Object, _providers.Object, Mock.Of<IFileSystem>(), _index, NullLogger<ArtworkCoordinator>.Instance);
    private RefreshIndexTask CreateTask() => new(_index, new ArtworkMediaWriter(_index, _context.Manager.Object, NullLogger<ArtworkMediaWriter>.Instance),
        _providers.Object, Mock.Of<IFileSystem>(), new ArtworkLibraryConfigurator(_context.Manager.Object, NullLogger<ArtworkLibraryConfigurator>.Instance), NullLogger<RefreshIndexTask>.Instance);
    private Task Check() => _task.ExecuteAsync(new Progress<double>(), default);
    private void Persist(BaseItem item) => _persisted[item.Id] = new Movie { Id = item.Id,
        ImageInfos = item.ImageInfos.Select(i => new ItemImageInfo { Type = i.Type, Path = i.Path }).ToArray() };
    private BaseItem AddItem(int tmdb, bool collection = false)
    {
        BaseItem item = collection ? new BoxSet { Path = Path.Combine(_context.Plugin.CollectionsPath, tmdb.ToString()) } : new Movie { Path = Path.Combine(_context.Root, "media", tmdb + ".mkv") };
        item.Id = Guid.NewGuid(); item.Name = "Fixture " + tmdb;
        if (collection) _collections.Add(tmdb);
        item.SetProviderId(MetadataProvider.Tmdb, tmdb.ToString()); _items.Add(item); Persist(item);
        return item;
    }
    private void SetCloud(int tmdb, ImageType type, byte[] bytes) { _cloud[(tmdb, type)] = bytes; _revision++; }
    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var revision = Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(_revision)));
        var path = request.RequestUri!.AbsolutePath;
        HttpContent content;
        if (path.EndsWith("revision.json")) content = new StringContent(JsonSerializer.Serialize(new { revision }));
        else if (path.EndsWith("v1.json")) content = new StringContent(JsonSerializer.Serialize(new { schema_version = 2, revision,
            files = _cloud.Select(pair => new { path = "Collections/" + pair.Key.Id + "/" + (pair.Key.Type == ImageType.Primary ? "poster.jpg" : "clearlogo.png"),
                sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)), size = pair.Value.Length, tmdb_id = pair.Key.Id,
                release_names = new[] { "Fixture " + pair.Key.Id }, scope = _collections.Contains(pair.Key.Id) ? "collection" : "item" }) }));
        else
        {
            _downloads++;
            var parts = path.Split('/');
            var tmdb = int.Parse(parts[^2]); var type = parts[^1].StartsWith("poster") ? ImageType.Primary : ImageType.Logo;
            content = new ByteArrayContent(_cloud[(tmdb, type)]);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedIndexAndLegacyBacklogDoNotWriteIdenticalImages(bool collection)
    {
        var item = AddItem(123, collection);
        SetCloud(123, ImageType.Primary, [1, 2, 3]); SetCloud(123, ImageType.Logo, [4, 5, 6]);
        await Check(); Assert.Equal(2, _worker.PendingCount);
        await _worker.ProcessBatchAsync(default); Assert.Equal(0, _worker.PendingCount); Assert.Equal(2, _updates);
        for (var i = 0; i < 3; i++) { await Check(); Assert.Equal(0, _worker.PendingCount); }
        // Existing 1.0.14 jobs must also drain quietly, even after a worker restart.
        using var restarted = CreateWorker(); CompanionPlugin.Images = restarted;
        await Check(); Assert.Equal(0, restarted.PendingCount);
        restarted.Queue(item.Id, [ImageType.Primary, ImageType.Logo], CompanionModule.Artwork);
        await restarted.ProcessBatchAsync(default);
        Assert.Equal(0, restarted.PendingCount); Assert.Equal(2, _updates); Assert.Equal(2, _downloads);
    }

    [Fact]
    public async Task NewCloudArtworkAndNewCollectionAreDetectedWithAnOtherwiseUnchangedIndex()
    {
        AddItem(123); SetCloud(123, ImageType.Primary, [1]);
        await Check(); await _worker.ProcessBatchAsync(default);
        SetCloud(123, ImageType.Primary, [2]);
        await Check(); Assert.Equal(1, _worker.PendingCount); await _worker.ProcessBatchAsync(default);
        SetCloud(456, ImageType.Primary, [3]); SetCloud(456, ImageType.Logo, [4]);
        var collection = AddItem(456, true);
        await Check(); await _worker.ProcessBatchAsync(default);
        Assert.Equal(4, _updates); Assert.Equal(4, _downloads);
        Assert.NotNull(collection.GetImageInfo(ImageType.Primary, 0)); Assert.NotNull(collection.GetImageInfo(ImageType.Logo, 0));
        await Check(); Assert.Equal(0, _worker.PendingCount);
    }

    [Fact]
    public async Task CollectionCreatedAfterAnAcknowledgedManifestIsAutomaticallyQueued()
    {
        // Publish both roles before creating the collection; the manifest revision
        // stays fixed when Jellyfin later creates its new collection item.
        var prototype = AddItem(456, true);
        SetCloud(456, ImageType.Primary, [3]); SetCloud(456, ImageType.Logo, [4]);
        _items.Remove(prototype);
        await Check(); Assert.Equal(0, _worker.PendingCount);
        var collection = AddItem(456, true);
        await Check(); Assert.Equal(2, _worker.PendingCount);
        await _worker.ProcessBatchAsync(default); Assert.Equal(2, _updates);
        Assert.NotNull(collection.GetImageInfo(ImageType.Primary, 0));
    }

    [Fact]
    public async Task EnablingArtworkRolesAndScopesAppliesPreviouslySkippedMatches()
    {
        AddItem(123); SetCloud(123, ImageType.Primary, [1]); SetCloud(123, ImageType.Logo, [2]);
        _context.Config.AllLibraries = false;
        await Check(); Assert.Equal(0, _worker.PendingCount);
        _context.Config.AllLibraries = true; _context.Config.Logos = false;
        await Check(); await _worker.ProcessBatchAsync(default); Assert.Equal(1, _updates);
        _context.Config.Logos = true;
        await Check(); await _worker.ProcessBatchAsync(default); Assert.Equal(2, _updates);
        // Disabling can consume pending jobs; re-enabling the same saved settings
        // must still recheck after UpdateConfiguration invalidates the baseline.
        _context.Config.ArtworkEnabled = false; _worker.InvalidateArtworkConfiguration();
        _context.Config.ArtworkEnabled = true;
        await Check(); await _worker.ProcessBatchAsync(default); Assert.Equal(2, _updates);
        await Check(); Assert.Equal(0, _worker.PendingCount);
    }

    [Fact]
    public async Task SameHashWithAnUnsavedReferenceIsRepairedOnceEvenWithLegacyOwnership()
    {
        var item = AddItem(123); SetCloud(123, ImageType.Primary, [1, 2, 3]);
        await Check(); await _worker.ProcessBatchAsync(default);
        _persisted[item.Id].ImageInfos = [];
        _worker.Queue(item.Id, [ImageType.Primary], CompanionModule.Artwork);
        await _worker.ProcessBatchAsync(default); Assert.Equal(2, _updates);
        _worker.Queue(item.Id, [ImageType.Primary], CompanionModule.Artwork);
        await _worker.ProcessBatchAsync(default); Assert.Equal(2, _updates); Assert.Equal(1, _downloads);
    }

    [Fact]
    public async Task ExistingIdenticalImageWithNoOwnershipDoesNotGenerateAnUpdate()
    {
        var item = AddItem(123); SetCloud(123, ImageType.Primary, [1, 2, 3]);
        var path = Path.Combine(_context.Root, "existing.jpg"); File.WriteAllBytes(path, [1, 2, 3]);
        item.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = path }]; Persist(item);
        await Check(); await _worker.ProcessBatchAsync(default);
        Assert.Equal(0, _updates); Assert.Equal(0, _downloads); Assert.Equal(0, _worker.PendingCount);
    }

    [Fact]
    public async Task DailyRecoveryRestoresAnOverwrittenImageButDoesNotNotifyForEqualImages()
    {
        var item = AddItem(123); SetCloud(123, ImageType.Primary, [1, 2, 3]);
        await Check(); await _worker.ProcessBatchAsync(default);
        void ExpireRecovery()
        {
            var field = typeof(ArtworkCoordinator).GetField("_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            ((ArtworkQueueState)field.GetValue(_worker)!).LastFullArtworkCheckUtc = DateTime.UtcNow.AddDays(-2);
        }
        ExpireRecovery(); await Check(); await _worker.ProcessBatchAsync(default); Assert.Equal(1, _updates);
        File.WriteAllBytes(item.GetImageInfo(ImageType.Primary, 0)!.Path, [9]);
        ExpireRecovery(); await Check(); await _worker.ProcessBatchAsync(default); Assert.Equal(2, _updates);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(item.GetImageInfo(ImageType.Primary, 0)!.Path));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
