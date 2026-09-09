using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace RussianMetadata;

public sealed class ArtworkPreferenceRefreshService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ArtworkPreferenceRefreshService> _logger;

    public ArtworkPreferenceRefreshService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<ArtworkPreferenceRefreshService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public ArtworkPreferenceRefreshResult Queue(ArtworkPreferenceRefreshRequest request)
    {
        var movieTypes = GetImageTypes(request.MoviePosters, request.MovieLogos);
        var collectionTypes = GetImageTypes(
            request.CollectionPosters,
            request.CollectionLogos);
        var queuedMovies = QueueItems(BaseItemKind.Movie, movieTypes);
        var queuedCollections = QueueItems(BaseItemKind.BoxSet, collectionTypes);

        _logger.LogInformation(
            "ChooseYourMeta: применение настроек изображений поставлено в очередь для {Movies} фильмов и {Collections} коллекций",
            queuedMovies,
            queuedCollections);
        return new ArtworkPreferenceRefreshResult(queuedMovies, queuedCollections);
    }

    internal static ImageType[] GetImageTypes(bool posters, bool logos)
    {
        var result = new List<ImageType>(2);
        if (posters)
        {
            result.Add(ImageType.Primary);
        }

        if (logos)
        {
            result.Add(ImageType.Logo);
        }

        return result.ToArray();
    }

    private int QueueItems(BaseItemKind itemKind, ImageType[] imageTypes)
    {
        if (imageTypes.Length == 0)
        {
            return 0;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [itemKind],
            Recursive = true,
        });
        if (!CompanionPlugin.GetConfiguration().MetadataImagesEnabled) return 0;
        return CompanionPlugin.Images?.QueueMany(items.Select(item =>
            (item.Id, (IEnumerable<ImageType>)imageTypes)), CompanionModule.Metadata) ?? 0;
    }
}

public sealed record ArtworkPreferenceRefreshResult(
    int QueuedMovies,
    int QueuedCollections);
