using Jellyfin.Data.Enums;

namespace RussianMetadata;

internal static class MovieTextLocalization
{
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
