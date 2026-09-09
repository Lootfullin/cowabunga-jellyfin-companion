using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Companion.Configuration;

namespace RussianMetadata;

/// <summary>
/// Resolves the TMDb API key bundled with the matching Jellyfin server.
/// This intentionally follows Jellyfin's internal implementation and therefore
/// must be compatibility-tested for every supported Jellyfin release.
/// </summary>
internal static class TmdbApiKeyResolver
{
    private const string TmdbUtilsTypeName =
        "MediaBrowser.Providers.Plugins.Tmdb.TmdbUtils";

    internal static string? Resolve(PluginConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.TmdbApiKey))
        {
            return configuration.TmdbApiKey.Trim();
        }

        return ResolveFromAssemblies(AppDomain.CurrentDomain.GetAssemblies());
    }

    internal static string? ResolveFromAssemblies(
        IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var tmdbUtils = assembly.GetType(
                TmdbUtilsTypeName,
                throwOnError: false,
                ignoreCase: false);
            var field = tmdbUtils?.GetField(
                "ApiKey",
                BindingFlags.Public | BindingFlags.Static);
            if (field?.GetRawConstantValue() is string apiKey
                && !string.IsNullOrWhiteSpace(apiKey))
            {
                return apiKey;
            }
        }

        return null;
    }
}
