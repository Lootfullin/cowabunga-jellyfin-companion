using Jellyfin.Plugin.SmartResolver.Diagnostics;
using Jellyfin.Plugin.SmartResolver.Modules.Movies;
using Jellyfin.Plugin.SmartResolver.Modules.NestedSeries;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartResolver.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("SmartResolver")]
public sealed class SmartResolverController : ControllerBase
{
    private readonly ResolutionHistory _history;
    private readonly MovieFileDetector _movieDetector;
    private readonly NestedSeriesDetector _seriesDetector;

    public SmartResolverController(
        ResolutionHistory history,
        MovieFileDetector movieDetector,
        NestedSeriesDetector seriesDetector)
    {
        _history = history;
        _movieDetector = movieDetector;
        _seriesDetector = seriesDetector;
    }

    [HttpGet("History")]
    public ActionResult<IReadOnlyList<ResolutionHistoryEntry>> GetHistory()
    {
        return Ok(_history.GetRecent());
    }

    [HttpDelete("History")]
    public ActionResult<IReadOnlyList<ResolutionHistoryEntry>> ClearHistory()
    {
        _history.Clear();
        return Ok(Array.Empty<ResolutionHistoryEntry>());
    }

    [HttpPost("Preview")]
    public ActionResult<IReadOnlyList<PreviewResponse>> Preview(
        [FromBody] PreviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest("A folder path is required.");
        }

        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(request.Path);
        }
        catch (Exception)
        {
            return BadRequest("The folder path is invalid.");
        }

        if (!Directory.Exists(fullPath))
        {
            return NotFound("The folder does not exist or is unavailable.");
        }

        FileSystemMetadata[] children;
        try
        {
            children = Directory
                .EnumerateFileSystemEntries(fullPath)
                .Select(CreateMetadata)
                .ToArray();
        }
        catch (Exception)
        {
            return NotFound("The folder could not be inspected.");
        }

        var configuration = CompanionPlugin.GetConfiguration();
        var results = new List<PreviewResponse>();
        if (!string.Equals(request.MediaType, "Series", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(ToResponse(
                "Movies",
                _movieDetector.Detect(
                    fullPath,
                    children,
                    configuration.NestedMoviesEnabled)));
        }

        if (!string.Equals(request.MediaType, "Movies", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(ToResponse(
                "Series",
                _seriesDetector.Detect(fullPath, children, configuration)));
        }

        return Ok(results);
    }

    private static FileSystemMetadata CreateMetadata(string path)
    {
        var isDirectory = Directory.Exists(path);
        return new FileSystemMetadata
        {
            Exists = isDirectory || System.IO.File.Exists(path),
            FullName = path,
            IsDirectory = isDirectory,
            Name = System.IO.Path.GetFileName(path)
        };
    }

    private static PreviewResponse ToResponse(
        string module,
        Core.ResolverDecision decision)
    {
        return new PreviewResponse(
            module,
            decision.Accepted,
            decision.OuterPath,
            decision.ResolvedPath,
            decision.ReasonCode,
            decision.HumanReadableReason,
            decision.DetectedName,
            decision.DetectedYear,
            decision.Evidence);
    }
}
