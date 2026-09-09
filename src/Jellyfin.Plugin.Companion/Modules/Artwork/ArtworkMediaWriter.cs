using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed class ArtworkMediaWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ArtworkIndex _index;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArtworkMediaWriter> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArtworkMediaWriter(
        ArtworkIndex index,
        ILibraryManager libraryManager,
        ILogger<ArtworkMediaWriter> logger)
    {
        _index = index;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static string StatePath => Path.Combine(
        CompanionPlugin.Instance?.DataFolderPath ?? Path.GetTempPath(),
        "managed-media-files.v1.json");

    public async Task<IReadOnlyCollection<Guid>> ApplyAsync(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.Enabled(CompanionModule.Artwork)) return Array.Empty<Guid>();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = LoadState();
            if (!configuration.StorageMode.Equals(
                    PluginConfiguration.MediaFolderStorage,
                    StringComparison.Ordinal))
            {
                if (state.Files.Count == 0
                    && state.StorageMode.Equals(PluginConfiguration.JellyfinStorage, StringComparison.Ordinal))
                {
                    return Array.Empty<Guid>();
                }

                return CleanupAll(state);
            }

            var processAll = !state.StorageMode.Equals(
                    PluginConfiguration.MediaFolderStorage,
                    StringComparison.Ordinal)
                || state.Posters != configuration.Posters
                || state.Logos != configuration.Logos
                || state.OverwriteExistingMediaFiles != configuration.OverwriteExistingMediaFiles;
            var itemIds = processAll
                ? _index.Matches.Keys.ToHashSet()
                : _index.ChangedItemIds.ToHashSet();

            var allMatchedItemIds = _index.Matches.Keys.ToHashSet();
            var libraryItems = GetLibraryItems();
            var items = libraryItems
                .Where(pair => allMatchedItemIds.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var movieDirectoryCounts = BuildMovieDirectoryCounts(libraryItems.Values);
            if (!processAll)
            {
                AddPendingItems(
                    itemIds,
                    items,
                    movieDirectoryCounts,
                    configuration,
                    state);
            }

            foreach (var removed in state.Files.Values
                         .Where(file => !_index.Matches.ContainsKey(file.ItemId))
                         .Select(file => file.ItemId))
            {
                itemIds.Add(removed);
            }

            if (itemIds.Count == 0)
            {
                if (processAll)
                {
                    state.StorageMode = PluginConfiguration.MediaFolderStorage;
                    state.Posters = configuration.Posters;
                    state.Logos = configuration.Logos;
                    state.OverwriteExistingMediaFiles = configuration.OverwriteExistingMediaFiles;
                    SaveState(state);
                }

                return Array.Empty<Guid>();
            }

            var refreshed = new HashSet<Guid>();

            foreach (var itemId in itemIds)
            {
                if (!LibraryPolicy.Allows(CompanionModule.Artwork, _libraryManager.GetItemById(itemId))) continue;
                cancellationToken.ThrowIfCancellationRequested();
                if (!_index.Matches.TryGetValue(itemId, out var artwork)
                    || !items.TryGetValue(itemId, out var item)
                    || item is BoxSet)
                {
                    CleanupManagedFiles(
                        itemId,
                        state,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        refreshed);
                    continue;
                }

                var desiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var includeGenericAliases = CanUseGenericAliases(item, movieDirectoryCounts);
                if (configuration.Posters && artwork.Poster is not null)
                {
                    var path = GetDestinationPath(item, "poster", artwork.Poster.Path);
                    if (path is not null)
                    {
                        desiredPaths.Add(path);
                    }

                    AddManagedCandidates(
                        desiredPaths,
                        GetCandidatePaths(item, "poster", artwork.Poster.Path, includeGenericAliases),
                        itemId,
                        state);
                }

                if (configuration.Logos && artwork.Logo is not null)
                {
                    var path = GetDestinationPath(item, "logo", artwork.Logo.Path);
                    if (path is not null)
                    {
                        desiredPaths.Add(path);
                    }

                    AddManagedCandidates(
                        desiredPaths,
                        GetCandidatePaths(item, "logo", artwork.Logo.Path, includeGenericAliases),
                        itemId,
                        state);
                }

                CleanupManagedFiles(itemId, state, desiredPaths, refreshed);

                if (configuration.Posters && artwork.Poster is not null)
                {
                    if (await SynchronizeAsync(
                            item,
                            "poster",
                            artwork.Poster,
                            includeGenericAliases,
                            configuration,
                            state,
                            cancellationToken)
                            .ConfigureAwait(false))
                    {
                        refreshed.Add(itemId);
                    }
                }

                if (configuration.Logos && artwork.Logo is not null)
                {
                    if (await SynchronizeAsync(
                            item,
                            "logo",
                            artwork.Logo,
                            includeGenericAliases,
                            configuration,
                            state,
                            cancellationToken)
                            .ConfigureAwait(false))
                    {
                        refreshed.Add(itemId);
                    }
                }
            }

            state.StorageMode = PluginConfiguration.MediaFolderStorage;
            state.Posters = configuration.Posters;
            state.Logos = configuration.Logos;
            state.OverwriteExistingMediaFiles = configuration.OverwriteExistingMediaFiles;
            SaveState(state);
            return refreshed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void AddPendingItems(
        ISet<Guid> itemIds,
        IReadOnlyDictionary<Guid, BaseItem> items,
        IReadOnlyDictionary<string, int> movieDirectoryCounts,
        PluginConfiguration configuration,
        ManagedMediaState state)
    {
        foreach (var (itemId, artwork) in _index.Matches)
        {
            if (!items.TryGetValue(itemId, out var item) || item is BoxSet)
            {
                continue;
            }

            var includeGenericAliases = CanUseGenericAliases(item, movieDirectoryCounts);

            if (configuration.Posters
                && artwork.Poster is not null
                && NeedsRoleSynchronization(
                    GetCandidatePaths(item, "poster", artwork.Poster.Path, includeGenericAliases),
                    artwork.Poster.Sha256,
                    configuration.OverwriteExistingMediaFiles,
                    state))
            {
                itemIds.Add(itemId);
                continue;
            }

            if (configuration.Logos
                && artwork.Logo is not null
                && NeedsRoleSynchronization(
                    GetCandidatePaths(item, "logo", artwork.Logo.Path, includeGenericAliases),
                    artwork.Logo.Sha256,
                    configuration.OverwriteExistingMediaFiles,
                    state))
            {
                itemIds.Add(itemId);
            }
        }
    }

    internal static bool NeedsRoleSynchronization(
        IReadOnlyList<string> candidates,
        string sourceSha256,
        bool overwriteExistingMediaFiles,
        ManagedMediaState state)
    {
        if (candidates.Count == 0)
        {
            return false;
        }

        var destination = candidates[0];
        var aliases = candidates.Skip(1).Where(File.Exists).ToArray();
        if (!File.Exists(destination)
            && aliases.Any(path => !state.Files.ContainsKey(path))
            && !overwriteExistingMediaFiles)
        {
            return false;
        }

        if (NeedsSynchronization(destination, sourceSha256, overwriteExistingMediaFiles, state))
        {
            return true;
        }

        if (aliases.Length == 0)
        {
            return false;
        }

        return overwriteExistingMediaFiles
            || aliases.Any(path => state.Files.TryGetValue(path, out var managed)
                && managed.Sha256.Equals(sourceSha256, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool NeedsSynchronization(
        string? destination,
        string sourceSha256,
        bool overwriteExistingMediaFiles,
        ManagedMediaState state)
    {
        if (destination is null)
        {
            return false;
        }

        if (!File.Exists(destination))
        {
            return true;
        }

        if (!state.Files.TryGetValue(destination, out var managed))
        {
            return overwriteExistingMediaFiles;
        }

        return !managed.Sha256.Equals(
            sourceSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<Guid, BaseItem> GetLibraryItems()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes =
            [
                BaseItemKind.Movie,
                BaseItemKind.Series,
                BaseItemKind.Season,
            ],
            Recursive = true,
        };

        return _libraryManager
            .GetItemList(query)
            .ToDictionary(item => item.Id);
    }

    private async Task<bool> SynchronizeAsync(
        BaseItem item,
        string role,
        ArtworkManifestFile source,
        bool includeGenericAliases,
        PluginConfiguration configuration,
        ManagedMediaState state,
        CancellationToken cancellationToken)
    {
        var destination = GetDestinationPath(item, role, source.Path);
        if (destination is null)
        {
            _logger.LogWarning(
                "Custom Artwork: не определена папка медиатеки для {Item} ({Id})",
                item.Name,
                item.Id);
            return false;
        }

        var candidates = GetCandidatePaths(item, role, source.Path, includeGenericAliases);
        var existingAliases = candidates
            .Skip(1)
            .Where(File.Exists)
            .ToArray();
        if (!configuration.OverwriteExistingMediaFiles
            && existingAliases.Any(path => !state.Files.ContainsKey(path)))
        {
            _logger.LogWarning(
                "Custom Artwork: локальный файл для {Role} позиции {Item} сохранён; включите замену существующих локальных изображений",
                role,
                item.Name);
            return false;
        }

        state.Files.TryGetValue(destination, out var managed);
        var overwriteManagedFile = false;
        if (File.Exists(destination))
        {
            var currentHash = await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
            if (currentHash.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                state.Files[destination] = new ManagedMediaFile
                {
                    ItemId = item.Id,
                    Role = role,
                    Sha256 = source.Sha256,
                };
                return CleanupAliases(existingAliases, state, configuration.OverwriteExistingMediaFiles);
            }

            overwriteManagedFile = true;

            if (!CanOverwriteExistingFile(
                    configuration.OverwriteExistingMediaFiles,
                    managed,
                    currentHash))
            {
                state.Files.Remove(destination);
                _logger.LogWarning(
                    "Custom Artwork: локальный файл {Path} не перезаписан, потому что он создан или изменён не плагином",
                    destination);
                return false;
            }

            if (managed is null
                || !managed.Sha256.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
            {
                state.Files.Remove(destination);
                _logger.LogInformation(
                    "Custom Artwork: локальный файл {Path} заменяется изображением из облака согласно настройке",
                    destination);
            }
        }

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".cowabunga-artwork-{Guid.NewGuid():N}.tmp");
        try
        {
            var url = _index.GetArtworkUri(source.Path).AbsoluteUri;
            using var response = await _index
                .GetArtworkResponseAsync(url, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > 0
                && response.Content.Headers.ContentLength != source.Size)
            {
                throw new InvalidDataException($"Размер изображения не совпал с манифестом: {source.Path}");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var info = new FileInfo(temporaryPath);
            if (info.Length != source.Size)
            {
                throw new InvalidDataException($"Размер изображения не совпал с манифестом: {source.Path}");
            }

            var downloadedHash = await ComputeSha256Async(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!downloadedHash.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SHA-256 изображения не совпал с манифестом: {source.Path}");
            }

            if (!LibraryPolicy.Allows(CompanionModule.Artwork, item)) return false;
            var current = CompanionPlugin.GetConfiguration();
            if (role == "poster" && !current.Posters || role == "logo" && !current.Logos) return false;
            File.Move(temporaryPath, destination, overwriteManagedFile);
            state.Files[destination] = new ManagedMediaFile
            {
                ItemId = item.Id,
                Role = role,
                Sha256 = source.Sha256,
            };
            CleanupAliases(existingAliases, state, configuration.OverwriteExistingMediaFiles);
            return true;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Custom Artwork: не записать изображение {Path}", destination);
            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static string? GetDestinationPath(BaseItem item, string role, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        string itemPath;
        try
        {
            itemPath = Path.GetFullPath(item.Path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
        var isDirectoryItem = item is Series or Season || Directory.Exists(itemPath);
        var directory = isDirectoryItem ? itemPath : Path.GetDirectoryName(itemPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        string fileName;
        if (role == "logo")
        {
            fileName = isDirectoryItem
                ? "clearlogo.png"
                : $"{Path.GetFileNameWithoutExtension(itemPath)}-clearlogo.png";
        }
        else
        {
            var extension = Path.GetExtension(sourcePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? ".png"
                : ".jpg";
            fileName = isDirectoryItem
                ? $"poster{extension}"
                : $"{Path.GetFileNameWithoutExtension(itemPath)}-poster{extension}";
        }

        return Path.Combine(directory, fileName);
    }

    internal static IReadOnlyList<string> GetCandidatePaths(
        BaseItem item,
        string role,
        string sourcePath,
        bool includeGenericAliases)
    {
        var destination = GetDestinationPath(item, role, sourcePath);
        if (destination is null)
        {
            return Array.Empty<string>();
        }

        var directory = Path.GetDirectoryName(destination)!;
        var itemPath = Path.GetFullPath(item.Path);
        var isDirectoryItem = item is Series or Season || Directory.Exists(itemPath);
        var baseName = isDirectoryItem ? null : Path.GetFileNameWithoutExtension(itemPath);
        var stems = new List<string>();
        if (role.Equals("logo", StringComparison.Ordinal))
        {
            if (baseName is not null)
            {
                stems.Add($"{baseName}-clearlogo");
                stems.Add($"{baseName}-logo");
            }

            if (isDirectoryItem || includeGenericAliases)
            {
                stems.Add("clearlogo");
                stems.Add("logo");
            }
        }
        else
        {
            if (baseName is not null)
            {
                stems.Add($"{baseName}-poster");
                stems.Add($"{baseName}-folder");
                stems.Add($"{baseName}-cover");
                stems.Add($"{baseName}-default");
            }

            if (isDirectoryItem || includeGenericAliases)
            {
                stems.Add("poster");
                stems.Add("folder");
                stems.Add("cover");
                stems.Add("default");
            }
        }

        var sourceExtension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var extensions = new[] { sourceExtension, ".jpg", ".jpeg", ".png", ".webp" }
            .Where(extension => extension.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return new[] { destination }
            .Concat(stems.SelectMany(stem => extensions.Select(extension => Path.Combine(directory, stem + extension))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, int> BuildMovieDirectoryCounts(IEnumerable<BaseItem> items)
    {
        return items
            .OfType<Movie>()
            .Select(item => GetMediaDirectory(item.Path))
            .Where(path => path is not null)
            .GroupBy(path => path!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool CanUseGenericAliases(
        BaseItem item,
        IReadOnlyDictionary<string, int> movieDirectoryCounts)
    {
        if (item is not Movie)
        {
            return true;
        }

        var directory = GetMediaDirectory(item.Path);
        return directory is not null
            && movieDirectoryCounts.TryGetValue(directory, out var count)
            && count == 1;
    }

    private static string? GetMediaDirectory(string? itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(itemPath));
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static void AddManagedCandidates(
        ISet<string> desiredPaths,
        IEnumerable<string> candidates,
        Guid itemId,
        ManagedMediaState state)
    {
        foreach (var path in candidates)
        {
            if (state.Files.TryGetValue(path, out var managed) && managed.ItemId == itemId)
            {
                desiredPaths.Add(path);
            }
        }
    }

    private bool CleanupAliases(
        IEnumerable<string> aliases,
        ManagedMediaState state,
        bool overwriteExistingMediaFiles)
    {
        var changed = false;
        foreach (var path in aliases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (overwriteExistingMediaFiles)
                {
                    File.Delete(path);
                    state.Files.Remove(path);
                    changed = true;
                    continue;
                }

                if (!state.Files.TryGetValue(path, out var managed))
                {
                    continue;
                }

                var result = DeleteManagedFile(path, managed);
                if (result == ManagedDeleteResult.Deleted)
                {
                    changed = true;
                }

                if (result != ManagedDeleteResult.Retry)
                {
                    state.Files.Remove(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Custom Artwork: не удалить конфликтующий локальный файл {Path}", path);
            }
        }

        return changed;
    }

    private static void CleanupManagedFiles(
        Guid itemId,
        ManagedMediaState state,
        IReadOnlySet<string> desiredPaths,
        ISet<Guid> refreshed)
    {
        var obsolete = state.Files
            .Where(pair => pair.Value.ItemId == itemId && !desiredPaths.Contains(pair.Key))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var path in obsolete)
        {
            var config = CompanionPlugin.GetConfiguration();
            if (state.Files[path].Role == "poster" && !config.Posters
                || state.Files[path].Role == "logo" && !config.Logos) continue;
            var result = DeleteManagedFile(path, state.Files[path]);
            if (result == ManagedDeleteResult.Deleted)
            {
                refreshed.Add(itemId);
            }

            if (result != ManagedDeleteResult.Retry)
            {
                state.Files.Remove(path);
            }
        }
    }

    private IReadOnlyCollection<Guid> CleanupAll(ManagedMediaState state)
    {
        var refreshed = new HashSet<Guid>();
        foreach (var (path, file) in state.Files.ToList())
        {
            if (!LibraryPolicy.Allows(CompanionModule.Artwork, _libraryManager.GetItemById(file.ItemId))) continue;
            var result = DeleteManagedFile(path, file);
            if (result == ManagedDeleteResult.Deleted)
            {
                refreshed.Add(file.ItemId);
            }

            if (result != ManagedDeleteResult.Retry)
            {
                state.Files.Remove(path);
            }
        }

        state.StorageMode = PluginConfiguration.JellyfinStorage;
        SaveState(state);
        return refreshed;
    }

    internal static bool DeleteIfUnchanged(string path, ManagedMediaFile managed)
        => DeleteManagedFile(path, managed) == ManagedDeleteResult.Deleted;

    internal static bool CanOverwriteExistingFile(
        bool overwriteExistingMediaFiles,
        ManagedMediaFile? managed,
        string currentHash)
        => overwriteExistingMediaFiles
            || managed is not null
            && managed.Sha256.Equals(currentHash, StringComparison.OrdinalIgnoreCase);

    private static ManagedDeleteResult DeleteManagedFile(string path, ManagedMediaFile managed)
    {
        if (!File.Exists(path))
        {
            return ManagedDeleteResult.Relinquished;
        }

        try
        {
            string currentHash;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                currentHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }

            if (!currentHash.Equals(managed.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return ManagedDeleteResult.Relinquished;
            }

            File.Delete(path);
            return ManagedDeleteResult.Deleted;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ManagedDeleteResult.Retry;
        }
    }

    private enum ManagedDeleteResult
    {
        Deleted,
        Relinquished,
        Retry,
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ManagedMediaState LoadState()
    {
        if (!File.Exists(StatePath))
        {
            return new ManagedMediaState();
        }

        try
        {
            var state = JsonSerializer.Deserialize<ManagedMediaState>(File.ReadAllText(StatePath), JsonOptions)
                ?? new ManagedMediaState();
            state.Files = new Dictionary<string, ManagedMediaFile>(state.Files, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new ManagedMediaState();
        }
    }

    private static void SaveState(ManagedMediaState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporaryPath = $"{StatePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
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
