using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.CustomArtwork;

internal sealed class ArtworkRevision
{
    [JsonPropertyName("revision")]
    public string Revision { get; set; } = string.Empty;
}

internal sealed class ArtworkManifest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("revision")]
    public string Revision { get; set; } = string.Empty;

    [JsonPropertyName("generated_at")]
    public string GeneratedAt { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<ArtworkManifestFile> Files { get; set; } = [];
}

internal sealed class ArtworkManifestFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("modified_at")]
    public string ModifiedAt { get; set; } = string.Empty;

    [JsonPropertyName("release_names")]
    public List<string> ReleaseNames { get; set; } = [];

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; set; }

    [JsonPropertyName("collection_key")]
    public string? CollectionKey { get; set; }

    [JsonPropertyName("collection_part_tmdb_ids")]
    public List<int> CollectionPartTmdbIds { get; set; } = [];
}

internal sealed class ArtworkSet
{
    public ArtworkManifestFile? Poster { get; set; }

    public ArtworkManifestFile? Logo { get; set; }

    public string Fingerprint => $"{Poster?.Sha256}|{Logo?.Sha256}";

    public string? CollectionKey => Poster?.CollectionKey ?? Logo?.CollectionKey;
}

internal readonly record struct ArtworkIdentity(string MediaType, string TmdbId, int? SeasonNumber)
{
    public override string ToString() => $"{MediaType}:{TmdbId}:{SeasonNumber?.ToString() ?? "-"}";
}

internal sealed class ArtworkState
{
    public int SchemaVersion { get; set; }

    public string Revision { get; set; } = string.Empty;

    public Dictionary<string, ArtworkStateEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class ArtworkStateEntry
{
    public Guid ItemId { get; set; }

    public string Fingerprint { get; set; } = string.Empty;

    public string? CollectionKey { get; set; }
}

internal sealed class ManagedMediaState
{
    public string StorageMode { get; set; } = Configuration.PluginConfiguration.JellyfinStorage;

    public bool Posters { get; set; } = true;

    public bool Logos { get; set; } = true;

    public bool OverwriteExistingMediaFiles { get; set; }

    public Dictionary<string, ManagedMediaFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ManagedMediaFile
{
    public Guid ItemId { get; set; }

    public string Role { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class CollectionArtworkRetryState
{
    public int SchemaVersion { get; set; }

    public Dictionary<Guid, CollectionArtworkRetryEntry> Entries { get; set; } = [];
}

internal sealed class CollectionArtworkRetryEntry
{
    public string Fingerprint { get; set; } = string.Empty;

    public int Attempts { get; set; }

    public DateTime NextAttemptUtc { get; set; }

    public string PosterFileSignature { get; set; } = string.Empty;

    public string LogoFileSignature { get; set; } = string.Empty;
}

internal sealed record ArtworkRefreshRequest(
    Guid ItemId,
    IReadOnlyCollection<MediaBrowser.Model.Entities.ImageType> ImageTypes);
