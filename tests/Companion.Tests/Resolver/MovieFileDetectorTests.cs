using Emby.Naming.Common;
using Jellyfin.Plugin.SmartResolver.Core;
using Jellyfin.Plugin.SmartResolver.Modules.Movies;
using MediaBrowser.Model.IO;
using Xunit;

namespace Jellyfin.Plugin.SmartResolver.Tests;

public sealed class MovieFileDetectorTests
{
    [Fact]
    public void Detect_UsesVideoFileNameInsteadOfFolderName()
    {
        var folderPath = Path.Combine("Movies", "(1979) Completely Wrong Folder");
        var videoPath = Path.Combine(
            folderPath,
            "Moonraker (1979) [WEB-DL].mkv");

        var decision = CreateDetector().Detect(
            folderPath,
            [FileMetadata(videoPath)]);

        Assert.True(decision.Accepted);
        Assert.Equal(videoPath, decision.ResolvedPath);
        Assert.Equal("Moonraker", decision.DetectedName);
        Assert.Equal(1979, decision.DetectedYear);
        Assert.Equal([ResolverEvidence.MovieVideoFile], decision.Evidence);
    }

    [Theory]
    [InlineData("Movie (2025).mkv")]
    [InlineData("Movie (2025).mp4")]
    [InlineData("Movie (2025).avi")]
    public void Detect_AcceptsVideoFileRecognizedByJellyfin(string fileName)
    {
        var folderPath = Path.Combine("Movies", "Navigation Folder");
        var videoPath = Path.Combine(folderPath, fileName);

        var decision = CreateDetector().Detect(
            folderPath,
            [FileMetadata(videoPath)]);

        Assert.True(decision.Accepted);
        Assert.Equal("Movie", decision.DetectedName);
        Assert.Equal(2025, decision.DetectedYear);
    }

    [Theory]
    [InlineData("2012 (2009) [WEB-DL].mkv", "2012", 2009)]
    [InlineData("1917 (2019) [UHD].mkv", "1917", 2019)]
    [InlineData("1984 (1984).mkv", "1984", 1984)]
    public void Detect_PreservesNumericMovieTitle(
        string fileName,
        string expectedTitle,
        int expectedYear)
    {
        var folderPath = Path.Combine("Movies", "Navigation Folder");
        var videoPath = Path.Combine(folderPath, fileName);

        var decision = CreateDetector().Detect(
            folderPath,
            [FileMetadata(videoPath)]);

        Assert.True(decision.Accepted);
        Assert.Equal(expectedTitle, decision.DetectedName);
        Assert.Equal(expectedYear, decision.DetectedYear);
    }

    [Fact]
    public void Detect_DoesNotUseFolderYearAsFallback()
    {
        var folderPath = Path.Combine("Movies", "(1999) Navigation Folder");
        var videoPath = Path.Combine(folderPath, "Movie [WEB-DL].mkv");

        var decision = CreateDetector().Detect(
            folderPath,
            [FileMetadata(videoPath)]);

        Assert.True(decision.Accepted);
        Assert.Equal("Movie", decision.DetectedName);
        Assert.Null(decision.DetectedYear);
    }

    [Fact]
    public void Detect_IgnoresArtworkAndMetadataFiles()
    {
        var folderPath = Path.Combine("Movies", "Moonraker");
        var videoPath = Path.Combine(folderPath, "Moonraker (1979).mkv");

        var decision = CreateDetector().Detect(
            folderPath,
            [
                FileMetadata(videoPath),
                FileMetadata(Path.Combine(folderPath, "poster.jpg")),
                FileMetadata(Path.Combine(folderPath, "movie.nfo"))
            ]);

        Assert.True(decision.Accepted);
        Assert.Equal(videoPath, decision.ResolvedPath);
    }

    [Fact]
    public void Detect_IgnoresTrailerAlongsidePrimaryMovie()
    {
        var folderPath = Path.Combine("Movies", "Moonraker");
        var videoPath = Path.Combine(folderPath, "Moonraker (1979).mkv");

        var decision = CreateDetector().Detect(
            folderPath,
            [
                FileMetadata(videoPath),
                FileMetadata(Path.Combine(folderPath, "Moonraker (1979)-trailer.mkv"))
            ]);

        Assert.True(decision.Accepted);
        Assert.Equal(videoPath, decision.ResolvedPath);
    }

