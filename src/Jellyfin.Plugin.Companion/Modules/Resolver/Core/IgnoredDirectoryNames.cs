namespace Jellyfin.Plugin.SmartResolver.Core;

public static class IgnoredDirectoryNames
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "extrafanart",
        "extrathumbs",
        ".metadata",
        "@eaDir",
        ".recycle",
        "$RECYCLE.BIN",
        "System Volume Information",
        "lost+found"
    };

    public static bool Contains(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && Names.Contains(name);
    }
}
