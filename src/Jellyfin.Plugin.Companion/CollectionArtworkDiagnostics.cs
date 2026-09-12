using System.Security.Cryptography;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CustomArtwork;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Companion;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("CowabungaCompanion/Artwork")]
public sealed class CollectionArtworkDiagnostics(ILibraryManager library, ArtworkIndex index, ArtworkCoordinator coordinator) : ControllerBase
{
    [HttpGet("Collections")]
    public async Task<ActionResult> InspectCollections([FromQuery] string? search, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Length > 100) return BadRequest("Введите название, TMDB ID или ID коллекции.");
        search = search.Trim();
        var collections = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true })
            .OfType<BoxSet>().Where(item => (item.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (item.OriginalTitle?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || string.Equals(item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb), search, StringComparison.Ordinal)
                || Guid.TryParse(search, out var id) && item.Id == id).Take(10).ToArray();
        var config = CompanionPlugin.GetConfiguration();
        var results = new List<object>();
        foreach (var item in collections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artwork = index.Find(item);
            var roles = new List<object>();
            foreach (var type in new[] { ImageType.Primary, ImageType.Logo })
            {
                var source = type == ImageType.Primary ? artwork?.Poster : artwork?.Logo;
                var path = item.GetImageInfo(type, 0)?.Path;
                var current = await ReadHash(path, cancellationToken).ConfigureAwait(false);
                var state = coordinator.Inspect(item.Id, type);
                var managed = state.Expected is { } expected && string.Equals(expected.Sha256, current.Hash, StringComparison.OrdinalIgnoreCase)
                    ? expected : state.Managed;
                var aliases = new List<object>();
                var protectedAlias = false;
                try
                {
                    foreach (var alias in CollectionArtworkStorage.Aliases(item, type))
                    {
                        var hash = await ReadHash(alias, cancellationToken).ConfigureAwait(false);
                        var protect = hash.Error is not null || !ArtworkCoordinator.MayReplace(hash.Hash, managed?.Sha256, config.ReplaceExistingImages);
                        protectedAlias |= protect;
                        aliases.Add(new { Path = alias, hash.Hash, hash.Error, Protected = protect });
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { protectedAlias = true; aliases.Add(new { Error = error.GetType().Name }); }
                var status = !LibraryPolicy.Enabled(CompanionModule.Artwork) ? "Модуль Custom Artwork выключен или заблокирован старым плагином"
                    : !config.CollectionArtworkEnabled ? "Кастомные изображения коллекций выключены"
                    : item.IsLocked ? "Метаданные коллекции заблокированы"
                    : !(type == ImageType.Primary ? config.Posters : config.Logos) ? "Этот тип изображений выключен"
                    : current.Error is not null ? "Ошибка чтения текущего изображения"
                    : source is not null && string.Equals(current.Hash, source.Sha256, StringComparison.OrdinalIgnoreCase) ? "Текущий файл совпадает с облаком"
                    : !ArtworkCoordinator.MayReplace(current.Hash, managed?.Sha256, config.ReplaceExistingImages) ? "Текущее изображение защищено от замены"
                    : protectedAlias ? "В папке коллекции есть защищённый или недоступный файл"
                    : !index.RemoteArtworkAvailable ? "Облачный индекс недоступен"
                    : source is null ? "В загруженном индексе не найдено соответствующее изображение"
                    : state.Pending is null ? "Облачное изображение найдено, но задания в очереди нет"
                    : state.Pending.Attempts > 0 ? "Задание ожидает повторной попытки"
                    : "Облачное изображение найдено, задание ожидает обработки";
                roles.Add(new { Type = type.ToString(), Status = status, CurrentPath = path, CurrentHash = current.Hash,
                    ReadError = current.Error, CloudPath = source?.Path, CloudHash = source?.Sha256,
                    ManagedHash = state.Managed?.Sha256, QueuePosition = state.Position, state.Pending, Aliases = aliases });
            }
            results.Add(new { item.Name, item.Id, TmdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb), item.Path, item.IsLocked,
                NativeCollectionFolder = CollectionArtworkStorage.UsesNativeFolder(item), InLibraryMap = index.Matches.ContainsKey(item.Id), Images = roles });
        }
        return Ok(new { Version = typeof(CollectionArtworkDiagnostics).Assembly.GetName().Version?.ToString(),
            CheckedUtc = DateTime.UtcNow, index.BuiltUtc, index.Revision, index.RemoteArtworkAvailable, PendingImages = coordinator.PendingCount,
            config.CollectionArtworkEnabled, config.ReplaceExistingImages, Collections = results });
    }

    private static async Task<(string? Hash, string? Error)> ReadHash(string? path, CancellationToken token)
    {
        if (string.IsNullOrEmpty(path)) return (null, null);
        try
        {
            await using var stream = System.IO.File.OpenRead(path);
            return (Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), null);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return (null, null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return (null, error.GetType().Name); }
    }
}