    [Fact]
    public void Detect_RejectsFolderWithoutPrimaryVideo()
    {
        var folderPath = Path.Combine("Movies", "Moonraker");

        var decision = CreateDetector().Detect(
            folderPath,
            [
                FileMetadata(Path.Combine(folderPath, "poster.jpg")),
                FileMetadata(Path.Combine(folderPath, "movie.nfo"))
            ]);

        Assert.False(decision.Accepted);
        Assert.Equal(ResolverReasonCode.ChildHasNoMovieEvidence, decision.ReasonCode);
    }

    [Fact]
    public void Detect_RejectsDifferentPrimaryMovies()
    {
        var folderPath = Path.Combine("Movies", "Moonraker");

        var decision = CreateDetector().Detect(
            folderPath,
            [
                FileMetadata(Path.Combine(folderPath, "Moonraker (1979).mkv")),
                FileMetadata(Path.Combine(folderPath, "Moonraker Alternate (1979).mkv"))
            ]);

        Assert.False(decision.Accepted);
        Assert.Equal(ResolverReasonCode.MultipleMovieFiles, decision.ReasonCode);
    }

    [Fact]
    public void Detect_AcceptsAlternateVersionsOfSameMovie()
    {
        var folderPath = Path.Combine("Movies", "Navigation");
        var fullHd = Path.Combine(folderPath, "Moonraker (1979) - 1080p.mkv");
        var ultraHd = Path.Combine(folderPath, "Moonraker (1979) - 2160p.mkv");

        var decision = CreateDetector().Detect(
            folderPath,
            [FileMetadata(fullHd), FileMetadata(ultraHd)]);

        Assert.True(decision.Accepted);
        Assert.Equal("Moonraker", decision.DetectedName);
        Assert.Equal([ultraHd], decision.AlternateVersions);
        Assert.Contains(ResolverEvidence.MovieAlternateVersion, decision.Evidence);
    }

    [Fact]
    public void Detect_AcceptsMultipartMovie()
    {
        var folderPath = Path.Combine("Movies", "Navigation");
        var firstPart = Path.Combine(folderPath, "Once Upon a Time in America (1984) CD1.mkv");
        var secondPart = Path.Combine(folderPath, "Once Upon a Time in America (1984) CD2.mkv");

        var decision = CreateDetector().Detect(
            folderPath,
            [FileMetadata(firstPart), FileMetadata(secondPart)]);

        Assert.True(decision.Accepted);
        Assert.Equal("Once Upon a Time in America", decision.DetectedName);
        Assert.Equal([secondPart], decision.AdditionalParts);
        Assert.Contains(ResolverEvidence.MovieAdditionalPart, decision.Evidence);
    }

    [Fact]
    public void Detect_AcceptsMovieInsideOneAdditionalFolder()
    {
        var outer = Directory.CreateTempSubdirectory("SmartResolverMovieOuter").FullName;
        try
        {
            var inner = Directory.CreateDirectory(Path.Combine(outer, "Actual Movie")).FullName;
            var video = Path.Combine(inner, "Moonraker (1979).mkv");
            File.WriteAllBytes(video, []);

            var decision = CreateDetector().Detect(
                outer,
                [DirectoryMetadata(inner)],
                allowNestedFolder: true);

            Assert.True(decision.Accepted);
            Assert.Equal(video, decision.ResolvedPath);
            Assert.Equal("Moonraker", decision.DetectedName);
            Assert.Contains(ResolverEvidence.NestedMovieFolder, decision.Evidence);
        }
        finally
        {
            Directory.Delete(outer, recursive: true);
        }
    }

    private static MovieFileDetector CreateDetector()
    {
        return new MovieFileDetector(new NamingOptions());
    }

    private static FileSystemMetadata FileMetadata(string path)
    {
        return new FileSystemMetadata
        {
            Exists = true,
            FullName = path,
            IsDirectory = false,
            Name = Path.GetFileName(path)
        };
    }

    private static FileSystemMetadata DirectoryMetadata(string path)
    {
        return new FileSystemMetadata
        {
            Exists = true,
            FullName = path,
            IsDirectory = true,
            Name = Path.GetFileName(path)
        };
    }
}
