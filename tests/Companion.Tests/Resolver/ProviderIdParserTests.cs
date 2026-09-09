using Jellyfin.Plugin.SmartResolver.Core;
using Xunit;

namespace Jellyfin.Plugin.SmartResolver.Tests;

public sealed class ProviderIdParserTests
{
    [Theory]
    [InlineData("Movie (2025) [tmdbid-12345]", "12345")]
    [InlineData("Movie (2025) [TMDBID-987]", "987")]
    public void GetTmdbId_ReturnsNumericId(string value, string expected)
    {
        Assert.Equal(expected, ProviderIdParser.GetTmdbId(value));
    }

    [Theory]
    [InlineData("Movie (2025) [imdbid-tt1234567]", "tt1234567")]
    [InlineData("Movies/Movie [IMDBID-tt7654321]/Movie.mkv", "tt7654321")]
    public void GetImdbId_ReturnsImdbId(string value, string expected)
    {
        Assert.Equal(expected, ProviderIdParser.GetImdbId(value));
    }

    [Theory]
    [InlineData("Show (2025) [tvdbid-12345]", "12345")]
    [InlineData("Show [TVDBID-987]", "987")]
    public void GetTvdbId_ReturnsNumericId(string value, string expected)
    {
        Assert.Equal(expected, ProviderIdParser.GetTvdbId(value));
    }

    [Theory]
    [InlineData("Movie [tmdbid-not-a-number]")]
    [InlineData("Movie [imdbid-1234567]")]
    [InlineData("Movie")]
    public void Parser_RejectsInvalidOrMissingIds(string value)
    {
        Assert.Null(ProviderIdParser.GetTmdbId(value));
        Assert.Null(ProviderIdParser.GetImdbId(value));
        Assert.Null(ProviderIdParser.GetTvdbId(value));
    }
}
