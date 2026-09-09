using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Companion;

public enum CompanionModule { Resolver, Metadata, Artwork }

public sealed class LibraryPolicy(ILibraryManager libraryManager)
{
    private readonly object _gate = new();
    private VirtualFolderInfo[] _folders = [];
    private DateTime _expires;
    private bool _loading;

    public VirtualFolderInfo[] GetLibraries()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow >= _expires)
            {
                // Jellyfin can invoke resolvers while constructing its virtual folders.
                // Do not recursively request the same folder graph during that construction.
                if (_loading) return _folders;
                _loading = true;
                try
                {
                    _folders = libraryManager.GetVirtualFolders().ToArray();
                    _expires = DateTime.UtcNow.AddSeconds(10);
                }
                finally { _loading = false; }
            }
            return _folders;
        }
    }

    public void Invalidate() { lock (_gate) _expires = DateTime.MinValue; }

    public static bool Enabled(CompanionModule module)
    {
        var config = CompanionPlugin.Instance?.Configuration;
        return config is { Enabled: true } && !CompanionPlugin.BlockedModules.Contains(module) && module switch
        {
            CompanionModule.Resolver => config.ResolverEnabled,
            CompanionModule.Metadata => config.MetadataEnabled,
            CompanionModule.Artwork => config.ArtworkEnabled,
            _ => false
        };
    }

    public static bool Allows(CompanionModule module, BaseItem? item)
    {
        if (!Enabled(module) || item is null) return false;
        if (item is BoxSet) return AllowsCollection(module);
        return AllowsPath(module, item.Path);
    }

    public static bool AllowsCollection(CompanionModule module)
    {
        var config = CompanionPlugin.GetConfiguration();
        return Enabled(module) && module switch
        {
            CompanionModule.Metadata => config.CollectionMetadataEnabled,
            CompanionModule.Artwork => config.CollectionArtworkEnabled,
            _ => false
        };
    }

    public static bool AllowsPath(CompanionModule module, string? path) =>
        Enabled(module) && CompanionPlugin.Policy?.ForPath(module, path) == true;

    public bool ForLibrary(CompanionModule module, Guid id)
    {
        var config = CompanionPlugin.GetConfiguration();
        if (!Enabled(module)) return false;
        var rule = config.Libraries.FirstOrDefault(value => value.LibraryId == id);
        if (rule is null) return config.AllLibraries;
        return module switch
        {
            CompanionModule.Resolver => rule.Resolver,
            CompanionModule.Metadata => rule.Metadata,
            CompanionModule.Artwork => rule.Artwork,
            _ => false
        };
    }

    public bool ForPath(CompanionModule module, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var candidates = GetLibraries()
            .SelectMany(folder => (folder.Locations ?? []).Select(root => (folder.ItemId, Root: root)))
            .Where(value => ContainsPath(value.Root, path))
            .ToArray();
        if (candidates.Length == 0) return false;
        var longest = candidates.Max(value => value.Root.TrimEnd('/', '\\').Length);
        // Shared paths must be allowed in every equally specific library.
        return candidates.Where(value => value.Root.TrimEnd('/', '\\').Length == longest)
            .All(value => Guid.TryParse(value.ItemId, out var id) && ForLibrary(module, id));
    }

    internal static bool ContainsPath(string root, string path)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedPath = Path.GetFullPath(path);
            return normalizedPath.Equals(normalizedRoot, comparison)
                || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }
}
