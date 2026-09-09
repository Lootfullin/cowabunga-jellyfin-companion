using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace RussianMetadata;

public sealed class ReconcileCollectionMetadataTask : IScheduledTask
{
    private const int MaxCollectionsPerRun = 50;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly LibraryConfigurationService _configurationService;
    private readonly ILogger<ReconcileCollectionMetadataTask> _logger;

    public ReconcileCollectionMetadataTask(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        LibraryConfigurationService configurationService,
        ILogger<ReconcileCollectionMetadataTask> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _configurationService = configurationService;
        _logger = logger;
    }

    public string Name => "Проверить русские названия коллекций";

    public string Key => "ChooseYourMetaReconcileCollections";

    public string Description =>
        "Проверяет TMDB ID коллекций по входящим фильмам и возвращает русские названия после фоновых задач Jellyfin или TMDB.";

    public string Category => "Cowabunga Jellyfin Companion";

    private static string StatePath => Path.Combine(
        CompanionPlugin.Instance?.DataFolderPath ?? Path.GetTempPath(),
        "collection-reconciliation.v1.json");

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.AllowsCollection(CompanionModule.Metadata)) return Task.CompletedTask;

        _configurationService.Apply();
        var localizationEnabled = CompanionPlugin.Instance?.Configuration.EnableRussianTitles == true;

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true,
        };
        var candidates = _libraryManager.GetItemList(query)
            .OfType<BoxSet>()
            .OrderBy(item => item.Id)
            .ToArray();
        var state = LoadState();
        var candidateIds = candidates.Select(item => item.Id).ToHashSet();
        foreach (var removedId in state.Entries.Keys.Except(candidateIds).ToArray())
        {
            state.Entries.Remove(removedId);
        }

        foreach (var item in candidates)
        {
            state.Entries.TryAdd(item.Id, new CollectionReconciliationEntry());
        }

        var now = DateTime.UtcNow;
        var selected = candidates
            .Where(item => NeedsRefresh(
                state.Entries[item.Id],
                localizationEnabled,
                MovieTextLocalization.ContainsCyrillic(item.Name),
                now))
            .OrderBy(item => state.Entries[item.Id].NextAttemptUtc)
            .ThenBy(item => item.Id)
            .Take(MaxCollectionsPerRun)
            .ToArray();

        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.PreferredMetadataLanguage = "ru";
            item.PreferredMetadataCountryCode = "RU";
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.None,
                ReplaceAllMetadata = false,
            };
            _providerManager.QueueRefresh(item.Id, options, RefreshPriority.High);
            var entry = state.Entries[item.Id];
            entry.IdentityAuditQueued = true;
            entry.Attempts++;
            entry.NextAttemptUtc = now + RetryDelay(entry.Attempts);
        }

        SaveState(state);
        progress.Report(100);
        if (selected.Length > 0)
        {
            _logger.LogInformation(
                "ChooseYourMeta: проверка TMDB ID и русской локализации поставлена в очередь для {Count} коллекций; всего в аудите {Total}",
                selected.Length,
                candidates.Length);
        }

        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(15).Ticks,
        };
    }

    internal static TimeSpan RetryDelay(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(1440, 15 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 7))));

    internal static bool NeedsRefresh(
        CollectionReconciliationEntry entry,
        bool localizationEnabled,
        bool nameContainsCyrillic,
        DateTime now) =>
        !entry.IdentityAuditQueued
        || (localizationEnabled && !nameContainsCyrillic && entry.NextAttemptUtc <= now);

    private static CollectionReconciliationState LoadState()
    {
        if (!File.Exists(StatePath))
        {
            return new CollectionReconciliationState();
        }

        try
        {
            return JsonSerializer.Deserialize<CollectionReconciliationState>(
                File.ReadAllText(StatePath),
                JsonOptions) ?? new CollectionReconciliationState();
        }
        catch (JsonException)
        {
            return new CollectionReconciliationState();
        }
        catch (IOException)
        {
            return new CollectionReconciliationState();
        }
    }

    private static void SaveState(CollectionReconciliationState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporaryPath = $"{StatePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(state, JsonOptions),
                new UTF8Encoding(false));
            File.Move(temporaryPath, StatePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed class CollectionReconciliationState
{
    public Dictionary<Guid, CollectionReconciliationEntry> Entries { get; set; } = [];
}

internal sealed class CollectionReconciliationEntry
{
    public bool IdentityAuditQueued { get; set; }

    public int Attempts { get; set; }

    public DateTime NextAttemptUtc { get; set; }
}
