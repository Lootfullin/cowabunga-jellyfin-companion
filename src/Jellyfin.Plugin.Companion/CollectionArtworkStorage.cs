using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Companion;

internal static class CollectionArtworkStorage
{
    // Jellyfin's own collection folders are not folders containing the user's videos.
    internal static bool UsesNativeFolder(BaseItem item) => item is BoxSet
        && !string.IsNullOrWhiteSpace(item.Path)
        && CompanionPlugin.Instance is { } plugin
        && LibraryPolicy.ContainsPath(plugin.CollectionsPath, item.Path)
        && !Path.GetFullPath(item.Path).TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.GetFullPath(plugin.CollectionsPath).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static string[] Aliases(BaseItem item, ImageType type)
    {
        if (!UsesNativeFolder(item) || !Directory.Exists(item.Path)) return [];
        string[] names = type == ImageType.Primary ? ["poster", "folder", "cover", "default"] :
            type == ImageType.Logo ? ["logo", "clearlogo"] : [];
        string[] extensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tbn"];
        return Directory.EnumerateFiles(item.Path).Where(path =>
            names.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            && extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    internal static void RetireAliases(BaseItem item, ImageType type, string savedPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!UsesNativeFolder(item) || !LibraryPolicy.ContainsPath(item.Path, savedPath)) return;
        foreach (var path in Aliases(item, type))
        {
            if (Path.GetFullPath(path).Equals(Path.GetFullPath(savedPath), comparison)) continue;
            // Keep obsolete alternatives outside the folder scanned by Jellyfin.
            var backup = Path.Combine(CompanionPlugin.Instance!.DataFolderPath, "collection-image-backups", item.Id.ToString("N"));
            Directory.CreateDirectory(backup);
            File.Move(path, Path.Combine(backup, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path)));
        }
    }
}
