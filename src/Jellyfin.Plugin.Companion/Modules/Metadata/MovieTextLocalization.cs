using Jellyfin.Data.Enums;

namespace RussianMetadata;

internal static class MovieTextLocalization
{
    internal static bool ApplyDescription(MediaBrowser.Controller.Entities.Movies.Movie item,
        string? overview, string? tagline, Jellyfin.Plugin.Companion.Configuration.PluginConfiguration config)
    {
        if (item.IsLocked) return false;
        var changed = false;
        var russianOverview = RussianOrNull(overview);
        if (config.EnableRussianOverviews && !item.LockedFields.Contains(MediaBrowser.Model.Entities.MetadataField.Overview)
            && russianOverview is not null && item.Overview != russianOverview)
        {
            item.Overview = russianOverview;
            changed = true;
        }
        var russianTagline = RussianOrNull(tagline);
        if (config.EnableRussianTaglines
            && russianTagline is not null && item.Tagline != russianTagline)
        {
            item.Tagline = russianTagline;
            changed = true;
        }
        return changed;
    }
    public static bool ContainsCyrillic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Any(character =>
            (character >= '\u0400' && character <= '\u04FF')
            || (character >= '\u0500' && character <= '\u052F'));
    }

    public static string? RussianOrNull(string? value)
    {
        return ContainsCyrillic(value) ? value?.Trim() : null;
    }

    public static PersonKind? MapCrewJob(string? job)
    {
        return job switch
        {
            "Director" => PersonKind.Director,
            "Screenplay" or "Writer" or "Story" or "Teleplay" => PersonKind.Writer,
            "Producer" or "Executive Producer" => PersonKind.Producer,
            "Original Music Composer" or "Music" => PersonKind.Composer,
            _ => null
        };
    }
}
