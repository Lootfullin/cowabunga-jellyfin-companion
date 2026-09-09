using MediaBrowser.Common.Api;
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

    public ChooseYourMetaController(
        LibraryConfigurationService configurationService,
        ArtworkPreferenceRefreshService artworkRefreshService)
    {
        _configurationService = configurationService;
        _artworkRefreshService = artworkRefreshService;
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
