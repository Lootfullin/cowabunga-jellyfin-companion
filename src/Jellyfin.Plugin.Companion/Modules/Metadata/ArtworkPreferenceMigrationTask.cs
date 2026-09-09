using MediaBrowser.Model.Tasks;
using Jellyfin.Plugin.Companion.Configuration;

namespace RussianMetadata;

public sealed class ArtworkPreferenceMigrationTask : IScheduledTask
{
    private const int CurrentSchemaVersion = 1;
    private readonly LibraryConfigurationService _configurationService;
    private readonly ArtworkPreferenceRefreshService _artworkRefreshService;

    public ArtworkPreferenceMigrationTask(
        LibraryConfigurationService configurationService,
        ArtworkPreferenceRefreshService artworkRefreshService)
    {
        _configurationService = configurationService;
        _artworkRefreshService = artworkRefreshService;
    }

    public string Name => "Применить настройки источников изображений";

    public string Key => "ChooseYourMetaMigrateArtworkPreferences";

    public string Description =>
        "Один раз обновляет сохранённые изображения после изменения порядка и настроек источников.";

    public string Category => "Cowabunga Jellyfin Companion";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!LibraryPolicy.Enabled(CompanionModule.Metadata)) return Task.CompletedTask;

        cancellationToken.ThrowIfCancellationRequested();
        var plugin = CompanionPlugin.Instance;
        if (plugin is null
            || plugin.Configuration.ArtworkRefreshSchemaVersion >= CurrentSchemaVersion)
        {
            progress.Report(100);
            return Task.CompletedTask;
        }

        _configurationService.Apply();
        _artworkRefreshService.Queue(CreateRefreshRequest(plugin.Configuration));
        plugin.Configuration.ArtworkRefreshSchemaVersion = CurrentSchemaVersion;
        plugin.SaveConfiguration();
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }

    internal static ArtworkPreferenceRefreshRequest CreateRefreshRequest(
        PluginConfiguration configuration)
    {
        return new ArtworkPreferenceRefreshRequest(
            MoviePosters: configuration.ForeignMoviePosterPreference != ArtworkLanguagePreference.Disabled
                || configuration.RussianMoviePosterPreference != ArtworkLanguagePreference.Disabled,
            MovieLogos: configuration.ForeignMovieLogoPreference != ArtworkLanguagePreference.Disabled
                || configuration.RussianMovieLogoPreference != ArtworkLanguagePreference.Disabled,
            CollectionPosters: configuration.CollectionPosterPreference != ArtworkLanguagePreference.Disabled,
            CollectionLogos: configuration.CollectionLogoPreference != ArtworkLanguagePreference.Disabled);
    }
}
