namespace Jellyfin.Plugin.SmartResolver.Core;

public static class PathSafety
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static bool IsDirectSafeChild(string outerPath, string innerPath)
    {
        try
        {
            var outer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outerPath));
            var inner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(innerPath));
            if (string.Equals(outer, inner, PathComparison))
            {
                return false;
            }

            var parent = Directory.GetParent(inner)?.FullName;
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(parent ?? string.Empty),
                    outer,
                    PathComparison))
            {
                return false;
            }

            var directory = new DirectoryInfo(inner);
            return directory.LinkTarget is null
                && (directory.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }
}
