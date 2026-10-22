using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;

namespace RussianMetadata;

// Preserve all native collection operations. Guard only name-only creations
// during the scan, which is how Jellyfin's CollectionPostScanTask creates sets.
internal sealed class CollectionCreationGuard(ICollectionManager inner, ILibraryManager libraryManager,
    CollectionNameSynchronizer names, IFileSystem fileSystem) : ICollectionManager, IDisposable
{
    public event EventHandler<CollectionCreatedEventArgs>? CollectionCreated { add => inner.CollectionCreated += value; remove => inner.CollectionCreated -= value; }
    public event EventHandler<CollectionModifiedEventArgs>? ItemsAddedToCollection { add => inner.ItemsAddedToCollection += value; remove => inner.ItemsAddedToCollection -= value; }
    public event EventHandler<CollectionModifiedEventArgs>? ItemsRemovedFromCollection { add => inner.ItemsRemovedFromCollection += value; remove => inner.ItemsRemovedFromCollection -= value; }
    public Task<BoxSet> CreateCollectionAsync(CollectionCreationOptions options)
    {
        if (libraryManager.IsScanRunning && !options.ParentId.HasValue && options.UserIds.Count == 0
            && options.ItemIdList.Count == 0 && options.ProviderIds.Count == 0
            && !options.IsLocked)
        {
            var path = Path.Combine(CompanionPlugin.Instance!.CollectionsPath, fileSystem.GetValidFilename(options.Name) + " [boxset]");
            var existing = names.FindExistingAutomaticCollection(options.Name) ?? names.FindExistingFolder(path);
            if (existing is not null) return Task.FromResult(existing);
        }
        return inner.CreateCollectionAsync(options);
    }
    public Task AddToCollectionAsync(Guid collectionId, IEnumerable<Guid> itemIds) => inner.AddToCollectionAsync(collectionId, itemIds);
    public Task RemoveFromCollectionAsync(Guid collectionId, IEnumerable<Guid> itemIds) => inner.RemoveFromCollectionAsync(collectionId, itemIds);
    public IEnumerable<BaseItem> CollapseItemsWithinBoxSets(IEnumerable<BaseItem> items, User user) => inner.CollapseItemsWithinBoxSets(items, user);
    public IEnumerable<BoxSet> GetCollectionsContainingItem(User user, Guid itemId) => inner.GetCollectionsContainingItem(user, itemId);
    public Task<Folder?> GetCollectionsFolder(bool createIfNeeded) => inner.GetCollectionsFolder(createIfNeeded);
    public void Dispose() { if (inner is IDisposable disposable) disposable.Dispose(); }
}
