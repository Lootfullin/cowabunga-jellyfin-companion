using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Companion;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("CowabungaCompanion")]
public sealed class CompanionController(LibraryPolicy policy, LegacyMigration migration, ArtworkCoordinator images) : ControllerBase
{
    [HttpGet("Status")]
    public ActionResult GetStatus() => Ok(new
    {
        Name = "Cowabunga Jellyfin Companion",
        Version = typeof(CompanionController).Assembly.GetName().Version?.ToString(),
        Libraries = policy.GetLibraries().Select(folder => new { Id = folder.ItemId, folder.Name, Type = folder.CollectionType?.ToString() }),
        ActiveLegacyPlugins = migration.ActiveLegacyPlugins(),
        LegacyConfigurations = migration.AvailableConfigurations(),
        PendingImages = images.PendingCount
    });

    [HttpPost("ImportLegacy")]
    public ActionResult ImportLegacy()
    {
        try { return Ok(new { Imported = migration.Import() }); }
        catch (Exception error) when (error is System.Xml.XmlException or FormatException or ArgumentException)
        { return BadRequest("One of the legacy configurations is invalid. No settings were imported."); }
    }
}
