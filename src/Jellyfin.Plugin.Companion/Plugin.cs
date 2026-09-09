using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Companion;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "0a281de0-d3c8-43ef-bf2d-5fac17fbb8c6";
    public const string MetadataProviderName = "Cowabunga Companion Metadata";
    public const string MetadataImageProviderName = "Cowabunga Companion Language Artwork";
    public const string ArtworkProviderName = "Cowabunga Companion Custom Artwork";
    public static Plugin? Instance { get; private set; }
    internal static LibraryPolicy? Policy { get; set; }
    internal static LibraryConfigurator? Libraries { get; set; }
    internal static ArtworkCoordinator? Images { get; set; }
    internal static CompanionModule[] BlockedModules { get; set; } = [];
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer) => Instance = this;
    public override string Name => "Cowabunga Jellyfin Companion";
    public override Guid Id => Guid.Parse(PluginGuid);
    public override string Description => "Media recognition, localized metadata and Cowabunga artwork with per-library controls.";
    internal static PluginConfiguration GetConfiguration() => Instance?.Configuration ?? new();
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var config = (PluginConfiguration)configuration;
        config.Libraries ??= [];
        config.Libraries = config.Libraries.Where(rule => rule is not null && rule.LibraryId != Guid.Empty)
            .GroupBy(rule => rule.LibraryId).Select(group => group.Last()).ToArray();
        config.RefreshIntervalMinutes = Math.Clamp(config.RefreshIntervalMinutes, 1, 1440);
        if (config.StorageMode is not (PluginConfiguration.JellyfinStorage or PluginConfiguration.MediaFolderStorage))
            throw new ArgumentException("Unknown artwork storage mode.");
        base.UpdateConfiguration(config);
        Policy?.Invalidate();
        Libraries?.Apply();
    }
    public IEnumerable<PluginPageInfo> GetPages()
    {
        foreach (var page in new[] { "Companion", "Resolver", "Metadata", "Artwork" })
            yield return new PluginPageInfo
            {
                Name = "Cowabunga" + page,
                DisplayName = page == "Companion" ? Name : $"{Name}: {page}",
                EmbeddedResourcePath = $"Jellyfin.Plugin.Companion.Web.{page}.html"
            };
    }
}
