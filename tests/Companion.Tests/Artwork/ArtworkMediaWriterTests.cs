namespace Jellyfin.Plugin.CustomArtwork.Tests;

using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

public sealed class ArtworkMediaWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"custom-artwork-writer-{Guid.NewGuid():N}");

    public ArtworkMediaWriterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void MissingDestinationIsRetried()
    {
        var path = Path.Combine(_directory, "poster.jpg");

        Assert.True(ArtworkMediaWriter.NeedsSynchronization(
            path,
            "new-hash",
            overwriteExistingMediaFiles: false,
            new ManagedMediaState()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ExistingUnmanagedFileHonorsOverwriteSetting(
        bool overwriteExistingMediaFiles,
        bool expected)
    {
        var path = CreateFile("clearlogo.png");

        Assert.Equal(expected, ArtworkMediaWriter.NeedsSynchronization(
            path,
            "new-hash",
            overwriteExistingMediaFiles,
            new ManagedMediaState()));
    }

    [Fact]
    public void ManagedFileIsRetriedWhenCloudHashChanges()
    {
        var path = CreateFile("poster.jpg");
        var state = new ManagedMediaState();
        state.Files[path] = new ManagedMediaFile { Sha256 = "old-hash" };

        Assert.True(ArtworkMediaWriter.NeedsSynchronization(
            path,
            "new-hash",
            overwriteExistingMediaFiles: false,
            state));
    }

    [Fact]
    public void CurrentManagedFileIsNotDownloadedAgain()
    {
        var path = CreateFile("poster.jpg");
        var state = new ManagedMediaState();
        state.Files[path] = new ManagedMediaFile { Sha256 = "same-hash" };

        Assert.False(ArtworkMediaWriter.NeedsSynchronization(
            path,
            "same-hash",
            overwriteExistingMediaFiles: true,
            state));
    }

    [Fact]
    public void SeriesCandidatesIncludeJellyfinPosterAndLogoAliases()
    {
        var series = new Series { Path = _directory };

        var posters = ArtworkMediaWriter.GetCandidatePaths(
            series,
            "poster",
            "Shows/Example/poster.jpg",
            includeGenericAliases: true);
        var logos = ArtworkMediaWriter.GetCandidatePaths(
            series,
            "logo",
            "Shows/Example/clearlogo.png",
            includeGenericAliases: true);

        Assert.Contains(Path.Combine(_directory, "poster.jpg"), posters);
        Assert.Contains(Path.Combine(_directory, "folder.png"), posters);
        Assert.Contains(Path.Combine(_directory, "cover.webp"), posters);
        Assert.Contains(Path.Combine(_directory, "clearlogo.png"), logos);
        Assert.Contains(Path.Combine(_directory, "logo.png"), logos);
    }

    [Fact]
    public void MovieCandidatesUseGenericAliasesOnlyForDedicatedDirectory()
    {
        var video = CreateFile("Example.mkv");
        var movie = new Movie { Path = video };

        var dedicated = ArtworkMediaWriter.GetCandidatePaths(
            movie,
            "poster",
            "Movies/Example/poster.jpg",
            includeGenericAliases: true);
        var shared = ArtworkMediaWriter.GetCandidatePaths(
            movie,
            "poster",
            "Movies/Example/poster.jpg",
            includeGenericAliases: false);

        Assert.Contains(Path.Combine(_directory, "Example-poster.jpg"), dedicated);
        Assert.Contains(Path.Combine(_directory, "folder.jpg"), dedicated);
        Assert.DoesNotContain(Path.Combine(_directory, "folder.jpg"), shared);
    }

    [Fact]
    public void ExistingUnmanagedAliasRequiresExplicitOverwrite()
    {
        var folder = CreateFile("folder.jpg");
        var destination = Path.Combine(_directory, "Example-poster.jpg");
        var candidates = new[] { destination, folder };
        var state = new ManagedMediaState();

        Assert.False(ArtworkMediaWriter.NeedsRoleSynchronization(
            candidates,
            "cloud-hash",
            overwriteExistingMediaFiles: false,
            state));
        Assert.True(ArtworkMediaWriter.NeedsRoleSynchronization(
            candidates,
            "cloud-hash",
            overwriteExistingMediaFiles: true,
            state));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "existing");
        return path;
    }
}
