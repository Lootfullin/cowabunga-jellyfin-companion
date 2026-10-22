using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace RussianMetadata;

// Jellyfin's automatic collection builder groups movies by a name, not by TMDB
// ID. Keep that grouping name aligned with an existing collection's chosen name.
public sealed class CollectionNameSynchronizer(ILibraryManager libraryManager) : IDisposable
{
    private readonly object _gate = new();
    private BoxSet[] _collections = [];
    private DateTime _expires;
    private bool _subscribed;
    private readonly Dictionary<string, HashSet<Guid>> _aliases = new(StringComparer.Ordinal);

    internal void Start()
    {
        if (_subscribed) return;
        libraryManager.ItemUpdated += OnItemChanged;
        libraryManager.ItemAdded += OnItemChanged;
        libraryManager.ItemRemoved += OnItemChanged;
        _subscribed = true;
    }
    private void OnItemChanged(object? sender, ItemChangeEventArgs args)
    {
        if (args.Item is BoxSet) Invalidate();
    }
    internal void Invalidate() { lock (_gate) _expires = DateTime.MinValue; }
    public void Dispose()
    {
        if (!_subscribed) return;
        libraryManager.ItemUpdated -= OnItemChanged;
        libraryManager.ItemAdded -= OnItemChanged;
        libraryManager.ItemRemoved -= OnItemChanged;
        _subscribed = false;
    }

    private BoxSet[] Collections()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow >= _expires)
            {
                _collections = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true
                }).OfType<BoxSet>().ToArray();
                _expires = DateTime.UtcNow.AddSeconds(10);
            }
            return _collections;
        }
    }

    private static bool NameLocked(BoxSet collection) => collection.IsLocked || collection.LockedFields.Contains(MetadataField.Name);
#pragma warning disable CS0618 // Older collection.xml files can still contain path-only links.
    private static bool Contains(BoxSet collection, Movie movie) => collection.LinkedChildren.Any(link =>
        link.ItemId == movie.Id && movie.Id != Guid.Empty
        || !string.IsNullOrEmpty(link.Path) && string.Equals(link.Path, movie.Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
#pragma warning restore CS0618
    private static string? CollectionId(Movie movie) => movie.GetProviderId(MetadataProvider.TmdbCollection);

    internal string? ResolveMovieName(Movie movie, BoxSet? updatedCollection = null)
    {
        if (!LibraryPolicy.AllowsCollection(CompanionModule.Metadata)) return null;
        var collections = updatedCollection is null ? Collections()
            : Collections().Where(c => c.Id != updatedCollection.Id).Append(updatedCollection).ToArray();
        // An explicitly locked manual collection may span several TMDB collections.
        var locked = collections.Where(c => NameLocked(c) && Contains(c, movie)).ToArray();
        if (locked.Length > 0) return locked.Length == 1 ? RememberAlias(movie, locked[0]) : null;
        var id = CollectionId(movie);
        if (!string.IsNullOrEmpty(id))
        {
            var matching = collections.Where(c => c.GetProviderId(MetadataProvider.Tmdb) == id).ToArray();
            if (matching.Length == 1) return RememberAlias(movie, matching[0]);
            var linked = matching.Where(c => Contains(c, movie)).ToArray();
            if (linked.Length == 1) return RememberAlias(movie, linked[0]);
        }
        return null;
    }

    private string RememberAlias(Movie movie, BoxSet collection)
    {
        if (!string.IsNullOrWhiteSpace(movie.TmdbCollectionName))
            lock (_gate)
            {
                if (!_aliases.TryGetValue(movie.TmdbCollectionName, out var targets))
                    _aliases[movie.TmdbCollectionName] = targets = [];
                targets.Add(collection.Id);
            }
        return collection.Name;
    }

    internal BoxSet? FindExistingFolder(string path) => !LibraryPolicy.AllowsCollection(CompanionModule.Metadata) ? null :
        Collections().FirstOrDefault(c => string.Equals(c.Path, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    internal async Task<int> ReconcileAsync(CancellationToken token)
    {
        if (!LibraryPolicy.AllowsCollection(CompanionModule.Metadata)) return 0;
        Invalidate();
        var movies = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie], Recursive = true
        }).OfType<Movie>();
        var count = 0;
        foreach (var movie in movies)
        {
            token.ThrowIfCancellationRequested();
            if (movie.IsLocked || !LibraryPolicy.Allows(CompanionModule.Metadata, movie)) continue;
            var name = ResolveMovieName(movie);
            if (string.IsNullOrWhiteSpace(name) || movie.TmdbCollectionName == name) continue;
            movie.TmdbCollectionName = name;
            await libraryManager.UpdateItemAsync(movie, movie.GetParent(), ItemUpdateType.MetadataEdit, token).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    internal async Task SynchronizeMembersAsync(BoxSet collection, CancellationToken token)
    {
        if (!LibraryPolicy.AllowsCollection(CompanionModule.Metadata)) return;
        var ids = collection.LinkedChildren.Where(c => c.ItemId.HasValue).Select(c => c.ItemId!.Value).Distinct().ToArray();
        if (ids.Length == 0) return;
        Invalidate();
        foreach (var movie in libraryManager.GetItemList(new InternalItemsQuery { ItemIds = ids }).OfType<Movie>())
        {
            token.ThrowIfCancellationRequested();
            if (movie.IsLocked || !LibraryPolicy.Allows(CompanionModule.Metadata, movie)) continue;
            var name = ResolveMovieName(movie, collection);
            if (string.IsNullOrWhiteSpace(name) || movie.TmdbCollectionName == name) continue;
            movie.TmdbCollectionName = name;
            await libraryManager.UpdateItemAsync(movie, movie.GetParent(), ItemUpdateType.MetadataEdit, token).ConfigureAwait(false);
        }
    }

    // Called just before native automatic creation, while its English grouping
    // name is still in flight. Returning the existing item prevents recreation of
    // its folder and consequent loss of its ID/locks, even on the first scan.
    internal BoxSet? FindExistingAutomaticCollection(string name)
    {
        if (!LibraryPolicy.AllowsCollection(CompanionModule.Metadata)) return null;
        var movies = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie], Recursive = true
        }).OfType<Movie>().Where(m => m.CollectionName == name && !m.PrimaryVersionId.HasValue).ToArray();
        var collections = Collections();
        if (movies.Length < 2)
        {
            lock (_gate)
                return _aliases.TryGetValue(name, out var targets) && targets.Count == 1
                    ? collections.FirstOrDefault(c => targets.Contains(c.Id)) : null;
        }
        if (movies.Any(m => !LibraryPolicy.Allows(CompanionModule.Metadata, m))) return null;
        var linked = collections.Where(c => movies.All(m => Contains(c, m))).ToArray();
        if (linked.Length == 1) return linked[0];
        var ids = movies.Select(CollectionId).Distinct().ToArray();
        if (ids.Length != 1 || string.IsNullOrWhiteSpace(ids[0])) return null;
        var matching = collections.Where(c => c.GetProviderId(MetadataProvider.Tmdb) == ids[0]).ToArray();
        return matching.Length == 1 ? matching[0] : null;
    }
}
