using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartResolver.Diagnostics;
using Jellyfin.Plugin.SmartResolver.Modules.NestedSeries;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.SmartResolver.Tests;

public sealed class NestedSeriesResolverTests : IDisposable
{
    private readonly CompanionTestContext _context = new();
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "Jellyfin.SmartResolver.Tests",
        Guid.NewGuid().ToString("N"));

    public NestedSeriesResolverTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _context.Dispose();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Priority_IsFirst()
    {
        var resolver = CreateResolver();

        Assert.Equal(ResolverPriority.First, resolver.Priority);
    }

    [Theory]
    [InlineData("Season 01")]
    [InlineData("Series 1 [UHD]")]
    [InlineData("Сезон 1")]
    [InlineData("Specials")]
    [InlineData("Спецэпизоды")]
    public void ResolvePath_AcceptsSupportedSeasonFolder(string seasonFolderName)
    {
        var (outerPath, innerPath) = CreateSeriesTree(
            "(2025) Alien Earth",
            "Alien Earth (2025)",
            seasonFolderName: seasonFolderName);

        var result = CreateResolver().ResolvePath(CreateArgs(outerPath, [innerPath]));

        var series = Assert.IsType<Series>(result);
        Assert.Equal(innerPath, series.Path);
        Assert.Equal("Alien Earth (2025)", series.Name);
        Assert.Equal(2025, series.ProductionYear);
    }

    [Theory]
    [InlineData("S01E01.mkv")]
    [InlineData("S1E1.mkv")]
    [InlineData("S01E001.mkv")]
    public void ResolvePath_AcceptsSupportedEpisodeFile(string episodeFileName)
    {
        var (outerPath, innerPath) = CreateSeriesTree(
            "(2024) Example",
            "Example (2024)",
            episodeFileName: episodeFileName);

        var result = CreateResolver().ResolvePath(CreateArgs(outerPath, [innerPath]));

        var series = Assert.IsType<Series>(result);
        Assert.Equal(innerPath, series.Path);
    }

    [Fact]
    public void ResolvePath_UsesOuterYearWhenInnerNameHasNoYear()
    {
        var (outerPath, innerPath) = CreateSeriesTree(
            "(2005) Doctor Who",
            "Doctor Who",
            seasonFolderName: "Season 01");

        var result = CreateResolver().ResolvePath(CreateArgs(outerPath, [innerPath]));

        var series = Assert.IsType<Series>(result);
        Assert.Equal(2005, series.ProductionYear);
    }

    [Fact]
    public void ResolvePath_PrefersInnerYear()
    {
        var (outerPath, innerPath) = CreateSeriesTree(
            "(2005) Doctor Who",
            "Doctor Who (2025)",
            seasonFolderName: "Season 01");

        var result = CreateResolver().ResolvePath(CreateArgs(outerPath, [innerPath]));

        var series = Assert.IsType<Series>(result);
        Assert.Equal(2025, series.ProductionYear);
    }

    [Fact]
    public void ResolvePath_PreservesProviderIdsFromNestedSeriesPath()
    {
        var (outerPath, innerPath) = CreateSeriesTree(
            "(2025) Example",
            "Example (2025) [tmdbid-123] [tvdbid-456] [imdbid-tt7654321]",
            seasonFolderName: "Season 01");

        var result = CreateResolver().ResolvePath(CreateArgs(outerPath, [innerPath]));

        var series = Assert.IsType<Series>(result);
        Assert.Equal("123", series.GetProviderId(MetadataProvider.Tmdb));
        Assert.Equal("456", series.GetProviderId(MetadataProvider.Tvdb));
        Assert.Equal("tt7654321", series.GetProviderId(MetadataProvider.Imdb));
    }

    [Theory]
    [InlineData(CollectionType.movies)]
    [InlineData(CollectionType.music)]
    [InlineData(CollectionType.books)]
    public void ResolvePath_RejectsNonTvLibrary(CollectionType collectionType)
    {
        var (outerPath, innerPath) = CreateSeriesTree(
            "(2025) Alien Earth",
            "Alien Earth (2025)",
            seasonFolderName: "Season 01");

        var result = CreateResolver().ResolvePath(
            CreateArgs(outerPath, [innerPath], collectionType: collectionType));

        Assert.Null(result);
    }

    [Theory]
    [InlineData("Alien Earth")]
    [InlineData("2025 Alien Earth")]
    [InlineData("(25) Alien Earth")]
    [InlineData("(2025)")]
    public void ResolvePath_RejectsUnsupportedOuterName(string outerName)
    {
        var (outerPath, innerPath) = CreateSeriesTree(
            outerName,
            "Alien Earth (2025)",
            seasonFolderName: "Season 01");

        var result = CreateResolver().ResolvePath(CreateArgs(outerPath, [innerPath]));

        Assert.Null(result);
    }

    [Fact]
    public void ResolvePath_RejectsFile()
    {
        var filePath = Path.Combine(_tempRoot, "(2025) Alien Earth.mkv");
        File.WriteAllText(filePath, string.Empty);

        var result = CreateResolver().ResolvePath(
            CreateArgs(filePath, [], isDirectory: false));

        Assert.Null(result);
    }

    [Fact]
    public void ResolvePath_RejectsMissingParent()
    {
        var (outerPath, innerPath) = CreateSeriesTree(
            "(2025) Alien Earth",
            "Alien Earth (2025)",
            seasonFolderName: "Season 01");

        var result = CreateResolver().ResolvePath(
            CreateArgs(outerPath, [innerPath], includeParent: false));

        Assert.Null(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolvePath_RejectsPathInsideExistingSeriesOrSeason(bool insideSeries)
    {
        var (outerPath, innerPath) = CreateSeriesTree(
            "(2025) Alien Earth",
            "Alien Earth (2025)",
            seasonFolderName: "Season 01");
        Folder parent = insideSeries ? new Series() : new Season();

        var result = CreateResolver().ResolvePath(
            CreateArgs(outerPath, [innerPath], parent: parent));

        Assert.Null(result);
    }

    [Fact]
    public void ResolvePath_RejectsOuterFolderWithoutChildDirectory()
    {
        var outerPath = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "(2025) Alien Earth")).FullName;

        var result = CreateResolver().ResolvePath(CreateArgs(outerPath, []));

        Assert.Null(result);
    }

    [Fact]
    public void ResolvePath_RejectsOuterFolderWithMultipleChildDirectories()
    {
        var outerPath = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "(2025) Alien Earth")).FullName;
        var firstChild = Directory.CreateDirectory(Path.Combine(outerPath, "Alien Earth (2025)")).FullName;
        var secondChild = Directory.CreateDirectory(Path.Combine(outerPath, "Extras")).FullName;

        var result = CreateResolver().ResolvePath(CreateArgs(outerPath, [firstChild, secondChild]));

        Assert.Null(result);
    }

    [Fact]
    public void ResolvePath_RejectsChildWithoutSeriesEvidence()
    {
        var (outerPath, innerPath) = CreateSeriesTree(
            "(2025) Alien Earth",
            "Alien Earth (2025)");

        var result = CreateResolver().ResolvePath(CreateArgs(outerPath, [innerPath]));

        Assert.Null(result);
    }

    [Fact]
    public void ResolvePath_ReturnsNullWhenChildDirectoryIsUnavailable()
    {
        var outerPath = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "(2025) Alien Earth")).FullName;
        var missingInnerPath = Path.Combine(outerPath, "Alien Earth (2025)");

        var result = CreateResolver().ResolvePath(CreateArgs(outerPath, [missingInnerPath]));

        Assert.Null(result);
    }

    [Fact]
    public void ResolvePath_LogsSuccessfulMapping()
    {
        var logger = new RecordingLogger<NestedSeriesResolver>();
        var (outerPath, innerPath) = CreateSeriesTree(
            "(2025) Alien Earth",
            "Alien Earth (2025)",
            seasonFolderName: "Season 01");

        var result = CreateResolver(logger).ResolvePath(CreateArgs(outerPath, [innerPath]));

        Assert.NotNull(result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Jellyfin Smart Resolver mapped", entry.Message, StringComparison.Ordinal);
        Assert.Contains(outerPath, entry.Message, StringComparison.Ordinal);
        Assert.Contains(innerPath, entry.Message, StringComparison.Ordinal);
    }

    private static NestedSeriesResolver CreateResolver(
        ILogger<NestedSeriesResolver>? logger = null)
    {
        return new NestedSeriesResolver(
            new NestedSeriesDetector(),
            new ResolutionHistory(),
            logger ?? new RecordingLogger<NestedSeriesResolver>());
    }

    private static ItemResolveArgs CreateArgs(
        string path,
        IReadOnlyCollection<string> childDirectories,
        bool isDirectory = true,
        CollectionType collectionType = CollectionType.tvshows,
        Folder? parent = default,
        bool includeParent = true)
    {
        return new ItemResolveArgs(null!, null!)
        {
            CollectionType = collectionType,
            FileInfo = new FileSystemMetadata
            {
                Exists = true,
                FullName = path,
                IsDirectory = isDirectory,
                Name = Path.GetFileName(path)
            },
            FileSystemChildren = childDirectories
                .Select(childPath => new FileSystemMetadata
                {
                    Exists = true,
                    FullName = childPath,
                    IsDirectory = true,
                    Name = Path.GetFileName(childPath)
                })
                .ToArray(),
            Parent = includeParent ? parent ?? new Folder() : null
        };
    }

    private (string OuterPath, string InnerPath) CreateSeriesTree(
        string outerName,
        string innerName,
        string? seasonFolderName = null,
        string? episodeFileName = null)
    {
        var outerPath = Directory.CreateDirectory(Path.Combine(_tempRoot, outerName)).FullName;
        var innerPath = Directory.CreateDirectory(Path.Combine(outerPath, innerName)).FullName;

        if (seasonFolderName is not null)
        {
            Directory.CreateDirectory(Path.Combine(innerPath, seasonFolderName));
        }

        if (episodeFileName is not null)
        {
            File.WriteAllText(Path.Combine(innerPath, episodeFileName), string.Empty);
        }

        return (outerPath, innerPath);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
