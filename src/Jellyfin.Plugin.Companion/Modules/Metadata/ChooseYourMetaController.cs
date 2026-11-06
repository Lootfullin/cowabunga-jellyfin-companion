using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Companion;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RussianMetadata;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("ChooseYourMeta")]
public sealed class ChooseYourMetaController : ControllerBase
{
    private readonly LibraryConfigurationService _configurationService;
    private readonly ArtworkPreferenceRefreshService _artworkRefreshService;
    private readonly ILibraryManager _library;

    public ChooseYourMetaController(
        LibraryConfigurationService configurationService,
        ArtworkPreferenceRefreshService artworkRefreshService,
        ILibraryManager library)
    {
        _configurationService = configurationService;
        _artworkRefreshService = artworkRefreshService;
        _library = library;
    }

    [HttpPost("ConfigureLibraries")]
    public ActionResult<LibraryConfigurationResult> ConfigureLibraries()
    {
        return Ok(_configurationService.Apply());
    }

    [HttpPost("RefreshArtworkPreferences")]
    public ActionResult<ArtworkPreferenceRefreshResult> RefreshArtworkPreferences(
        [FromBody] ArtworkPreferenceRefreshRequest request)
    {
        _configurationService.Apply();
        return Ok(_artworkRefreshService.Queue(request));
    }

    [HttpGet("MovieYearAudit")]
    public ActionResult<MovieYearAuditResult> CheckMovieYears(CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.Enabled(CompanionModule.Metadata))
        {
            return Ok(new MovieYearAuditResult(false, 0, []));
        }

        var mismatches = new List<MovieYearMismatch>();
        var checkedMovies = 0;
        foreach (var movie in _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie], Recursive = true, CollapseBoxSetItems = false
        }).OfType<Movie>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!LibraryPolicy.Allows(CompanionModule.Metadata, movie)) continue;
            checkedMovies++;
            if (MovieYearAudit.Check(movie) is { } mismatch) mismatches.Add(mismatch);
        }

        return Ok(new MovieYearAuditResult(true, checkedMovies,
            mismatches.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ToArray()));
    }

    [HttpGet("MovieYearAudit/{id:guid}/Candidates")]
    public async Task<ActionResult<IReadOnlyList<RemoteSearchResult>>> GetMovieYearCandidates(
        Guid id, CancellationToken cancellationToken)
    {
        if (_library.GetItemById(id) is not Movie movie || !LibraryPolicy.Allows(CompanionModule.Metadata, movie)
            || MovieYearAudit.Check(movie) is not { } mismatch)
        {
            return NotFound();
        }

        var config = CompanionPlugin.GetConfiguration();
        var key = TmdbApiKeyResolver.Resolve(config);
        if (string.IsNullOrWhiteSpace(key)) return StatusCode(503);

        using var client = new HttpClient(RussianMovieProvider.CreateProxyHandler(config)) { Timeout = TimeSpan.FromSeconds(5) };
        var candidates = await MovieManualSearch.SearchAsync(client, key,
            new MovieInfo { Name = mismatch.SearchTitle, Year = mismatch.FileYear },
            CompanionPlugin.MetadataProviderName, cancellationToken, limit: 5);
        return Ok(candidates.Where(candidate => MovieLookup.ScoreYear(mismatch.FileYear, candidate.ProductionYear) > 0).ToArray());
    }

    [HttpGet("Status")]
    public ActionResult<ChooseYourMetaStatus> GetStatus()
    {
        var configuration =
            CompanionPlugin.Instance?.Configuration ?? new Jellyfin.Plugin.Companion.Configuration.PluginConfiguration();
        return Ok(new ChooseYourMetaStatus(
            !string.IsNullOrWhiteSpace(
                TmdbApiKeyResolver.Resolve(configuration)),
            !string.IsNullOrWhiteSpace(FanartApiKeyResolver.Resolve())));
    }
}

public sealed record ChooseYourMetaStatus(
    bool JellyfinTmdbAvailable,
    bool JellyfinFanartAvailable);

public sealed record ArtworkPreferenceRefreshRequest(
    bool MoviePosters,
    bool MovieLogos,
    bool CollectionPosters,
    bool CollectionLogos);
