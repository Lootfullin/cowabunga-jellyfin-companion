using System.Globalization;
using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed class RefreshIndexTask : IScheduledTask
{
    private readonly ArtworkIndex _index;
    private readonly ArtworkMediaWriter _mediaWriter;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<RefreshIndexTask> _logger;
    private readonly ArtworkLibraryConfigurator _libraryConfigurator;

    public RefreshIndexTask(
        ArtworkIndex index,
        ArtworkMediaWriter mediaWriter,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ArtworkLibraryConfigurator libraryConfigurator,
        ILogger<RefreshIndexTask> logger)
    {
        _index = index;
        _mediaWriter = mediaWriter;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _libraryConfigurator = libraryConfigurator;
        _logger = logger;
    }

    public string Name => "Обновить индекс кастомных обложек";

    public string Key => "CustomArtworkRefreshIndex";

    public string Description =>
        "Проверяет ревизию приватного облака и обновляет только изменившиеся постеры и логотипы.";

    public string Category => "Cowabunga Jellyfin Companion";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.Enabled(CompanionModule.Artwork)) return;
        await ArtworkOperations.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!LibraryPolicy.Enabled(CompanionModule.Artwork)) return;
            var plugin = CompanionPlugin.Instance!;
            _libraryConfigurator.Apply();
            await _index.BuildAsync(progress, cancellationToken).ConfigureAwait(false);
            var configuration = plugin.Configuration;
            configuration.LastIndexedUtc = _index.BuiltUtc == DateTime.MinValue ? "" : _index.BuiltUtc.ToString("u", CultureInfo.InvariantCulture);
            configuration.LastIndexedCount = _index.Count;
            configuration.LastRevision = _index.Revision;
            configuration.LastError = _index.LastError;
            plugin.SaveConfiguration();
            if (!_index.RemoteArtworkAvailable) return;
            var mediaChanges = await _mediaWriter.ApplyAsync(configuration, cancellationToken).ConfigureAwait(false);
            foreach (var itemId in mediaChanges)
            {
                // Discover verified local files without forcing other remote sources to replace them.
                _providerManager.QueueRefresh(itemId, new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.None,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh
                }, RefreshPriority.Normal);
            }
            var requests = _index.Matches.Select(pair => new ArtworkRefreshRequest(pair.Key, GetRefreshImageTypes(pair.Value, configuration)))
                .Concat(_index.ChangedImageTypes.Select(pair => new ArtworkRefreshRequest(pair.Key, FilterEnabledImageTypes(pair.Value, configuration))));
            RequeueChangedItems(requests);
            _index.AcknowledgeChanges();
            progress.Report(100);
        }
        finally { ArtworkOperations.Gate.Release(); }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger,
        };

        var interval = CompanionPlugin.Instance?.Configuration.RefreshIntervalMinutes ?? 5;
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(Math.Max(1, interval)).Ticks,
        };
    }

    private IEnumerable<ArtworkRefreshRequest> CreateRefreshRequests(
        IEnumerable<Guid> itemIds,
        Jellyfin.Plugin.Companion.Configuration.PluginConfiguration configuration)
    {
        foreach (var itemId in itemIds.Distinct())
        {
            _index.Matches.TryGetValue(itemId, out var artwork);
            var imageTypes = GetRefreshImageTypes(artwork, configuration);

            if (imageTypes.Count > 0)
            {
                yield return new ArtworkRefreshRequest(itemId, imageTypes);
            }
        }
    }

    internal static IReadOnlyCollection<ImageType> GetRefreshImageTypes(
        ArtworkSet? artwork,
        Jellyfin.Plugin.Companion.Configuration.PluginConfiguration configuration)
    {
        var imageTypes = new List<ImageType>(2);
        if (configuration.Posters && (artwork is null || artwork.Poster is not null))
        {
            imageTypes.Add(ImageType.Primary);
        }

        if (configuration.Logos && (artwork is null || artwork.Logo is not null))
        {
            imageTypes.Add(ImageType.Logo);
        }

        return imageTypes;
    }

    internal static IReadOnlyCollection<ImageType> FilterEnabledImageTypes(
        IEnumerable<ImageType> imageTypes,
        Jellyfin.Plugin.Companion.Configuration.PluginConfiguration configuration)
    {
        return imageTypes
            .Where(imageType => imageType switch
            {
                ImageType.Primary => configuration.Posters,
                ImageType.Logo => configuration.Logos,
                _ => false,
            })
            .Distinct()
            .ToArray();
    }

    internal static IReadOnlyCollection<ImageType> GetRemovedImageTypes(
        ArtworkSet? artwork,
        IEnumerable<ImageType> changedImageTypes)
    {
        return changedImageTypes
            .Where(imageType => imageType switch
            {
                ImageType.Primary => artwork?.Poster is null,
                ImageType.Logo => artwork?.Logo is null,
                _ => false,
            })
            .Distinct()
            .ToArray();
    }

    private void RequeueChangedItems(IEnumerable<ArtworkRefreshRequest> requests)
    {
        var changed = MergeRefreshRequests(requests);
        CompanionPlugin.Images?.QueueMany(changed.Select(request =>
            (request.ItemId, (IEnumerable<ImageType>)request.ImageTypes)), CompanionModule.Artwork);

        _logger.LogInformation(
            "Custom Artwork: обновление изображений поставлено в очередь для {Count} позиций",
            changed.Count);
    }

    internal static IReadOnlyList<ArtworkRefreshRequest> MergeRefreshRequests(
        IEnumerable<ArtworkRefreshRequest> requests)
    {
        return requests
            .Where(request => request.ItemId != Guid.Empty)
            .GroupBy(request => request.ItemId)
            .Select(group => new ArtworkRefreshRequest(
                group.Key,
                group.SelectMany(request => request.ImageTypes).Distinct().ToArray()))
            .ToList();
    }
}
