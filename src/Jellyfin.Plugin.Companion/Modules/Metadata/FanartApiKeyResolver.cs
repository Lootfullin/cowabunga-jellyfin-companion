using System;
using System.Collections.Generic;
using System.Reflection;

namespace RussianMetadata;

/// <summary>
/// Resolves the project API key bundled with Jellyfin's official Fanart plugin.
/// Choose your Meta does not copy or persist that key and only uses Fanart when
/// the matching plugin is installed and loaded.
/// </summary>
internal static class FanartApiKeyResolver
{
    private const string FanartPluginTypeName = "Jellyfin.Plugin.Fanart.Plugin";

    internal static string? Resolve()
    {
        return ResolveFromAssemblies(AppDomain.CurrentDomain.GetAssemblies());
    }

    internal static string? ResolveFromAssemblies(
        IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var pluginType = assembly.GetType(
                FanartPluginTypeName,
                throwOnError: false,
                ignoreCase: false);
            var field = pluginType?.GetField(
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
