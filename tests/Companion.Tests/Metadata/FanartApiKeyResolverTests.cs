using System.Reflection;
using Xunit;

namespace RussianMetadata.Tests
{
    public sealed class FanartApiKeyResolverTests
    {
        [Fact]
        public void ResolveFromAssemblies_ReadsOfficialPluginConstant()
        {
            var result = FanartApiKeyResolver.ResolveFromAssemblies(
                [Assembly.GetExecutingAssembly()]);

            Assert.Equal("test-fanart-key", result);
        }
    }
}

namespace Jellyfin.Plugin.Fanart
{
    public static class Plugin
    {
        public const string ApiKey = "test-fanart-key";
    }
}
