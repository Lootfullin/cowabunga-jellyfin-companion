using Jellyfin.Plugin.SmartResolver.Core;
using Jellyfin.Plugin.SmartResolver.Modules.NestedSeries;
using MediaBrowser.Model.IO;
using Xunit;

namespace Jellyfin.Plugin.SmartResolver.Tests;

public sealed class NestedSeriesDetectorTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "Jellyfin.SmartResolver.Tests",
        Guid.NewGuid().ToString("N"));

    public NestedSeriesDetectorTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Detect_ReturnsAcceptedDecisionWithSeasonEvidence()
    {
        var outerPath = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "(2025) Alien Earth")).FullName;
        var innerPath = Directory.CreateDirectory(
            Path.Combine(outerPath, "Alien Earth (2025)")).FullName;
        Directory.CreateDirectory(Path.Combine(innerPath, "Season 01"));

        var decision = new NestedSeriesDetector().Detect(
            outerPath,
            [DirectoryMetadata(innerPath)]);

        Assert.True(decision.Accepted);
        Assert.Equal(ResolverReasonCode.Accepted, decision.ReasonCode);
        Assert.Equal(innerPath, decision.ResolvedPath);
        Assert.Equal("Alien Earth (2025)", decision.DetectedName);
        Assert.Equal(2025, decision.DetectedYear);
        Assert.Equal([ResolverEvidence.SeasonFolder], decision.Evidence);
    }

    [Fact]
    public void Detect_ReturnsAcceptedDecisionWithEpisodeEvidence()
    {
        var outerPath = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "(2025) Alien Earth")).FullName;
        var innerPath = Directory.CreateDirectory(
            Path.Combine(outerPath, "Alien Earth (2025)")).FullName;
        File.WriteAllText(Path.Combine(innerPath, "Alien.Earth.S01E01.mkv"), string.Empty);

        var decision = new NestedSeriesDetector().Detect(
            outerPath,
            [DirectoryMetadata(innerPath)]);

        Assert.True(decision.Accepted);
        Assert.Equal([ResolverEvidence.EpisodeFile], decision.Evidence);
    }

    [Fact]
    public void Detect_ExplainsMultipleNestedDirectories()
    {
        var outerPath = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "(2025) Alien Earth")).FullName;
        var firstChild = Directory.CreateDirectory(Path.Combine(outerPath, "First")).FullName;
        var secondChild = Directory.CreateDirectory(Path.Combine(outerPath, "Second")).FullName;

        var decision = new NestedSeriesDetector().Detect(
            outerPath,
            [DirectoryMetadata(firstChild), DirectoryMetadata(secondChild)]);

        Assert.False(decision.Accepted);
        Assert.Equal(ResolverReasonCode.MultipleEligibleChildren, decision.ReasonCode);
    }

    [Fact]
    public void Detect_ExplainsMissingSeriesEvidence()
    {
        var outerPath = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "(2025) Alien Earth")).FullName;
        var innerPath = Directory.CreateDirectory(
            Path.Combine(outerPath, "Alien Earth (2025)")).FullName;

        var decision = new NestedSeriesDetector().Detect(
            outerPath,
            [DirectoryMetadata(innerPath)]);

        Assert.False(decision.Accepted);
        Assert.Equal(ResolverReasonCode.ChildHasNoSeriesEvidence, decision.ReasonCode);
    }

    [Fact]
    public void Detect_ExplainsUnavailableFilesystem()
    {
        var outerPath = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "(2025) Alien Earth")).FullName;
        var missingInnerPath = Path.Combine(outerPath, "Alien Earth (2025)");

        var decision = new NestedSeriesDetector().Detect(
            outerPath,
            [DirectoryMetadata(missingInnerPath)]);

        Assert.False(decision.Accepted);
        Assert.Equal(ResolverReasonCode.FilesystemUnavailable, decision.ReasonCode);
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
