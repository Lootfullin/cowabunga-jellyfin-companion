using Emby.Naming.Common;
using Jellyfin.Plugin.SmartResolver.Api;
using Jellyfin.Plugin.SmartResolver.Diagnostics;
using Jellyfin.Plugin.SmartResolver.Modules.Movies;
using Jellyfin.Plugin.SmartResolver.Modules.NestedSeries;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.SmartResolver.Tests;

public sealed class SmartResolverControllerTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(
            Path.GetTempPath(),
            "Jellyfin.SmartResolver.ApiTests",
            Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Preview_ReportsMovieWithoutChangingFolder()
    {
        var movieFolder = Directory.CreateDirectory(
            Path.Combine(_root, "Navigation")).FullName;
        var moviePath = Path.Combine(movieFolder, "1917 (2019) [UHD].mkv");
        File.WriteAllBytes(moviePath, []);
        var before = Directory.GetFileSystemEntries(movieFolder);

        var result = CreateController().Preview(new PreviewRequest
        {
            Path = movieFolder,
            MediaType = "Movies"
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var entries = Assert.IsAssignableFrom<IReadOnlyList<PreviewResponse>>(ok.Value);
        var preview = Assert.Single(entries);
        Assert.True(preview.Accepted);
        Assert.Equal("1917", preview.DetectedName);
        Assert.Equal(2019, preview.DetectedYear);
        Assert.Equal(before, Directory.GetFileSystemEntries(movieFolder));
    }

    [Fact]
    public void Preview_RejectsUnavailableFolder()
    {
        var result = CreateController().Preview(new PreviewRequest
        {
            Path = Path.Combine(_root, "Missing"),
            MediaType = "Auto"
        });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    private static SmartResolverController CreateController()
    {
        return new SmartResolverController(
            new ResolutionHistory(),
            new MovieFileDetector(new NamingOptions()),
            new NestedSeriesDetector());
    }
}
