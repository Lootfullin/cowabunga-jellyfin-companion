using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using RussianMetadata;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Controller.Providers;
using System.Text.Json;

public sealed class CollectionNameSynchronizationTests : IDisposable
{
    private readonly CompanionTestContext _context = new();
    private readonly List<BaseItem> _items = [];
    private readonly CollectionNameSynchronizer _names;
    private readonly Movie _first;
    private readonly Movie _second;
    private readonly BoxSet _collection;

    public CollectionNameSynchronizationTests()
    {
        _context.Config.CollectionMetadataEnabled = true;
        _first = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(_context.Root, "one.mkv"), TmdbCollectionName = "Crank Collection" };
        _second = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(_context.Root, "two.mkv"), TmdbCollectionName = "Crank Collection" };
        _first.SetProviderId(MetadataProvider.TmdbCollection, "123"); _second.SetProviderId(MetadataProvider.TmdbCollection, "123");
        _collection = new BoxSet { Id = Guid.NewGuid(), Name = "Адреналин (Коллекция)",
            Path = Path.Combine(_context.Plugin.CollectionsPath, "Crank Collection [boxset]"),
            LinkedChildren = [LinkedChild.Create(_first), LinkedChild.Create(_second)] };
        _collection.SetProviderId(MetadataProvider.Tmdb, "123");
        _items.AddRange([_first, _second, _collection]);
        _context.Manager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => _items
            .Where(i => q.ItemIds.Length > 0 ? q.ItemIds.Contains(i.Id)
                : q.IncludeItemTypes.Contains(i is BoxSet ? BaseItemKind.BoxSet : BaseItemKind.Movie)).ToArray());
        _names = new CollectionNameSynchronizer(_context.Manager.Object);
        CompanionPlugin.CollectionNames = _names;
    }
    public void Dispose() { _names.Dispose(); _context.Dispose(); }

    [Fact]
    public async Task ReconcileAlignsExistingMoviesAndDoesNotWriteAgainWhenNamesMatch()
    {
        Assert.Equal(2, await _names.ReconcileAsync(default));
        Assert.Equal(_collection.Name, _first.TmdbCollectionName); Assert.Equal(_collection.Name, _second.TmdbCollectionName);
        Assert.Equal(0, await _names.ReconcileAsync(default));
        _context.Manager.Verify(m => m.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void PostMergeMovieCollectionNameUsesTheExistingCollectionInsteadOfAnotherTranslation()
    {
        Assert.True(RussianMovieProvider.ApplyTmdbCollection(_first, new TmdbCollectionReference { Id = 123, Name = "Другое название TMDB" }));
        Assert.Equal(_collection.Name, _first.TmdbCollectionName);
        Assert.False(RussianMovieProvider.ApplyTmdbCollection(_first, new TmdbCollectionReference { Id = 123, Name = "Crank Collection" }));
    }

    [Fact]
    public async Task LockedMixedCollectionKeepsItsManualNameAndDoesNotChangeMembershipOrIds()
    {
        _collection.Name = "Мой Аладдин"; _collection.LockedFields = [MetadataField.Name]; _collection.ProviderIds.Clear();
        _second.SetProviderId(MetadataProvider.TmdbCollection, "456");
        Assert.Equal(2, await _names.ReconcileAsync(default));
        Assert.Equal("Мой Аладдин", _first.TmdbCollectionName); Assert.Equal("Мой Аладдин", _second.TmdbCollectionName);
        Assert.Empty(_collection.ProviderIds); Assert.Equal(2, _collection.LinkedChildren.Length);
        Assert.Equal("456", _second.GetProviderId(MetadataProvider.TmdbCollection));
    }

    [Fact]
    public async Task UpdatedCollectionInstanceAlignsMembersBeforeTheRenameIsPersisted()
    {
        var updated = new BoxSet { Id = _collection.Id, Name = "Новое имя", LinkedChildren = _collection.LinkedChildren };
        updated.SetProviderId(MetadataProvider.Tmdb, "123");
        await _names.SynchronizeMembersAsync(updated, default);
        Assert.Equal("Новое имя", _first.CollectionName); Assert.Equal("Новое имя", _second.CollectionName);
        // Native post-scan can still have its earlier English grouping in flight.
        Assert.Same(_collection, _names.FindExistingAutomaticCollection("Crank Collection"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalizerRespectsFieldAndWholeItemLocks(bool wholeItem)
    {
        _collection.Name = "Ручное имя"; _collection.Overview = "Ручное описание";
        _collection.IsLocked = wholeItem; _collection.LockedFields = [MetadataField.Name, MetadataField.Overview];
        var changed = ChooseYourMetaBoxSetProvider.ApplyLocalizedMetadata(_collection,
            new TmdbCollectionDetails { Id = 123, Name = "Название TMDB", Overview = "Описание TMDB" }, _context.Config);
        Assert.False(changed); Assert.Equal("Ручное имя", _collection.Name); Assert.Equal("Ручное описание", _collection.Overview);
        Assert.Contains(MetadataField.Name, _collection.LockedFields); Assert.Equal("123", _collection.GetProviderId(MetadataProvider.Tmdb));
    }

    [Fact]
    public async Task AutomaticCreationReusesRenamedFolderWithoutClearingItsLocksAndProviderIds()
    {
        _collection.LockedFields = [MetadataField.Name];
        _context.Manager.SetupGet(m => m.IsScanRunning).Returns(true);
        var inner = new Mock<ICollectionManager>(MockBehavior.Strict);
        var files = new Mock<IFileSystem>(); files.Setup(f => f.GetValidFilename("Crank Collection")).Returns("Crank Collection");
        using var guard = new CollectionCreationGuard(inner.Object, _context.Manager.Object, _names, files.Object);
        var result = await guard.CreateCollectionAsync(new CollectionCreationOptions { Name = "Crank Collection" });
        Assert.Same(_collection, result); Assert.Equal("Адреналин (Коллекция)", result.Name);
        Assert.Equal("123", result.GetProviderId(MetadataProvider.Tmdb)); Assert.Contains(MetadataField.Name, result.LockedFields);
        Assert.Empty(inner.Invocations);
    }

    [Fact]
    public void AutomaticCreationCanFindACollectionWhoseFolderAlsoHasADifferentName()
    {
        _collection.Path = Path.Combine(_context.Plugin.CollectionsPath, "Custom folder [boxset]");
        Assert.Same(_collection, _names.FindExistingAutomaticCollection("Crank Collection"));
    }

    [Fact]
    public async Task ExplicitCreationOutsideScanIsDelegatedUnchanged()
    {
        var options = new CollectionCreationOptions { Name = "Crank Collection" };
        var inner = new Mock<ICollectionManager>(); inner.Setup(c => c.CreateCollectionAsync(options)).ReturnsAsync(new BoxSet());
        using var guard = new CollectionCreationGuard(inner.Object, _context.Manager.Object, _names, Mock.Of<IFileSystem>());
        await guard.CreateCollectionAsync(options);
        inner.Verify(c => c.CreateCollectionAsync(options), Times.Once);
    }

    [Theory]
    [InlineData("members")]
    [InlineData("providers")]
    [InlineData("locked")]
    [InlineData("parent")]
    [InlineData("users")]
    public async Task ExplicitCreationOptionsDuringScanAreDelegatedUnchanged(string kind)
    {
        _context.Manager.SetupGet(m => m.IsScanRunning).Returns(true);
        var options = new CollectionCreationOptions { Name = "Crank Collection" };
        if (kind == "members") options.ItemIdList = [_first.Id.ToString()];
        if (kind == "providers") options.ProviderIds["Tmdb"] = "123";
        if (kind == "locked") options.IsLocked = true;
        if (kind == "parent") options.ParentId = Guid.NewGuid();
        if (kind == "users") options.UserIds = [Guid.NewGuid()];
        var inner = new Mock<ICollectionManager>(); inner.Setup(c => c.CreateCollectionAsync(options)).ReturnsAsync(new BoxSet());
        using var guard = new CollectionCreationGuard(inner.Object, _context.Manager.Object, _names, Mock.Of<IFileSystem>());
        await guard.CreateCollectionAsync(options);
        inner.Verify(c => c.CreateCollectionAsync(options), Times.Once);
    }

    [Fact]
    public async Task ExcludedAndLockedMoviesRemainUnchanged()
    {
        _first.IsLocked = true; _context.Config.AllLibraries = false;
        Assert.Equal(0, await _names.ReconcileAsync(default));
        _context.Config.AllLibraries = true;
        Assert.Equal(1, await _names.ReconcileAsync(default));
        Assert.Equal("Crank Collection", _first.CollectionName);
        Assert.Equal(_collection.Name, _second.CollectionName);
    }

    [Fact]
    public async Task DisabledCollectionMetadataDoesNotAlignMoviesOrInterceptCreation()
    {
        _context.Config.CollectionMetadataEnabled = false;
        Assert.Equal(0, await _names.ReconcileAsync(default));
        Assert.Null(_names.FindExistingAutomaticCollection("Crank Collection"));
        Assert.Null(_names.FindExistingFolder(_collection.Path));
    }

    [Fact]
    public async Task ReconciliationDoesNotKeepRefreshingAnAuditedLockedEnglishName()
    {
        _collection.Name = "My chosen collection"; _collection.LockedFields = [MetadataField.Name];
        Directory.CreateDirectory(_context.Plugin.DataFolderPath);
        File.WriteAllText(Path.Combine(_context.Plugin.DataFolderPath, "collection-reconciliation.v1.json"),
            JsonSerializer.Serialize(new CollectionReconciliationState { Entries = new()
            {
                [_collection.Id] = new() { IdentityAuditQueued = true, NextAttemptUtc = DateTime.UtcNow.AddDays(-1) }
            } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var providers = new Mock<IProviderManager>(MockBehavior.Strict);
        var task = new ReconcileCollectionMetadataTask(_context.Manager.Object, providers.Object, Mock.Of<IFileSystem>(),
            new LibraryConfigurationService(_context.Manager.Object, NullLogger<LibraryConfigurationService>.Instance),
            NullLogger<ReconcileCollectionMetadataTask>.Instance);
        await task.ExecuteAsync(new Progress<double>(), default);
        Assert.Empty(providers.Invocations);
        Assert.Equal("My chosen collection", _first.CollectionName);
    }
}
