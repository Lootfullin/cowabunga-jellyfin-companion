namespace Jellyfin.Plugin.CustomArtwork.Tests;

using System.Security.Cryptography;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

public sealed class ArtworkIndexTests
{
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("Sherlock (2010) [S01 UHD DR]", "sherlock (2010)")]
    [InlineData("  Dr. No (1962)  ", "dr. no (1962)")]
    public void ReleaseKey_RemovesQualitySuffix(string value, string expected)
    {
        Assert.Equal(expected, ArtworkIndex.ReleaseKey(value));
    }

    [Fact]
    public void CollectionKey_IgnoresPunctuationAndCase()
    {
        Assert.Equal("starwarscollection", ArtworkIndex.CollectionKey("Star Wars: Collection"));
    }

    [Theory]
    [InlineData("Movies/Test/poster.jpg", true)]
    [InlineData("Shows/Test/Season 01/poster.jpg", true)]
    [InlineData("../secret.jpg", false)]
    [InlineData("Movies/../secret.jpg", false)]
    [InlineData("/absolute/poster.jpg", false)]
    [InlineData("Movies\\Test\\poster.jpg", false)]
    public void RelativePathValidation_RejectsUnsafePaths(string path, bool expected)
    {
        Assert.Equal(expected, ArtworkIndex.IsSafeRelativePath(path));
    }

    [Theory]
    [InlineData("https://cloud.example/webdav/Media/Movies/Test/poster.jpg", true, "Movies/Test/poster.jpg")]
    [InlineData("https://cloud.example/webdav/Media/%2e%2e/secret.jpg", false, "")]
    [InlineData("https://other.example/webdav/Media/Movies/Test/poster.jpg", false, "")]
    [InlineData("https://cloud.example/webdav/Media/Movies/Test/poster.jpg?download=other", false, "")]
    public void ArtworkUrlValidation_StaysInsideConfiguredMediaRoot(
        string url,
        bool expected,
        string expectedPath)
    {
        var result = ArtworkIndex.TryGetRelativeArtworkPath(
            new Uri(url),
            new Uri("https://cloud.example/webdav/Media"),
            out var path);

        Assert.Equal(expected, result);
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void ArtworkUri_UsesFixedCowabungaEndpoint()
    {
        var index = new ArtworkIndex(null!, null!, null!);

        var result = index.GetArtworkUri("Shows/Sherlock/Season 01/poster.jpg");

        Assert.Equal(
            "https://artwork.lootfullin.netcraze.pro/Media/Shows/Sherlock/Season%2001/poster.jpg",
            result.AbsoluteUri);
    }

    [Fact]
    public void ValidateManifest_AcceptsPublisherSchema()
    {
        var manifest = CreateManifest("Movies/Test/poster.jpg");

        ArtworkIndex.ValidateManifest(manifest, Revision);
    }

    [Fact]
    public void CollectionKey_RepairsCyrillicLookalikeInsideLatinName()
    {
        Assert.Equal("beetlejuicecollection", ArtworkIndex.CollectionKey("Beetlejuice Сollection"));
    }

    [Fact]
    public void ValidateManifest_AcceptsSchemaTwoCollectionWithTmdbId()
    {
        var file = CreateFile("Collections/Star Wars/poster.jpg", "collection");
        file.TmdbId = 10;
        var manifest = new ArtworkManifest
        {
            SchemaVersion = 2,
            Revision = Revision,
            GeneratedAt = "2026-08-01T00:00:00Z",
            Files = [file],
        };

        ArtworkIndex.ValidateManifest(manifest, Revision);
    }

    [Fact]
    public void ValidateManifest_AllowsSchemaTwoCustomCollectionWithoutTmdbId()
    {
        var file = CreateFile("Collections/Unknown/poster.jpg", "collection");
        file.CollectionKey = "phase16";
        var manifest = new ArtworkManifest
        {
            SchemaVersion = 2,
            Revision = Revision,
            GeneratedAt = "2026-08-01T00:00:00Z",
            Files = [file],
        };

        ArtworkIndex.ValidateManifest(manifest, Revision);
    }


    [Fact]
    public void ValidateManifest_RejectsSchemaTwoCollectionWithoutIdentity()
    {
        var manifest = new ArtworkManifest
        {
            SchemaVersion = 2,
            Revision = Revision,
            GeneratedAt = "2026-08-01T00:00:00Z",
            Files = [CreateFile("Collections/Unknown/poster.jpg", "collection")],
        };

        Assert.Throws<InvalidDataException>(() => ArtworkIndex.ValidateManifest(manifest, Revision));
    }

    [Fact]
    public void ValidateManifest_RejectsTraversal()
    {
        var manifest = CreateManifest("../poster.jpg");

        Assert.Throws<InvalidDataException>(() => ArtworkIndex.ValidateManifest(manifest, Revision));
    }

    [Fact]
    public void MatchCandidates_JoinsPosterAndLogoFromOneDirectory()
    {
        var poster = CreateFile("Shows/Sherlock/poster.jpg", "series");
        var logo = CreateFile("Shows/Sherlock/clearlogo.png", "series");
        var lookup = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal)
        {
            ["sherlock"] = [poster, logo],
        };

        var result = ArtworkIndex.MatchCandidates(
            ["sherlock"],
            lookup,
            file => file.Scope == "series");

        Assert.NotNull(result);
        Assert.Same(poster, result.Poster);
        Assert.Same(logo, result.Logo);
    }

    [Fact]
    public void MatchCandidates_SkipsAmbiguousDirectories()
    {
        var first = CreateFile("Movies/One/poster.jpg", "item");
        var second = CreateFile("Movies/Two/poster.jpg", "item");
        second.Sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var lookup = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal)
        {
            ["example"] = [first, second],
        };

        var result = ArtworkIndex.MatchCandidates(
            ["example"],
            lookup,
            file => file.Scope == "item");

        Assert.Null(result);
    }

    [Fact]
    public void MatchCandidates_AcceptsContentIdenticalCopiesInNestedCollectionDirectories()
    {
        var rootPoster = CreateFile("Collections/Iron Man Collection/poster.jpg", "collection");
        var rootLogo = CreateFile("Collections/Iron Man Collection/clearlogo.png", "collection");
        var nestedPoster = CreateFile("Collections/Marvel Collection/Iron Man Collection/poster.jpg", "collection");
        var nestedLogo = CreateFile("Collections/Marvel Collection/Iron Man Collection/clearlogo.png", "collection");
        var lookup = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal)
        {
            ["ironmancollection"] = [rootPoster, rootLogo, nestedPoster, nestedLogo],
        };

        var result = ArtworkIndex.MatchCandidates(
            ["ironmancollection"],
            lookup,
            file => file.Scope == "collection");

        Assert.NotNull(result);
        Assert.Same(rootPoster, result.Poster);
        Assert.Same(rootLogo, result.Logo);
    }

    [Theory]
    [InlineData("item", 1893, null, "movie")]
    [InlineData("series", 19885, null, "tv")]
    [InlineData("season", 19885, 1, "tv")]
    public void PublishedIdentityLookup_IncludesAllSupportedScopes(
        string scope,
        int tmdbId,
        int? seasonNumber,
        string mediaType)
    {
        var file = CreateFile($"Media/{scope}/poster.jpg", scope);
        file.TmdbId = tmdbId;
        file.SeasonNumber = seasonNumber;

        var lookup = ArtworkIndex.BuildPublishedIdentityLookup([file]);

        Assert.Same(file, Assert.Single(lookup[new ArtworkIdentity(mediaType, tmdbId.ToString(), seasonNumber)]));
    }

    [Fact]
    public void ValidateManifest_AcceptsMovieWithTmdbId()
    {
        var file = CreateFile("Movies/Example/poster.jpg", "item");
        file.TmdbId = 1893;
        var manifest = new ArtworkManifest
        {
            SchemaVersion = 2,
            Revision = Revision,
            GeneratedAt = "2026-08-01T00:00:00Z",
            Files = [file],
        };

        ArtworkIndex.ValidateManifest(manifest, Revision);
    }

    [Fact]
    public void MatchCandidates_MergesNonConflictingArtworkAcrossDuplicateDirectories()
    {
        var rootPoster = CreateFile("Collections/Iron Man Collection/poster.jpg", "collection");
        var nestedPoster = CreateFile("Collections/Marvel Collection/Iron Man Collection/poster.jpg", "collection");
        var nestedLogo = CreateFile("Collections/Marvel Collection/Iron Man Collection/clearlogo.png", "collection");
        var lookup = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal)
        {
            ["ironmancollection"] = [rootPoster, nestedPoster, nestedLogo],
        };

        var result = ArtworkIndex.MatchCandidates(
            ["ironmancollection"],
            lookup,
            file => file.Scope == "collection");

        Assert.NotNull(result);
        Assert.Same(rootPoster, result.Poster);
        Assert.Same(nestedLogo, result.Logo);
    }

    [Fact]
    public void MatchCandidates_PrefersExplicitNamedArtworkOverStaleBareFile()
    {
        var bare = CreateFile("Collections/Star Wars Collection/poster.jpg", "collection");
        var named = CreateFile("Collections/Star Wars Collection/Star Wars Collection-poster.jpg", "collection");
        named.Sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var lookup = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal)
        {
            ["starwarscollection"] = [bare, named],
        };

        var result = ArtworkIndex.MatchCandidates(
            ["starwarscollection"],
            lookup,
            file => file.Scope == "collection");

        Assert.NotNull(result);
        Assert.Same(named, result.Poster);
    }

    [Fact]
    public void CollectionMembers_ResolveWrongCollectionProviderIdByMovieIds()
    {
        var blade = CreateFile("Collections/Blade Collection/poster.jpg", "collection");
        blade.TmdbId = 735;
        blade.CollectionPartTmdbIds = [36647, 36586, 12596];
        var lookup = blade.CollectionPartTmdbIds.ToDictionary(
            id => id.ToString(),
            _ => new List<ArtworkManifestFile> { blade },
            StringComparer.Ordinal);

        var result = ArtworkIndex.MatchCollectionMemberIds(["36647"], lookup);

        Assert.NotNull(result);
        Assert.Same(blade, result.Poster);
    }

    [Fact]
    public void CollectionMatching_PrefersMembersOverWrongProviderIdAndLocalizedName()
    {
        var correct = CreateFile("Collections/Blade Collection/poster.jpg", "collection");
        correct.TmdbId = 735;
        correct.CollectionPartTmdbIds = [36647, 36586, 12596];
        var wrong = CreateFile("Collections/Marvel Collection/poster.jpg", "collection");
        wrong.TmdbId = 131292;

        var byCollection = new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal)
        {
            ["marvelcollection"] = [wrong],
        };
        var byIdentity = ArtworkIndex.BuildPublishedIdentityLookup([correct, wrong]);
        var byParts = correct.CollectionPartTmdbIds.ToDictionary(
            id => id.ToString(),
            _ => new List<ArtworkManifestFile> { correct },
            StringComparer.Ordinal);

        var result = ArtworkIndex.MatchCollectionCandidates(
            previousCollectionKey: null,
            identity: new ArtworkIdentity("collection", "131292", null),
            memberTmdbIds: ["36647"],
            candidateNames: ["Marvel Collection"],
            byCollection,
            byIdentity,
            byParts,
            new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal));

        Assert.NotNull(result);
        Assert.Same(correct, result.Poster);
    }

    [Fact]
    public void CollectionMatching_CurrentIdentityRepairsStaleCustomCollectionState()
    {
        var correct = CreateFile("Collections/Iron Man Collection/poster.jpg", "collection");
        correct.TmdbId = 131292;
        var stale = CreateFile("Collections/Marvel Collection/poster.jpg", "collection");
        stale.CollectionKey = "marvel";

        var result = ArtworkIndex.MatchCollectionCandidates(
            previousCollectionKey: "marvel",
            identity: new ArtworkIdentity("collection", "131292", null),
            memberTmdbIds: [],
            candidateNames: ["Iron Man Collection"],
            new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal),
            ArtworkIndex.BuildPublishedIdentityLookup([correct]),
            new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal),
            new Dictionary<string, List<ArtworkManifestFile>>(StringComparer.Ordinal)
            {
                ["marvel"] = [stale],
            });

        Assert.NotNull(result);
        Assert.Same(correct, result.Poster);
    }

    [Fact]
    public void MediaFolderDestination_UsesVideoBaseNameForMovie()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var video = Path.Combine(directory, "Example (2026) [UHD].mkv");
            File.WriteAllBytes(video, [0]);
            var movie = new Movie { Path = video };

            Assert.Equal(
                Path.Combine(directory, "Example (2026) [UHD]-poster.jpg"),
                ArtworkMediaWriter.GetDestinationPath(movie, "poster", "Movies/Example/poster.jpg"));
            Assert.Equal(
                Path.Combine(directory, "Example (2026) [UHD]-clearlogo.png"),
                ArtworkMediaWriter.GetDestinationPath(movie, "logo", "Movies/Example/clearlogo.png"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void MediaFolderDestination_UsesBareNamesForSeries()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var series = new Series { Path = directory };

            Assert.Equal(
                Path.Combine(directory, "poster.png"),
                ArtworkMediaWriter.GetDestinationPath(series, "poster", "Shows/Example/poster.png"));
            Assert.Equal(
                Path.Combine(directory, "clearlogo.png"),
                ArtworkMediaWriter.GetDestinationPath(series, "logo", "Shows/Example/clearlogo.png"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ManagedFileDeletion_DeletesOnlyUnchangedPluginFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "poster.jpg");
            var contents = new byte[] { 1, 2, 3 };
            File.WriteAllBytes(path, contents);
            var managed = new ManagedMediaFile
            {
                Sha256 = Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant(),
            };

            Assert.True(ArtworkMediaWriter.DeleteIfUnchanged(path, managed));
            Assert.False(File.Exists(path));

            File.WriteAllBytes(path, [9, 9, 9]);
            Assert.False(ArtworkMediaWriter.DeleteIfUnchanged(path, managed));
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ExistingMediaFileOverwrite_RequiresSettingForUnmanagedOrModifiedFiles()
    {
        var managed = new ManagedMediaFile { Sha256 = "original" };

        Assert.False(ArtworkMediaWriter.CanOverwriteExistingFile(false, null, "local"));
        Assert.False(ArtworkMediaWriter.CanOverwriteExistingFile(false, managed, "modified"));
        Assert.True(ArtworkMediaWriter.CanOverwriteExistingFile(false, managed, "original"));
        Assert.True(ArtworkMediaWriter.CanOverwriteExistingFile(true, null, "local"));
        Assert.True(ArtworkMediaWriter.CanOverwriteExistingFile(true, managed, "modified"));
    }

    private static ArtworkManifest CreateManifest(string path) => new()
    {
        SchemaVersion = 1,
        Revision = Revision,
        GeneratedAt = "2026-08-01T00:00:00Z",
        Files = [CreateFile(path, "item")],
    };

    private static ArtworkManifestFile CreateFile(string path, string scope) => new()
    {
        Path = path,
        Sha256 = Revision,
        Size = 123,
        ModifiedAt = "2026-08-01T00:00:00Z",
        ReleaseNames = ["Example"],
        Scope = scope,
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cowabunga-artwork-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
