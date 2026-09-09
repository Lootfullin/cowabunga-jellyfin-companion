using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.Companion;

public sealed class LibraryConfigurator(ILibraryManager libraryManager, LibraryPolicy policy,
    IProviderManager providerManager, IServerConfigurationManager serverConfiguration)
{
    private readonly object _gate = new();
    public string[] Apply()
    {
        lock (_gate)
        {
            policy.Invalidate();
            var updated = new List<string>();
            MetadataPluginSummary[]? summaries = null;
            TypeOptions Defaults(string type)
            {
                summaries ??= providerManager.GetAllMetadataPlugins();
                var global = serverConfiguration.GetMetadataOptionsForType(type);
                var plugins = summaries.FirstOrDefault(value => value.ItemType == type)?.Plugins ?? [];
                return new TypeOptions
                {
                    Type = type,
                    MetadataFetchers = plugins.Where(value => value.Type == MetadataPluginType.MetadataFetcher
                        && !(global?.DisabledMetadataFetchers ?? []).Contains(value.Name, StringComparer.OrdinalIgnoreCase))
                        .Select(value => value.Name).Distinct().ToArray(),
                    ImageFetchers = plugins.Where(value => value.Type == MetadataPluginType.ImageFetcher
                        && !(global?.DisabledImageFetchers ?? []).Contains(value.Name, StringComparer.OrdinalIgnoreCase))
                        .Select(value => value.Name).Distinct().ToArray(),
                    MetadataFetcherOrder = global?.MetadataFetcherOrder ?? [],
                    ImageFetcherOrder = global?.ImageFetcherOrder ?? []
                };
            }
            foreach (var folder in policy.GetLibraries())
            {
                if (!Guid.TryParse(folder.ItemId, out var id)
                    || libraryManager.GetItemById<CollectionFolder>(id) is not { } collection) continue;
                var options = collection.GetLibraryOptions();
                var metadata = policy.ForLibrary(CompanionModule.Metadata, id);
                var artwork = policy.ForLibrary(CompanionModule.Artwork, id);
                if (!ApplyOptions(options, folder.CollectionType, metadata, artwork, Defaults)) continue;
                collection.UpdateLibraryOptions(options);
                updated.Add(folder.Name);
            }
            return updated.ToArray();
        }
    }

    internal static bool ApplyOptions(LibraryOptions options, CollectionTypeOptions? collectionType, bool metadata, bool artwork,
        Func<string, TypeOptions>? defaults = null)
    {
        string[] supported = collectionType switch
        {
            CollectionTypeOptions.movies => ["Movie", "BoxSet"],
            CollectionTypeOptions.tvshows => ["Series", "Season", "Episode"],
            CollectionTypeOptions.mixed => ["Movie", "BoxSet", "Series", "Season", "Episode"],
            CollectionTypeOptions.boxsets => ["BoxSet"],
            _ => []
        };
        var changed = false;
        var types = (options.TypeOptions ?? []).ToList();
        foreach (var type in supported)
        {
            var useMetadata = type == "BoxSet" ? LibraryPolicy.AllowsCollection(CompanionModule.Metadata) : metadata;
            var useArtwork = type == "BoxSet" ? LibraryPolicy.AllowsCollection(CompanionModule.Artwork) : artwork;
            var item = types.FirstOrDefault(value => value.Type == type);
            if (item is null)
            {
                if (!useMetadata && !useArtwork) continue;
                item = defaults?.Invoke(type) ?? new TypeOptions { Type = type };
                types.Add(item);
                changed = true;
            }
            var metadataNames = useMetadata ? new[] { CompanionPlugin.MetadataProviderName } : [];
            var imageNames = new List<string>();
            if (useArtwork && type != "Episode") imageNames.Add(CompanionPlugin.ArtworkProviderName);
            if (useMetadata && CompanionPlugin.GetConfiguration().MetadataImagesEnabled && type is "Movie" or "BoxSet")
                imageNames.Add(CompanionPlugin.MetadataImageProviderName);
            var mf = Merge(item.MetadataFetchers, metadataNames, false);
            var mo = Merge(item.MetadataFetcherOrder, metadataNames, false);
            var imf = Merge(item.ImageFetchers, imageNames, true);
            var imo = Merge(item.ImageFetcherOrder, imageNames, true);
            changed |= !Same(item.MetadataFetchers, mf) || !Same(item.MetadataFetcherOrder, mo)
                || !Same(item.ImageFetchers, imf) || !Same(item.ImageFetcherOrder, imo);
            item.MetadataFetchers = mf; item.MetadataFetcherOrder = mo;
            item.ImageFetchers = imf; item.ImageFetcherOrder = imo;
        }
        if (changed) options.TypeOptions = types.ToArray();
        return changed;
    }
    private static bool Same(string[]? left, string[] right) => (left ?? []).SequenceEqual(right);
    private static string[] Merge(string[]? existing, IEnumerable<string> desired, bool images)
    {
        var own = images ? new[] { CompanionPlugin.ArtworkProviderName, CompanionPlugin.MetadataImageProviderName }
            : [CompanionPlugin.MetadataProviderName];
        return desired.Concat((existing ?? []).Where(name => !own.Contains(name, StringComparer.OrdinalIgnoreCase))).ToArray();
    }
}
