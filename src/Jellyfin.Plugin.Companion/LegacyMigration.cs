using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;

namespace Jellyfin.Plugin.Companion;

public sealed class LegacyMigration(IApplicationPaths paths, IPluginManager plugins)
{
    internal static readonly Guid[] LegacyIds =
    [
        Guid.Parse("c61d7897-a923-4a6d-9d4d-c6c911f28e73"),
        Guid.Parse("a8f3c2e1-4b5d-6e7f-8a9b-0c1d2e3f4a5b"),
        Guid.Parse("6f2d1a54-9c6e-4f2b-9a7d-5c1e2b8a44f1")
    ];
    private static readonly string[] Files = ["Jellyfin.Plugin.SmartResolver.xml", "RussianMetadata.xml", "Jellyfin.Plugin.CustomArtwork.xml"];
    private static readonly string[][] Fields =
    [
        ["Enabled", "NestedSeriesEnabled", "NestedSeriesMode", "CustomRegex", "RequireExactlyOneEligibleChild", "MoviesEnabled", "NestedMoviesEnabled", "EnableResolutionLogs", "EnableRejectionLogs"],
        ["TmdbApiKey", "EnableRussianTitles", "EnableRussianOverviews", "EnableRussianTaglines", "EnableRussianGenres", "EnableRussianStudios", "EnableRussianPeople", "ForeignMoviePosterPreference", "ForeignMovieLogoPreference", "RussianMoviePosterPreference", "RussianMovieLogoPreference", "CollectionPosterPreference", "CollectionLogoPreference", "ProxyUrl", "ProxyUsername", "ProxyPassword"],
        ["RefreshIntervalMinutes", "Posters", "Logos", "StorageMode", "OverwriteExistingMediaFiles"]
    ];

    public string[] ActiveLegacyPlugins() => plugins.Plugins
        .Where(plugin => LegacyIds.Contains(plugin.Id) && plugin.Instance is not null)
        .Select(plugin => plugin.Name).Distinct().ToArray();
    public CompanionModule[] BlockedModules() => plugins.Plugins
        .Where(plugin => LegacyIds.Contains(plugin.Id) && plugin.Instance is not null)
        .Select(plugin => (CompanionModule)Array.IndexOf(LegacyIds, plugin.Id)).Distinct().ToArray();
    public string[] AvailableConfigurations() => Files.Where(file => File.Exists(Path.Combine(paths.PluginConfigurationsPath, file))).ToArray();

    public string[] Import()
    {
        var plugin = CompanionPlugin.Instance!;
        // Clone before editing: malformed legacy XML must not partly change live settings.
        var config = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>(
            System.Text.Json.JsonSerializer.Serialize(plugin.Configuration))!;
        var imported = new List<string>();
        for (var i = 0; i < Files.Length; i++)
        {
            var path = Path.Combine(paths.PluginConfigurationsPath, Files[i]);
            if (!File.Exists(path)) continue;
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
            });
            ApplyXml(config, XDocument.Load(reader), Fields[i], i == 0);
            imported.Add(Files[i]);
        }
        // Preserve ownership and stable collection matching. Never overwrite newer Companion state.
        var legacyData = plugins.Plugins.Where(value => value.Id == LegacyIds[2])
            .OrderByDescending(value => value.Version).SelectMany(value => new[] { (value.Instance as BasePlugin)?.DataFolderPath, value.Path })
            .Concat(new[] { Path.Combine(paths.PluginsPath, "Jellyfin.Plugin.CustomArtwork") })
            .Where(path => path is not null && Directory.Exists(path)).Distinct().ToArray();
        if (legacyData.Length > 0)
        {
            Directory.CreateDirectory(plugin.DataFolderPath);
            foreach (var file in new[] { "managed-media-files.v1.json", "artwork-state.v1.json", "artwork-index.v1.json" })
            {
                var source = legacyData.Select(directory => Path.Combine(directory!, file)).FirstOrDefault(File.Exists);
                var target = Path.Combine(plugin.DataFolderPath, file);
                if (source is not null && !File.Exists(target)) File.Copy(source, target);
            }
        }
        CompanionPlugin.Images?.ImportLegacyOwnership(Path.Combine(plugin.DataFolderPath, "artwork-state.v1.json"));
        plugin.UpdateConfiguration(config);
        return imported.ToArray();
    }

    internal static void ApplyXml(PluginConfiguration config, XDocument xml, IEnumerable<string> fields, bool resolver)
    {
        foreach (var name in fields)
        {
            var value = xml.Root?.Element(name)?.Value;
            if (value is null) continue;
            var property = typeof(PluginConfiguration).GetProperty(resolver && name == "Enabled" ? "ResolverEnabled" : name)!;
            var converted = property.PropertyType.IsEnum ? Enum.Parse(property.PropertyType, value)
                : Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture);
            property.SetValue(config, converted);
        }
    }
}
