using Jellyfin.Plugin.CustomArtwork;
using Jellyfin.Plugin.SmartResolver.Diagnostics;
using Jellyfin.Plugin.SmartResolver.Modules.Movies;
using Jellyfin.Plugin.SmartResolver.Modules.NestedSeries;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RussianMetadata;

namespace Jellyfin.Plugin.Companion;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<LibraryPolicy>();
        services.AddSingleton<LibraryConfigurator>();
        services.AddSingleton<LegacyMigration>();
        services.AddSingleton<ArtworkCoordinator>();
        services.AddHostedService<CompanionStartup>();
        services.AddHostedService(provider => provider.GetRequiredService<ArtworkCoordinator>());
        services.AddSingleton<ResolutionHistory>();
        services.AddSingleton<MovieFileDetector>();
        services.AddSingleton<OuterFolderMatcher>();
        services.AddSingleton<SeriesRootEvidenceDetector>();
        services.AddSingleton<NestedSeriesDetector>();
        services.AddSingleton<IItemResolver, MovieFileResolver>();
        services.AddSingleton<IItemResolver, NestedSeriesResolver>();
        services.AddSingleton<ArtworkIndex>();
        services.AddSingleton<ArtworkMediaWriter>();
        services.AddSingleton<ArtworkLibraryConfigurator>();
        services.AddSingleton<LibraryConfigurationService>();
        services.AddSingleton<ArtworkPreferenceRefreshService>();
    }
}

internal sealed class CompanionStartup(LibraryPolicy policy, LibraryConfigurator configurator, ArtworkCoordinator images,
    LegacyMigration migration, IServerApplicationHost applicationHost, ILogger<CompanionStartup> logger) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        CompanionPlugin.Policy = policy;
        CompanionPlugin.Libraries = configurator;
        CompanionPlugin.Images = images;
        CompanionPlugin.BlockedModules = migration.BlockedModules();
        return base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!applicationHost.CoreStartupHasCompleted)
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { configurator.Apply(); }
            catch (Exception error) { logger.LogWarning(error, "Companion could not apply library settings; will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        CompanionPlugin.Policy = null;
        CompanionPlugin.Libraries = null;
        CompanionPlugin.Images = null;
        return base.StopAsync(cancellationToken);
    }
}
