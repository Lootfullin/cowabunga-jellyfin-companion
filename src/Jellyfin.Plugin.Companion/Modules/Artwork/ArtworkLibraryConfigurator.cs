using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed class ArtworkLibraryConfigurator
{
    internal const string ProviderName = "Cowabunga Custom Artwork";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArtworkLibraryConfigurator> _logger;

    public ArtworkLibraryConfigurator(
        ILibraryManager libraryManager,
        ILogger<ArtworkLibraryConfigurator> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public void Apply() => CompanionPlugin.Libraries?.Apply();

}
