using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartResolver.Core;
using Jellyfin.Plugin.SmartResolver.Diagnostics;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartResolver.Modules.NestedSeries;

public sealed partial class NestedSeriesResolver : IItemResolver
{
    private readonly NestedSeriesDetector _detector;
    private readonly ResolutionHistory _history;
    private readonly ILogger<NestedSeriesResolver> _logger;

    public NestedSeriesResolver(
        NestedSeriesDetector detector,
        ResolutionHistory history,
        ILogger<NestedSeriesResolver> logger)
    {
        _detector = detector;
        _history = history;
        _logger = logger;
    }

    // Jellyfin's built-in SeriesResolver uses ResolverPriority.Second.
    public ResolverPriority Priority => ResolverPriority.First;

    public BaseItem? ResolvePath(ItemResolveArgs args)
    {
        if (!LibraryPolicy.AllowsPath(CompanionModule.Resolver, args.Path)) return null;

        var configuration = CompanionPlugin.GetConfiguration();
        if (!configuration.Enabled || !configuration.NestedSeriesEnabled)
        {
            return null;
        }

        if (!args.IsDirectory || args.Parent is null)
        {
            return null;
        }

        if (args.GetCollectionType() != CollectionType.tvshows)
        {
            return null;
        }

        if (args.HasParent<Series>() || args.HasParent<Season>())
        {
            return null;
        }

        var decision = _detector.Detect(
            args.Path,
            args.FileSystemChildren,
            configuration);
        _history.Add("Series", decision);
        if (!decision.Accepted)
        {
            if (configuration.EnableRejectionLogs)
            {
                _logger.LogDebug(
                    "Jellyfin Smart Resolver rejected nested series candidate {OuterPath}. Reason: {ReasonCode}",
                    decision.OuterPath,
                    decision.ReasonCode);
            }

            return null;
        }

        if (configuration.EnableResolutionLogs)
        {
            _logger.LogInformation(
                "Jellyfin Smart Resolver mapped outer folder {OuterPath} to nested series root {InnerPath}. Module: NestedSeries. Evidence: {Evidence}",
                decision.OuterPath,
                decision.ResolvedPath,
                decision.Evidence);
        }

        var series = new Series
        {
            Path = decision.ResolvedPath,
            Name = decision.DetectedName,
            ProductionYear = decision.DetectedYear
        };

        SetProviderIdsFromPath(series);
        return series;
    }

    private static void SetProviderIdsFromPath(Series series)
    {
        var tmdbId = ProviderIdParser.GetTmdbId(series.Path);
        if (tmdbId is not null)
        {
            series.TrySetProviderId(MetadataProvider.Tmdb, tmdbId);
        }

        var tvdbId = ProviderIdParser.GetTvdbId(series.Path);
        if (tvdbId is not null)
        {
            series.TrySetProviderId(MetadataProvider.Tvdb, tvdbId);
        }

        var imdbId = ProviderIdParser.GetImdbId(series.Path);
        if (imdbId is not null)
        {
            series.TrySetProviderId(MetadataProvider.Imdb, imdbId);
        }
    }
}
