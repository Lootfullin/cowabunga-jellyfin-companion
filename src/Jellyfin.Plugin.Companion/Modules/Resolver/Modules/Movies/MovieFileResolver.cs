using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartResolver.Core;
using Jellyfin.Plugin.SmartResolver.Diagnostics;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartResolver.Modules.Movies;

public sealed class MovieFileResolver : IItemResolver
{
    private readonly MovieFileDetector _detector;
    private readonly ResolutionHistory _history;
    private readonly ILogger<MovieFileResolver> _logger;

    public MovieFileResolver(
        MovieFileDetector detector,
        ResolutionHistory history,
        ILogger<MovieFileResolver> logger)
    {
        _detector = detector;
        _history = history;
        _logger = logger;
    }

    public ResolverPriority Priority => ResolverPriority.First;

    public BaseItem? ResolvePath(ItemResolveArgs args)
    {
        if (!LibraryPolicy.AllowsPath(CompanionModule.Resolver, args.Path)) return null;

        var configuration = CompanionPlugin.GetConfiguration();
        if (!configuration.Enabled || !configuration.MoviesEnabled)
        {
            return null;
        }

        if (!args.IsDirectory || args.Parent is null)
        {
            return null;
        }

        if (args.GetCollectionType() != CollectionType.movies)
        {
            return null;
        }

        var decision = _detector.Detect(
            args.Path,
            args.FileSystemChildren,
            configuration.NestedMoviesEnabled);
        _history.Add("Movies", decision);
        if (!decision.Accepted)
        {
            if (configuration.EnableRejectionLogs)
            {
                _logger.LogDebug(
                    "Jellyfin Smart Resolver rejected movie candidate {FolderPath}. Reason: {ReasonCode}",
                    decision.OuterPath,
                    decision.ReasonCode);
            }

            return null;
        }

        var movie = new Movie
        {
            Path = decision.ResolvedPath,
            Name = decision.DetectedName,
            ProductionYear = decision.DetectedYear,
            IsInMixedFolder = false,
            AdditionalParts = decision.AdditionalParts.ToArray(),
            LocalAlternateVersions = decision.AlternateVersions.ToArray(),
            VideoType = string.Equals(
                Path.GetExtension(decision.ResolvedPath),
                ".iso",
                StringComparison.OrdinalIgnoreCase)
                ? VideoType.Iso
                : VideoType.VideoFile
        };

        SetProviderIdsFromPath(movie);

        if (configuration.EnableResolutionLogs)
        {
            _logger.LogInformation(
                "Jellyfin Smart Resolver mapped movie folder {FolderPath} to video file {MoviePath}. Name: {MovieName}. Evidence: {Evidence}",
                decision.OuterPath,
                decision.ResolvedPath,
                decision.DetectedName,
                decision.Evidence);
        }

        return movie;
    }

    private static void SetProviderIdsFromPath(Movie movie)
    {
        var folderName = Path.GetFileName(Path.GetDirectoryName(movie.Path)) ?? string.Empty;
        var tmdbId = ProviderIdParser.GetTmdbId(folderName)
            ?? ProviderIdParser.GetTmdbId(Path.GetFileName(movie.Path));
        if (tmdbId is not null)
        {
            movie.TrySetProviderId(MetadataProvider.Tmdb, tmdbId);
        }

        var imdbId = ProviderIdParser.GetImdbId(movie.Path);
        if (imdbId is not null)
        {
            movie.TrySetProviderId(MetadataProvider.Imdb, imdbId);
        }
    }
}
