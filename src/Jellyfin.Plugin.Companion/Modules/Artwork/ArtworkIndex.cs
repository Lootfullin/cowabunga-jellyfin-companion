using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomArtwork;

public sealed partial class ArtworkIndex
{
    private const int MaxRevisionBytes = 4096;
    private const int StateSchemaVersion = 3;
    private const int MaxManifestBytes = 32 * 1024 * 1024;
    private const int MaxManifestFiles = 100_000;
    private const long MaxArtworkBytes = 100 * 1024 * 1024;
    private const string RevisionFileName = "artwork-index.revision.json";
    private const string ManifestFileName = "artwork-index.v1.json";
    private const string MediaPath = "Media";

    internal static readonly Uri ServiceBaseUri = new(
        "https://artwork.lootfullin.netcraze.pro/",
        UriKind.Absolute);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<ArtworkIndex> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<ArtworkIdentity, ArtworkSet> _byIdentity = [];
    private Dictionary<Guid, ArtworkSet> _byItemId = [];
    private HashSet<string> _allowedPaths = new(StringComparer.Ordinal);
    private ArtworkManifest? _manifest;
    private ArtworkState? _pendingState;
    private Func<BaseItem, ArtworkSet?>? _matchNewItem;

    public ArtworkIndex(
        ILogger<ArtworkIndex> logger,
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
    }

    public DateTime BuiltUtc { get; private set; } = DateTime.MinValue;

    public int Count { get; private set; }

    public string Revision { get; private set; } = string.Empty;

    public string LastError { get; private set; } = string.Empty;

    internal bool RemoteArtworkAvailable => string.IsNullOrWhiteSpace(LastError);

    public IReadOnlyCollection<Guid> ChangedItemIds { get; private set; } = Array.Empty<Guid>();

    internal IReadOnlyDictionary<Guid, IReadOnlyCollection<ImageType>> ChangedImageTypes { get; private set; }
        = new Dictionary<Guid, IReadOnlyCollection<ImageType>>();

    internal IReadOnlyDictionary<Guid, ArtworkSet> Matches => _byItemId;

    internal void AcknowledgeChanges()
    {
        if (_pendingState is null)
        {
            return;
        }

        SaveJsonAtomically(StatePath, _pendingState);
        _pendingState = null;
        ChangedItemIds = Array.Empty<Guid>();
        ChangedImageTypes = new Dictionary<Guid, IReadOnlyCollection<ImageType>>();
    }

