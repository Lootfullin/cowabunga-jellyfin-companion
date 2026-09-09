using MediaBrowser.Model.Plugins;
namespace Jellyfin.Plugin.Companion.Configuration;
public enum ArtworkLanguagePreference { RussianFirst, EnglishFirst, Disabled }
public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool ResolverEnabled { get; set; } = true;
    public bool MetadataEnabled { get; set; } = true;
    public bool ArtworkEnabled { get; set; } = true;
    public bool MetadataImagesEnabled { get; set; } = true;
    public bool ReplaceExistingImages { get; set; }
    public bool AllLibraries { get; set; }
    public LibraryRule[] Libraries { get; set; } = [];
    public bool CollectionMetadataEnabled { get; set; }
    public bool CollectionArtworkEnabled { get; set; }
    public int ConfigurationSchemaVersion { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public bool NestedSeriesEnabled { get; set; } = true;

    public ResolverMode NestedSeriesMode { get; set; } = ResolverMode.YearPrefix;

    public string CustomRegex { get; set; } = string.Empty;

    public bool RequireExactlyOneEligibleChild { get; set; } = true;

    public bool MoviesEnabled { get; set; } = true;

    public bool NestedMoviesEnabled { get; set; } = true;

    public bool EnableResolutionLogs { get; set; } = true;

    public bool EnableRejectionLogs { get; set; }

    public const string JellyfinStorage = "Jellyfin";

    public const string MediaFolderStorage = "MediaFolder";

    public int RefreshIntervalMinutes { get; set; } = 5;

    public bool Posters { get; set; } = true;

    public bool Logos { get; set; } = true;

    public string StorageMode { get; set; } = JellyfinStorage;

    public bool OverwriteExistingMediaFiles { get; set; }

    public string LastIndexedUtc { get; set; } = string.Empty;

    public int LastIndexedCount { get; set; }

    public string LastRevision { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;

    public string TmdbApiKey { get; set; } = "";
    public bool EnableRussianTitles { get; set; } = true;
    public bool EnableRussianOverviews { get; set; } = true;
    public bool EnableRussianTaglines { get; set; } = true;
    public bool EnableRussianGenres { get; set; } = true;
    public bool EnableRussianStudios { get; set; } = true;
    public bool EnableRussianPeople { get; set; } = true;
    public ArtworkLanguagePreference ForeignMoviePosterPreference { get; set; } =
        ArtworkLanguagePreference.EnglishFirst;
    public ArtworkLanguagePreference ForeignMovieLogoPreference { get; set; } =
        ArtworkLanguagePreference.EnglishFirst;
    public ArtworkLanguagePreference RussianMoviePosterPreference { get; set; } =
        ArtworkLanguagePreference.RussianFirst;
    public ArtworkLanguagePreference RussianMovieLogoPreference { get; set; } =
        ArtworkLanguagePreference.RussianFirst;
    public ArtworkLanguagePreference CollectionPosterPreference { get; set; } =
        ArtworkLanguagePreference.EnglishFirst;
    public ArtworkLanguagePreference CollectionLogoPreference { get; set; } =
        ArtworkLanguagePreference.EnglishFirst;
    public string ProxyUrl { get; set; } = "";
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";
    public int ArtworkRefreshSchemaVersion { get; set; }
}
public sealed class LibraryRule
{
    public Guid LibraryId { get; set; }
    public bool Resolver { get; set; } = true;
    public bool Metadata { get; set; } = true;
    public bool Artwork { get; set; } = true;
}
