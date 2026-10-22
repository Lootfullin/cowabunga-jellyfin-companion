using Jellyfin.Plugin.CustomArtwork;
using Jellyfin.Plugin.SmartResolver.Diagnostics;
using Jellyfin.Plugin.SmartResolver.Modules.Movies;
using Jellyfin.Plugin.SmartResolver.Modules.NestedSeries;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
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
        services.AddSingleton<CollectionNameSynchronizer>();
        var original = services.LastOrDefault(s => s.ServiceType == typeof(ICollectionManager));
        if (original is not null)
        {
            services.Remove(original);
            services.Add(new ServiceDescriptor(typeof(ICollectionManager), provider =>
            {
                var inner = original.ImplementationInstance ?? original.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
                return new CollectionCreationGuard((ICollectionManager)inner, provider.GetRequiredService<ILibraryManager>(),
                    provider.GetRequiredService<CollectionNameSynchronizer>(), provider.GetRequiredService<IFileSystem>());
            }, original.Lifetime));
        }
    }
}

internal sealed class CompanionStartup(LibraryPolicy policy, LibraryConfigurator configurator, ArtworkCoordinator images,
    LegacyMigration migration, IServerApplicationHost applicationHost, ILogger<CompanionStartup> logger,
    CollectionNameSynchronizer collectionNames) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        CompanionPlugin.Policy = policy;
        CompanionPlugin.Libraries = configurator;
        CompanionPlugin.Images = images;
        CompanionPlugin.CollectionNames = collectionNames;
        collectionNames.Start();
        CompanionPlugin.BlockedModules = migration.BlockedModules();
        return base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!applicationHost.CoreStartupHasCompleted)
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        try { await collectionNames.ReconcileAsync(stoppingToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "Companion could not align collection names at startup"); }
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
        CompanionPlugin.CollectionNames = null;
        collectionNames.Dispose();
        return base.StopAsync(cancellationToken);
    }
}
