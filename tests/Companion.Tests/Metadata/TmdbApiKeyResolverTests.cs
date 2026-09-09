using System.Reflection;
using Jellyfin.Plugin.Companion.Configuration;
using Xunit;

namespace RussianMetadata.Tests
{
    public sealed class TmdbApiKeyResolverTests
    {
        [Fact]
        public void Resolve_PrefersExplicitOverride()
        {
            var configuration = new PluginConfiguration
            {
                TmdbApiKey = " override-key "
            };

            Assert.Equal(
                "override-key",
                TmdbApiKeyResolver.Resolve(configuration));
        }

        [Fact]
        public void ResolveFromAssemblies_ReadsJellyfinTmdbConstant()
        {
            var result = TmdbApiKeyResolver.ResolveFromAssemblies(
                [Assembly.GetExecutingAssembly()]);

            Assert.Equal("test-jellyfin-key", result);
        }
    }
}

namespace MediaBrowser.Providers.Plugins.Tmdb
{
    public static class TmdbUtils
    {
        public const string ApiKey = "test-jellyfin-key";
    }
}