    private static string DataFolder =>
        CompanionPlugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "CowabungaCustomArtwork");

    private static string ManifestCachePath => Path.Combine(DataFolder, "artwork-index.v1.json");

    private static string StatePath => Path.Combine(DataFolder, "artwork-state.v1.json");

    public bool IsStale(int intervalMinutes) =>
        BuiltUtc == DateTime.MinValue
        || (DateTime.UtcNow - BuiltUtc).TotalMinutes >= Math.Max(1, intervalMinutes);

    internal ArtworkSet? Find(BaseItem item)
    {
        if (_byItemId.TryGetValue(item.Id, out var byItemId))
        {
            return byItemId;
        }

        if (TryGetIdentity(item, out var identity)
            && _byIdentity.TryGetValue(identity, out var byIdentity))
        {
            return byIdentity;
        }

        // A collection can be created while the library map is still fresh.
        // Match it against the already downloaded manifest without waiting for a full rebuild.
        return _matchNewItem?.Invoke(item);
    }

    public async Task BuildAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.Enabled(CompanionModule.Artwork)) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastError = string.Empty;
            progress?.Report(1);

            await RefreshManifestAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(35);

            if (_manifest is null)
            {
                LastError = "Нет доступного индекса artwork.";
                return;
            }

            BuildLibraryMap(_manifest, progress, cancellationToken);
            BuiltUtc = DateTime.UtcNow;
            Revision = _manifest.Revision;
            progress?.Report(100);

            _logger.LogInformation(
                "Custom Artwork: индекс {Revision}, сопоставлено позиций {Count}, изменилось {Changed}",
                Revision,
                Count,
                ChangedItemIds.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or TaskCanceledException
            or IOException
            or JsonException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            LastError = exception.Message;
            _logger.LogWarning(exception, "Custom Artwork: не удалось обновить индекс");

            if (_manifest is null)
            {
                _manifest = LoadCachedManifest();
            }

            if (_manifest is not null)
            {
                BuildLibraryMap(_manifest, progress, cancellationToken);
                BuiltUtc = DateTime.UtcNow;
                Revision = _manifest.Revision;
                progress?.Report(100);
                _logger.LogInformation("Custom Artwork: используется последняя локальная копия индекса {Revision}", Revision);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Uri GetArtworkUri(string path)
    {
        var relative = JoinRelative(MediaPath, path);
        return BuildUri(ServiceBaseUri, relative);
    }

    public async Task<HttpResponseMessage> GetArtworkResponseAsync(
        string url,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var requested)
            || !TryGetRelativeArtworkPath(requested, BuildUri(ServiceBaseUri, MediaPath), out var path)
            || !_allowedPaths.Contains(path))
        {
            throw new InvalidOperationException("Custom Artwork rejected an unexpected image URL.");
        }

        using var request = CreateRequest(HttpMethod.Get, requested);
        try
        {
            var response = await _httpClientFactory
                .CreateClient("Default")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"Облако artwork вернуло HTTP {(int)response.StatusCode}.";
            }

            return response;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LastError = exception.Message;
            throw;
        }
    }

    private async Task RefreshManifestAsync(CancellationToken cancellationToken)
    {
        var revision = await ReadJsonAsync<ArtworkRevision>(
            BuildUri(ServiceBaseUri, RevisionFileName),
            MaxRevisionBytes,
            cancellationToken).ConfigureAwait(false);

        if (!IsSha256(revision.Revision))
        {
            throw new InvalidDataException("Файл ревизии artwork содержит некорректное значение.");
        }

        if (_manifest is null)
        {
            _manifest = LoadCachedManifest();
        }

        if (_manifest?.Revision.Equals(revision.Revision, StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        var manifest = await ReadJsonAsync<ArtworkManifest>(
            BuildUri(ServiceBaseUri, ManifestFileName),
            MaxManifestBytes,
            cancellationToken).ConfigureAwait(false);

        ValidateManifest(manifest, revision.Revision);
        SaveJsonAtomically(ManifestCachePath, manifest);
        _manifest = manifest;
    }

    private void BuildLibraryMap(
        ArtworkManifest manifest,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var byRelease = BuildReleaseLookup(manifest.Files);
        var byCollection = BuildCollectionLookup(manifest.Files);
        var byPublishedIdentity = BuildPublishedIdentityLookup(manifest.Files);
        var byCollectionPart = BuildCollectionPartLookup(manifest.Files);
        var byCustomCollectionKey = BuildCustomCollectionLookup(manifest.Files);
        var identities = new Dictionary<ArtworkIdentity, ArtworkSet>();
        var ambiguousIdentities = new HashSet<ArtworkIdentity>();
        var itemIds = new Dictionary<Guid, ArtworkSet>();
        var currentState = new ArtworkState
        {
            SchemaVersion = StateSchemaVersion,
            Revision = manifest.Revision,
        };
        var previousState = LoadState();
        var forceCurrentArtworkRefresh = previousState.SchemaVersion != StateSchemaVersion;
        var changed = new HashSet<Guid>();
        var changedImageTypes = new Dictionary<Guid, HashSet<ImageType>>();

        var query = new InternalItemsQuery
        {
            IncludeItemTypes =
            [
                BaseItemKind.Movie,
                BaseItemKind.Series,
                BaseItemKind.Season,
                BaseItemKind.BoxSet,
            ],
            Recursive = true,
        };
        var items = _libraryManager.GetItemList(query);

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            var stateKey = $"item:{item.Id:N}";
            previousState.Entries.TryGetValue(stateKey, out var previousEntry);
            var artwork = MatchItem(
                item,
                byRelease,
                byCollection,
                byPublishedIdentity,
                byCollectionPart,
                byCustomCollectionKey,
                previousEntry?.CollectionKey);
            if (artwork is null)
            {
                continue;
            }

            itemIds[item.Id] = artwork;
            if (TryGetIdentity(item, out var identity))
            {
                if (!ambiguousIdentities.Contains(identity))
                {
                    if (identities.TryGetValue(identity, out var existing)
                        && !existing.Fingerprint.Equals(artwork.Fingerprint, StringComparison.Ordinal))
                    {
                        identities.Remove(identity);
                        ambiguousIdentities.Add(identity);
                    }
                    else
                    {
                        identities[identity] = artwork;
                    }
                }
            }

            currentState.Entries[stateKey] = new ArtworkStateEntry
            {
                ItemId = item.Id,
                Fingerprint = artwork.Fingerprint,
                CollectionKey = artwork.CollectionKey,
            };

            if (forceCurrentArtworkRefresh
                || previousEntry is null
                || !previousEntry.Fingerprint.Equals(artwork.Fingerprint, StringComparison.Ordinal))
            {
                changed.Add(item.Id);
                AddChangedImageTypes(
                    changedImageTypes,
                    item.Id,
                    forceCurrentArtworkRefresh ? null : previousEntry?.Fingerprint,
                    artwork.Fingerprint);
            }

            if (index % 100 == 0)
            {
                progress?.Report(35 + (60.0 * index / Math.Max(1, items.Count)));
            }
        }

        foreach (var removed in previousState.Entries.Keys.Except(currentState.Entries.Keys, StringComparer.Ordinal))
        {
            var previous = previousState.Entries[removed];
            changed.Add(previous.ItemId);
            AddChangedImageTypes(
                changedImageTypes,
                previous.ItemId,
                previous.Fingerprint,
                currentFingerprint: null);
        }

        _byIdentity = identities;
        _byItemId = itemIds;
        _matchNewItem = item => MatchItem(item, byRelease, byCollection, byPublishedIdentity,
            byCollectionPart, byCustomCollectionKey, null);
        _allowedPaths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        Count = itemIds.Count;
        ChangedItemIds = changed;
        ChangedImageTypes = changedImageTypes.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyCollection<ImageType>)pair.Value.ToArray());
        // An ordinary remote-image request may build the index before the
        // scheduled task runs. Keep the diff pending until that task has queued
        // every affected item; otherwise the request silently consumes changes.
        _pendingState = currentState;
    }

    internal static IReadOnlyCollection<ImageType> GetChangedImageTypes(
        string? previousFingerprint,
        string? currentFingerprint)
    {
        var previous = SplitFingerprint(previousFingerprint);
        var current = SplitFingerprint(currentFingerprint);
        var result = new List<ImageType>(2);
        if (!previous.Poster.Equals(current.Poster, StringComparison.Ordinal))
        {
            result.Add(ImageType.Primary);
        }

        if (!previous.Logo.Equals(current.Logo, StringComparison.Ordinal))
        {
            result.Add(ImageType.Logo);
        }

        return result;
    }

    private static void AddChangedImageTypes(
        IDictionary<Guid, HashSet<ImageType>> changes,
        Guid itemId,
        string? previousFingerprint,
        string? currentFingerprint)
    {
        if (!changes.TryGetValue(itemId, out var imageTypes))
        {
            imageTypes = [];
            changes[itemId] = imageTypes;
        }

        imageTypes.UnionWith(GetChangedImageTypes(previousFingerprint, currentFingerprint));
    }

    private static (string Poster, string Logo) SplitFingerprint(string? fingerprint)
    {
        var parts = (fingerprint ?? string.Empty).Split('|', 2);
        return (
            parts.ElementAtOrDefault(0) ?? string.Empty,
            parts.ElementAtOrDefault(1) ?? string.Empty);
    }

    private static Dictionary<string, List<ArtworkManifestFile>> BuildReleaseLookup(
        IEnumerable<ArtworkManifestFile> files)
    {
        var result = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var releaseName in file.ReleaseNames)
            {
                AddLookup(result, ReleaseKey(releaseName), file);
            }
        }

        return result;
    }

    private static Dictionary<string, List<ArtworkManifestFile>> BuildCollectionLookup(
        IEnumerable<ArtworkManifestFile> files)
    {
        var result = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.Scope.Equals("collection", StringComparison.Ordinal)))
        {
            foreach (var releaseName in file.ReleaseNames)
            {
                AddLookup(result, CollectionKey(releaseName), file);
            }
        }

        return result;
    }

    private static void AddLookup(
        Dictionary<string, List<ArtworkManifestFile>> lookup,
        string key,
        ArtworkManifestFile file)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!lookup.TryGetValue(key, out var values))
        {
            values = [];
            lookup[key] = values;
        }

        values.Add(file);
    }

    private static ArtworkSet? MatchItem(
        BaseItem item,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> byRelease,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> byCollection,
        IReadOnlyDictionary<ArtworkIdentity, List<ArtworkManifestFile>> byPublishedIdentity,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> byCollectionPart,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> byCustomCollectionKey,
        string? previousCollectionKey)
    {
        if (item is BoxSet collection)
        {
            var collectionIdentityCandidate = TryGetIdentity(item, out var collectionIdentity)
                ? collectionIdentity
                : (ArtworkIdentity?)null;
            var memberIds = collection.GetLinkedChildren()
                .OfType<Movie>()
                .Select(movie => movie.GetProviderId(MetadataProvider.Tmdb))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!);
            return MatchCollectionCandidates(
                previousCollectionKey,
                collectionIdentityCandidate,
                memberIds,
                CandidateNames(item),
                byCollection,
                byPublishedIdentity,
                byCollectionPart,
                byCustomCollectionKey);
        }

        if (TryGetIdentity(item, out var identity)
            && byPublishedIdentity.TryGetValue(identity, out var identityMatches))
        {
            return BuildArtworkSet(identityMatches);
        }

        var scope = item switch
        {
            Movie => "item",
            Series => "series",
            Season => "season",
            _ => string.Empty,
        };
        var seasonNumber = item is Season ? item.IndexNumber : null;

        return MatchCandidates(
            CandidateNames(item).Select(ReleaseKey),
            byRelease,
            file => file.Scope.Equals(scope, StringComparison.Ordinal)
                && (scope != "season" || file.SeasonNumber == seasonNumber));
    }

    internal static ArtworkSet? MatchCollectionCandidates(
        string? previousCollectionKey,
        ArtworkIdentity? identity,
        IEnumerable<string> memberTmdbIds,
        IEnumerable<string> candidateNames,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> byCollection,
        IReadOnlyDictionary<ArtworkIdentity, List<ArtworkManifestFile>> byPublishedIdentity,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> byCollectionPart,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> byCustomCollectionKey)
    {
        // Membership is the strongest signal for official collections. It also
        // repairs BoxSets whose provider ID or localized name was overwritten by
        // another metadata provider.
        var memberArtwork = MatchCollectionMemberIds(memberTmdbIds, byCollectionPart);
        if (memberArtwork is not null)
        {
            return memberArtwork;
        }

        if (identity is { } collectionIdentity
            && byPublishedIdentity.TryGetValue(collectionIdentity, out var identityMatches))
        {
            var identityArtwork = BuildArtworkSet(identityMatches);
            if (identityArtwork is not null)
            {
                return identityArtwork;
            }
        }

        // Historical state is useful for explicitly mapped supercollections,
        // but must not override current membership or a repaired TMDB identity.
        if (!string.IsNullOrWhiteSpace(previousCollectionKey)
            && byCustomCollectionKey.TryGetValue(previousCollectionKey, out var stableMatches))
        {
            var stableArtwork = BuildArtworkSet(stableMatches);
            if (stableArtwork is not null)
            {
                return stableArtwork;
            }
        }

        // Names are deliberately the final fallback: they are localized and can
        // be duplicated by user-created supercollections.
        return MatchCandidates(
            candidateNames.Select(CollectionKey),
            byCollection,
            file => file.Scope.Equals("collection", StringComparison.Ordinal));
    }

    internal static ArtworkSet? MatchCandidates(
        IEnumerable<string> candidates,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> lookup,
        Func<ArtworkManifestFile, bool> predicate)
    {
        foreach (var candidate in candidates.Where(candidate => candidate.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (!lookup.TryGetValue(candidate, out var matches))
            {
                continue;
            }

            var filtered = matches.Where(predicate).ToList();
            var result = BuildArtworkSet(filtered);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    internal static Dictionary<ArtworkIdentity, List<ArtworkManifestFile>> BuildPublishedIdentityLookup(
        IEnumerable<ArtworkManifestFile> files)
    {
        var result = new Dictionary<ArtworkIdentity, List<ArtworkManifestFile>>();
        foreach (var file in files.Where(file => file.TmdbId is > 0))
        {
            var mediaType = file.Scope switch
            {
                "item" => "movie",
                "series" or "season" => "tv",
                "collection" => "collection",
                _ => string.Empty,
            };
            if (mediaType.Length == 0)
            {
                continue;
            }

            var identity = new ArtworkIdentity(
                mediaType,
                file.TmdbId!.Value.ToString(CultureInfo.InvariantCulture),
                file.Scope == "season" ? file.SeasonNumber : null);
            if (!result.TryGetValue(identity, out var values))
            {
                values = [];
                result[identity] = values;
            }

            values.Add(file);
        }

        return result;
    }

    private static Dictionary<string, List<ArtworkManifestFile>> BuildCollectionPartLookup(
        IEnumerable<ArtworkManifestFile> files)
    {
        var result = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.Scope == "collection"))
        {
            foreach (var partId in file.CollectionPartTmdbIds.Distinct())
            {
                AddLookup(result, partId.ToString(CultureInfo.InvariantCulture), file);
            }
        }

        return result;
    }

    internal static ArtworkSet? MatchCollectionMemberIds(
        IEnumerable<string> memberTmdbIds,
        IReadOnlyDictionary<string, List<ArtworkManifestFile>> byCollectionPart)
    {
        var memberIds = memberTmdbIds.Distinct(StringComparer.Ordinal).ToArray();
        if (memberIds.Length == 0)
        {
            return null;
        }

        var scores = new Dictionary<string, (int Score, List<ArtworkManifestFile> Files)>(StringComparer.Ordinal);
        foreach (var memberId in memberIds)
        {
            if (!byCollectionPart.TryGetValue(memberId, out var matches))
            {
                continue;
            }

            foreach (var group in matches.GroupBy(file =>
                         file.TmdbId?.ToString(CultureInfo.InvariantCulture)
                         ?? file.CollectionKey
                         ?? ParentPath(file.Path), StringComparer.Ordinal))
            {
                if (!scores.TryGetValue(group.Key, out var score))
                {
                    score = (0, []);
                }

                score.Score++;
                score.Files.AddRange(group);
                scores[group.Key] = score;
            }
        }

        if (scores.Count == 0)
        {
            return null;
        }

        var bestScore = scores.Values.Max(value => value.Score);
        var best = scores.Values.Where(value => value.Score == bestScore).ToArray();
        return best.Length == 1 ? BuildArtworkSet(best[0].Files) : null;
    }

    private static Dictionary<string, List<ArtworkManifestFile>> BuildCustomCollectionLookup(
        IEnumerable<ArtworkManifestFile> files)
    {
        var result = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.Scope == "collection" && file.CollectionKey is not null))
        {
            if (!result.TryGetValue(file.CollectionKey!, out var values))
            {
                values = [];
                result[file.CollectionKey!] = values;
            }

            values.Add(file);
        }

        return result;
    }

    private static ArtworkSet? BuildArtworkSet(IEnumerable<ArtworkManifestFile> matches)
    {
        var groups = matches
            .Where(file => !Path.GetFileName(file.Path).Contains("(alt)", StringComparison.OrdinalIgnoreCase))
            .GroupBy(file => ParentPath(file.Path), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0)
        {
            return null;
        }

        var selected = new ArtworkSet();
        var hasSelectedDirectory = false;
        foreach (var group in groups)
        {
            var candidate = new ArtworkSet();
            foreach (var file in group.OrderByDescending(ArtworkFilePriority).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (IsPoster(file.Path))
                {
                    candidate.Poster ??= file;
                }
                else if (IsLogo(file.Path))
                {
                    candidate.Logo ??= file;
                }
            }

            if (candidate.Poster is null && candidate.Logo is null)
            {
                continue;
            }

            if (hasSelectedDirectory
                && HasConflictingArtwork(selected.Poster, candidate.Poster))
            {
                return null;
            }

            if (hasSelectedDirectory
                && HasConflictingArtwork(selected.Logo, candidate.Logo))
            {
                return null;
            }

            selected.Poster ??= candidate.Poster;
            selected.Logo ??= candidate.Logo;
            hasSelectedDirectory = true;
        }

        return hasSelectedDirectory ? selected : null;
    }

    private static int ArtworkFilePriority(ArtworkManifestFile file)
    {
        var name = Path.GetFileName(file.Path);
        return name.Contains("-poster.", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-clearlogo.", StringComparison.OrdinalIgnoreCase)
            ? 2
            : 1;
    }

    private static bool HasConflictingArtwork(
        ArtworkManifestFile? selected,
        ArtworkManifestFile? candidate) =>
        selected is not null
        && candidate is not null
        && !selected.Sha256.Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> CandidateNames(BaseItem item)
    {
        if (item is Season season)
        {
            var series = season.Series;
            if (series is not null)
            {
                foreach (var candidate in CandidateNames(series))
                {
                    yield return candidate;
                }
            }

            yield break;
        }

        if (!string.IsNullOrWhiteSpace(item.Path))
        {
            if (item is Movie)
            {
                yield return Path.GetFileNameWithoutExtension(item.Path);
            }

            var folder = item is Movie
                ? Path.GetFileName(Path.GetDirectoryName(item.Path))
                : new DirectoryInfo(item.Path).Name;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                yield return folder;
            }
        }

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            yield return item.Name;
            if (item.ProductionYear.HasValue)
            {
                yield return $"{item.Name} ({item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture)})";
            }
        }
    }

    private static bool TryGetIdentity(BaseItem item, out ArtworkIdentity identity)
    {
        BaseItem identityItem = item;
        var mediaType = item switch
        {
            Movie => "movie",
            Series => "tv",
            Season => "tv",
            BoxSet => "collection",
            _ => string.Empty,
        };

        if (item is Season season && season.Series is not null)
        {
            identityItem = season.Series;
        }

        var tmdbId = identityItem.GetProviderId(MetadataProvider.Tmdb);
        if (mediaType.Length == 0 || string.IsNullOrWhiteSpace(tmdbId))
        {
            identity = default;
            return false;
        }

        identity = new ArtworkIdentity(mediaType, tmdbId, item is Season ? item.IndexNumber : null);
        return true;
    }

    private async Task<T> ReadJsonAsync<T>(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await _httpClientFactory
            .CreateClient("Default")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException($"Ответ {uri} превышает допустимый размер.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException($"Ответ {uri} превышает допустимый размер.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(buffer, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Ответ {uri} пуст.");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        var version = typeof(ArtworkIndex).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        request.Headers.UserAgent.ParseAdd($"Cowabunga-Custom-Artwork/{version}");
        return request;
    }

    internal static void ValidateManifest(ArtworkManifest manifest, string expectedRevision)
    {
        if (manifest.SchemaVersion is not (1 or 2)
            || !manifest.Revision.Equals(expectedRevision, StringComparison.OrdinalIgnoreCase)
            || !IsSha256(manifest.Revision)
            || manifest.Files.Count > MaxManifestFiles)
        {
            throw new InvalidDataException("Манифест artwork имеет неподдерживаемый формат или ревизию.");
        }

        foreach (var file in manifest.Files)
        {
            if (!IsSafeRelativePath(file.Path)
                || !IsSha256(file.Sha256)
                || file.Size is <= 0 or > MaxArtworkBytes
                || file.ReleaseNames.Count == 0
                || file.ReleaseNames.Count > 50
                || file.ReleaseNames.Any(string.IsNullOrWhiteSpace)
                || file.Scope is not ("item" or "series" or "season" or "collection")
                || (file.Scope == "season" && file.SeasonNumber is null or < 0)
                || (file.TmdbId is <= 0)
                || file.CollectionPartTmdbIds.Any(id => id <= 0)
                || (file.Scope != "collection" && file.CollectionPartTmdbIds.Count > 0)
                || (file.CollectionKey is not null && !CustomCollectionKeyRegex().IsMatch(file.CollectionKey))
                || (manifest.SchemaVersion == 2
                    && file.Scope == "collection"
                    && ((file.TmdbId is not null) == (file.CollectionKey is not null)))
                || (file.Scope != "collection" && file.CollectionKey is not null))
            {
                throw new InvalidDataException($"Некорректная запись artwork: {file.Path}");
            }
        }
    }

    private static ArtworkManifest? LoadCachedManifest()
    {
        if (!File.Exists(ManifestCachePath))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ArtworkManifest>(File.ReadAllText(ManifestCachePath), JsonOptions);
            if (manifest is null)
            {
                return null;
            }

            ValidateManifest(manifest, manifest.Revision);
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static ArtworkState LoadState()
    {
        if (!File.Exists(StatePath))
        {
            return new ArtworkState();
        }

        try
        {
            return JsonSerializer.Deserialize<ArtworkState>(File.ReadAllText(StatePath), JsonOptions)
                ?? new ArtworkState();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new ArtworkState();
        }
    }

    private static void SaveJsonAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static string ReleaseKey(string name) =>
        QualityTagRegex().Replace(name, string.Empty).Trim().ToLowerInvariant();

    internal static string CollectionKey(string name)
    {
        var normalized = name.ToLowerInvariant();
        var latin = normalized.Count(character => character is >= 'a' and <= 'z');
        var cyrillic = normalized.Count(character => character is >= 'а' and <= 'я' or 'ё');
        if (latin > cyrillic)
        {
            normalized = normalized
                .Replace('а', 'a').Replace('в', 'b').Replace('с', 'c').Replace('е', 'e')
                .Replace('к', 'k').Replace('м', 'm').Replace('н', 'h').Replace('о', 'o')
                .Replace('р', 'p').Replace('т', 't').Replace('х', 'x');
        }

        return NonCollectionCharacterRegex().Replace(normalized, string.Empty);
    }

    private static bool IsPoster(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("poster.jpg", StringComparison.OrdinalIgnoreCase)
            || name.Equals("poster.png", StringComparison.OrdinalIgnoreCase)
            || name.Equals("folder.jpg", StringComparison.OrdinalIgnoreCase)
            || name.Equals("folder.png", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-poster.jpg", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-poster.png", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLogo(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("clearlogo.png", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-clearlogo.png", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-сlearlogo.png", StringComparison.OrdinalIgnoreCase);
    }

    private static string ParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator > 0 ? path[..separator] : string.Empty;
    }

    internal static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/')
        && !path.Contains('\\')
        && path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static bool IsSha256(string value) => Sha256Regex().IsMatch(value);

    private static Uri BuildUri(Uri baseUri, string relativePath)
    {
        var escaped = string.Join(
            '/',
            NormalizeRelative(relativePath)
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        return new Uri(baseUri, escaped);
    }

    private static string JoinRelative(string left, string right) =>
        $"{NormalizeRelative(left)}/{NormalizeRelative(right)}".Trim('/');

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').Trim('/');

    internal static bool TryGetRelativeArtworkPath(Uri candidate, Uri root, out string path)
    {
        if (!candidate.Scheme.Equals(root.Scheme, StringComparison.OrdinalIgnoreCase)
            || !candidate.Host.Equals(root.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != root.Port
            || candidate.Query.Length > 0
            || candidate.Fragment.Length > 0)
        {
            path = string.Empty;
            return false;
        }

        var rootPath = root.AbsolutePath.TrimEnd('/') + "/";
        if (!candidate.AbsolutePath.StartsWith(rootPath, StringComparison.Ordinal))
        {
            path = string.Empty;
            return false;
        }

        try
        {
            path = string.Join(
                '/',
                candidate.AbsolutePath[rootPath.Length..]
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.UnescapeDataString));
            return IsSafeRelativePath(path);
        }
        catch (UriFormatException)
        {
            path = string.Empty;
            return false;
        }
    }

    [GeneratedRegex(@"\s*\[[^\]]*\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex QualityTagRegex();

    [GeneratedRegex(@"[^0-9a-zа-я]", RegexOptions.CultureInvariant)]
    private static partial Regex NonCollectionCharacterRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"^[a-z0-9]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex CustomCollectionKeyRegex();
}
