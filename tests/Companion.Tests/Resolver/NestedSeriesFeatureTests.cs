using Emby.Naming.Common;
using Jellyfin.Plugin.Companion.Configuration;
using Jellyfin.Plugin.SmartResolver.Core;
using Jellyfin.Plugin.SmartResolver.Modules.Movies;
using Jellyfin.Plugin.SmartResolver.Modules.NestedSeries;
using MediaBrowser.Model.IO;
using Xunit;

namespace Jellyfin.Plugin.SmartResolver.Tests;

public sealed class NestedSeriesFeatureTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(
            Path.GetTempPath(),
            "Jellyfin.SmartResolver.Features",
            Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void AnySingleNestedSeries_AcceptsOuterFolderWithoutYearPrefix()
    {
        var (outer, inner) = CreateSeries("Marvel", "Ironheart (2025)");
        var configuration = new PluginConfiguration
        {
            NestedSeriesMode = ResolverMode.AnySingleNestedSeries
        };

        var decision = new NestedSeriesDetector().Detect(
            outer,
            [DirectoryMetadata(inner)],
            configuration);

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void CustomRegex_AcceptsMatchingOuterFolder()
    {
        var (outer, inner) = CreateSeries("TV - Fallout", "Fallout (2024)");
        var configuration = new PluginConfiguration
        {
            NestedSeriesMode = ResolverMode.CustomRegex,
            CustomRegex = @"^TV\s-\s.+$"
        };

        var decision = new NestedSeriesDetector().Detect(
            outer,
            [DirectoryMetadata(inner)],
            configuration);

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void InvalidCustomRegex_IsRejectedWithoutException()
    {
        var (outer, inner) = CreateSeries("TV - Fallout", "Fallout (2024)");
        var configuration = new PluginConfiguration
        {
            NestedSeriesMode = ResolverMode.CustomRegex,
            CustomRegex = "["
        };

        var decision = new NestedSeriesDetector().Detect(
            outer,
            [DirectoryMetadata(inner)],
            configuration);

        Assert.False(decision.Accepted);
        Assert.Equal(ResolverReasonCode.InvalidCustomRegex, decision.ReasonCode);
    }

    [Fact]
    public void IgnoredServiceDirectory_DoesNotCreateAmbiguity()
    {
        var (outer, inner) = CreateSeries("(2025) Test", "Test (2025)");
        var ignored = Directory.CreateDirectory(Path.Combine(outer, "extrafanart")).FullName;

        var decision = new NestedSeriesDetector().Detect(
            outer,
            [DirectoryMetadata(inner), DirectoryMetadata(ignored)]);

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void TvShowNfo_IsStrongSeriesEvidence()
    {
        var outer = Directory.CreateDirectory(Path.Combine(_root, "(2025) Test")).FullName;
        var inner = Directory.CreateDirectory(Path.Combine(outer, "Test (2025)")).FullName;
        File.WriteAllText(Path.Combine(inner, "tvshow.nfo"), string.Empty);

        var decision = new NestedSeriesDetector().Detect(
            outer,
            [DirectoryMetadata(inner)]);

        Assert.True(decision.Accepted);
        Assert.Contains(ResolverEvidence.TvShowNfo, decision.Evidence);
    }

    [Fact]
    public void MovieDetector_PreservesRussianTitleFromFilename()
    {
        var folder = Path.Combine("Movies", "Навигационная папка");
        var video = Path.Combine(folder, "Ирония судьбы (1975) [WEB-DL].mkv");

        var decision = new MovieFileDetector(new NamingOptions()).Detect(
            folder,
            [FileMetadata(video)]);

        Assert.True(decision.Accepted);
        Assert.Equal("Ирония судьбы", decision.DetectedName);
        Assert.Equal(1975, decision.DetectedYear);
    }

    [Theory]
    [InlineData("extrafanart")]
    [InlineData("EXTRAFANART")]
    [InlineData("@eaDir")]
    [InlineData("$RECYCLE.BIN")]
    public void IgnoredDirectoryNames_AreCaseInsensitive(string name)
    {
        Assert.True(IgnoredDirectoryNames.Contains(name));
    }

    [Fact]
    public void PathSafety_AcceptsOnlyDirectChild()
    {
        var outer = Directory.CreateDirectory(Path.Combine(_root, "Outer")).FullName;
        var inner = Directory.CreateDirectory(Path.Combine(outer, "Inner")).FullName;
        var deeper = Directory.CreateDirectory(Path.Combine(inner, "Deeper")).FullName;

        Assert.True(PathSafety.IsDirectSafeChild(outer, inner));
        Assert.False(PathSafety.IsDirectSafeChild(outer, outer));
        Assert.False(PathSafety.IsDirectSafeChild(outer, deeper));
    }

    private (string Outer, string Inner) CreateSeries(string outerName, string innerName)
    {
        var outer = Directory.CreateDirectory(Path.Combine(_root, outerName)).FullName;
        var inner = Directory.CreateDirectory(Path.Combine(outer, innerName)).FullName;
        Directory.CreateDirectory(Path.Combine(inner, "Season 01"));
        return (outer, inner);
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
}
