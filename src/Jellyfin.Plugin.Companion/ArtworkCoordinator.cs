using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.Companion.Configuration;
using Jellyfin.Plugin.CustomArtwork;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Companion;

// All Companion background image writes, including local files, share this gate.
internal static class ArtworkOperations
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}

public sealed class ArtworkCoordinator(
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IFileSystem fileSystem,
    ArtworkIndex index,
    ILogger<ArtworkCoordinator> logger) : BackgroundService
{
    private readonly object _sync = new();
    private ArtworkQueueState _state = new();
    private bool _loaded;
    private readonly Dictionary<string, HashSet<string>> _candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _expectedHashes = new(StringComparer.Ordinal);
    private string StatePath => Path.Combine(CompanionPlugin.Instance!.DataFolderPath, "companion-artwork.v1.json");
    public int PendingCount { get { lock (_sync) { Load(); return _state.Pending.Count; } } }

    internal void RegisterCandidates(BaseItem item, IEnumerable<RemoteImageInfo> candidates)
    {
        lock (_sync)
        {
            if (_candidates.Count > 100_000) { _candidates.Clear(); _expectedHashes.Clear(); }
            foreach (var image in candidates)
            {
                if (!_candidates.TryGetValue(image.Url, out var items)) _candidates[image.Url] = items = [];
                items.Add($"{item.Id:N}/{image.Type}");
            }
        }
    }

    internal void RegisterHash(string url, string hash) { lock (_sync) _expectedHashes[url] = hash; }

    internal void ImportLegacyOwnership(string path)
    {
        if (!File.Exists(path)) return;
        ArtworkState? legacy;
        try { legacy = JsonSerializer.Deserialize<ArtworkState>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return; }
        if (legacy is null) return;
        lock (_sync)
        {
            Load();
            foreach (var entry in legacy.Entries.Values)
            {
                var roles = entry.Fingerprint.Split('|');
                for (var i = 0; i < Math.Min(roles.Length, 2); i++)
                {
                    if (roles[i].Length != 64 || !roles[i].All(Uri.IsHexDigit)) continue;
                    var key = $"{entry.ItemId:N}/{(i == 0 ? ImageType.Primary : ImageType.Logo)}";
                    if (!_state.Managed.ContainsKey(key)) _state.Expected.TryAdd(key,
                        new ManagedArtwork { Sha256 = roles[i].ToUpperInvariant(), Source = CompanionPlugin.ArtworkProviderName });
                }
            }
            Save();
        }
    }

    // Native Jellyfin scans also use our providers. Remember the actual downloaded
    // bytes so future updates can distinguish these images from manual replacements.
    internal async Task<HttpResponseMessage> TrackDownloadAsync(string url, string source, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            response.EnsureSuccessStatusCode();
            const int limit = 100 * 1024 * 1024;
            await response.Content.LoadIntoBufferAsync(limit, cancellationToken).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            lock (_sync)
            {
                Load();
                if (_expectedHashes.TryGetValue(url, out var expectedHash) && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Custom artwork hash mismatch.");
                if (_candidates.TryGetValue(url, out var items))
                {
                    foreach (var key in items) _state.Expected[key] = new ManagedArtwork { Sha256 = hash, Source = source };
                    _candidates.Remove(url);
                    Save();
                }
            }
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    public bool Queue(Guid itemId, IEnumerable<ImageType> types, CompanionModule module)
        => QueueMany([(itemId, types)], module) > 0;

    internal int QueueMany(IEnumerable<(Guid ItemId, IEnumerable<ImageType> Types)> requests, CompanionModule module)
    {
        var accepted = requests.Where(request => LibraryPolicy.Allows(module, libraryManager.GetItemById(request.ItemId))).ToArray();
        var changed = false;
        lock (_sync)
        {
            Load();
            foreach (var (itemId, types) in accepted)
            foreach (var type in types.Where(type => type is ImageType.Primary or ImageType.Logo).Distinct())
            {
                var key = $"{itemId:N}/{type}/{module}";
                changed |= _state.Pending.TryAdd(key, new PendingArtwork { ItemId = itemId, Type = type, Module = module });
            }
            if (changed) Save();
        }
        return accepted.Length;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            KeyValuePair<string, PendingArtwork>[] due;
            lock (_sync)
            {
                Load();
                due = _state.Pending.Where(pair => pair.Value.NextAttemptUtc <= DateTime.UtcNow).Take(50).ToArray();
            }
            foreach (var (key, pending) in due)
            {
                stoppingToken.ThrowIfCancellationRequested();
                var success = false;
                try
                {
                    await ArtworkOperations.Gate.WaitAsync(stoppingToken).ConfigureAwait(false);
                    try { success = await ApplyAsync(pending, stoppingToken).ConfigureAwait(false); }
                    finally { ArtworkOperations.Gate.Release(); }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    // Do not log remote URLs, which may contain credentials.
                    logger.LogWarning("Companion artwork update failed for {ItemId}/{Type}: {ErrorType}",
                        pending.ItemId, pending.Type, error.GetType().Name);
                }
                lock (_sync)
                {
                    if (success) _state.Pending.Remove(key);
                    else
                    {
                        pending.Attempts++;
                        pending.NextAttemptUtc = DateTime.UtcNow + RetryDelay(pending.Attempts);
                    }
                    Save();
                }
            }
        }
    }

    internal static TimeSpan RetryDelay(int attempts) => TimeSpan.FromMinutes(Math.Min(1440, Math.Pow(2, Math.Min(11, Math.Max(0, attempts - 1)))));

    internal async Task<bool> ApplyAsync(PendingArtwork request, CancellationToken cancellationToken)
    {
        var item = libraryManager.GetItemById(request.ItemId);
        if (!LibraryPolicy.Allows(request.Module, item)) return true;
        if (item!.IsLocked) return true;
        var config = CompanionPlugin.GetConfiguration();
        if (request.Module == CompanionModule.Metadata && !config.MetadataImagesEnabled) return true;
        var type = request.Type;
        if (request.Module == CompanionModule.Artwork && !(type == ImageType.Primary ? config.Posters : config.Logos)) return true;
        var key = $"{item!.Id:N}/{type}";
        var existingPath = item.GetImageInfo(type, 0)?.Path;
        var existingHash = await HashAsync(existingPath, cancellationToken).ConfigureAwait(false);
        ManagedArtwork? managed;
        lock (_sync)
        {
            Load();
            if (_state.Expected.TryGetValue(key, out var expected) && existingHash == expected.Sha256)
            {
                _state.Managed[key] = expected;
                _state.Expected.Remove(key);
                Save();
            }
            _state.Managed.TryGetValue(key, out managed);
        }
        if (!MayReplace(existingHash, managed?.Sha256, config.ReplaceExistingImages)) return true;
        if (existingPath is not null && IsMediaFile(item, existingPath) && !config.OverwriteExistingMediaFiles) return true;

        if (LibraryPolicy.Allows(CompanionModule.Artwork, item)
            && (type == ImageType.Primary ? config.Posters : config.Logos))
        {
            if (index.IsStale(config.RefreshIntervalMinutes))
                await index.BuildAsync(null, cancellationToken).ConfigureAwait(false);
            // A failed index read is not evidence that the custom image was removed.
            if (!index.RemoteArtworkAvailable) return false;
            var artwork = index.Find(item);
            var source = type == ImageType.Primary ? artwork?.Poster : artwork?.Logo;
            if (source is not null)
            {
                if (string.Equals(existingHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // Also repair references left only in memory by older releases.
                    await libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                    Remember(key, source.Sha256, CompanionPlugin.ArtworkProviderName);
                    return true;
                }
                // In media-folder mode the file writer owns movies, series and seasons.
                if (config.StorageMode == PluginConfiguration.MediaFolderStorage && item is not BoxSet) return true;
                using var response = await index.GetArtworkResponseAsync(index.GetArtworkUri(source.Path).AbsoluteUri, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await SaveResponseAsync(item, type, response, source.Size, source.Sha256, request.Module, cancellationToken).ConfigureAwait(false);
                if (!LibraryPolicy.Allows(request.Module, item)) return true;
                var savedHash = await HashAsync(item.GetImageInfo(type, 0)?.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(savedHash, source.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                Remember(key, savedHash!, CompanionPlugin.ArtworkProviderName);
                return true;
            }
        }

        // The normal provider list respects the library's enabled sources and order.
        var providers = providerManager.GetImageProviders(item, new ImageRefreshOptions(new DirectoryService(fileSystem)))
            .OfType<IRemoteImageProvider>()
            .Where(provider => provider.Name != CompanionPlugin.ArtworkProviderName)
            .OrderBy(provider => provider.Name == CompanionPlugin.MetadataImageProviderName ? 0 : 1);
        foreach (var provider in providers)
        {
            if (!provider.Supports(item)) continue;
            var images = await provider.GetImages(item, cancellationToken).ConfigureAwait(false);
            var image = images.FirstOrDefault(image => image.Type == type);
            if (image is null) continue;
            using var response = await provider.GetImageResponse(image.Url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await SaveResponseAsync(item, type, response, null, null, request.Module, cancellationToken).ConfigureAwait(false);
            if (!LibraryPolicy.Allows(request.Module, item)) return true;
            var savedHash = await HashAsync(item.GetImageInfo(type, 0)?.Path, cancellationToken).ConfigureAwait(false);
            if (savedHash is null) return false;
            Remember(key, savedHash, provider.Name);
            return true;
        }
        // Keep existing artwork when no source can currently supply a replacement.
        return false;
    }

    internal static bool MayReplace(string? currentHash, string? managedHash, bool overwrite) =>
        currentHash is null || overwrite || managedHash is not null && currentHash.Equals(managedHash, StringComparison.OrdinalIgnoreCase);

    private static bool IsMediaFile(BaseItem item, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(item.Path) || item is BoxSet) return false;
        var directory = item is Movie ? Path.GetDirectoryName(item.Path) : item.Path;
        return directory is not null && LibraryPolicy.ContainsPath(directory, imagePath);
    }

    private async Task SaveResponseAsync(BaseItem item, ImageType type, HttpResponseMessage response, long? expectedSize,
        string? expectedHash, CompanionModule module, CancellationToken cancellationToken)
    {
        const long limit = 100 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit || expectedSize > limit) throw new InvalidDataException("Image too large.");
        var directory = Path.Combine(CompanionPlugin.Instance!.DataFolderPath, "downloads");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > limit) throw new InvalidDataException("Image too large.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (expectedSize is not null && total != expectedSize) throw new InvalidDataException("Image size mismatch.");
            }
            var hash = await HashAsync(temporary, cancellationToken).ConfigureAwait(false);
            if (expectedHash is not null && !expectedHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Image hash mismatch.");
            if (!LibraryPolicy.Allows(module, item)) return;
            await providerManager.SaveImage(item, temporary, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg",
                type, 0, false, cancellationToken).ConfigureAwait(false);
            // SaveImage changes the file and in-memory item only. Persist the new
            // image reference, just as Jellyfin's manual image download does.
            await libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<string?> HashAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
    private void Remember(string key, string hash, string source)
    {
        lock (_sync) { _state.Managed[key] = new ManagedArtwork { Sha256 = hash, Source = source }; Save(); }
    }
    private void Load()
    {
        if (_loaded) return;
        if (File.Exists(StatePath))
        {
            try { _state = JsonSerializer.Deserialize<ArtworkQueueState>(File.ReadAllText(StatePath)) ?? new(); }
            catch (JsonException) { logger.LogWarning("Companion artwork state could not be read; existing images will be protected."); }
        }
        _loaded = true;
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
        File.Move(temporary, StatePath, true);
    }
}

internal sealed class ArtworkQueueState
{
    public Dictionary<string, PendingArtwork> Pending { get; set; } = [];
    public Dictionary<string, ManagedArtwork> Managed { get; set; } = [];
    public Dictionary<string, ManagedArtwork> Expected { get; set; } = [];
}
internal sealed class PendingArtwork
{
    public Guid ItemId { get; set; }
    public ImageType Type { get; set; }
    public CompanionModule Module { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptUtc { get; set; }
}
internal sealed class ManagedArtwork
{
    public string Sha256 { get; set; } = "";
    public string Source { get; set; } = "";
}
