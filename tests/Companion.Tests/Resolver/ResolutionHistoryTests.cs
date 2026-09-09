using Jellyfin.Plugin.SmartResolver.Core;
using Jellyfin.Plugin.SmartResolver.Diagnostics;
using Xunit;

namespace Jellyfin.Plugin.SmartResolver.Tests;

public sealed class ResolutionHistoryTests
{
    [Fact]
    public void GetRecent_ReturnsNewestEntryFirst()
    {
        var history = new ResolutionHistory();
        history.Add("Movies", Accepted("First"));
        history.Add("Series", Accepted("Second"));

        var entries = history.GetRecent();

        Assert.Equal(2, entries.Count);
        Assert.Equal("Second", entries[0].DetectedName);
        Assert.Equal("First", entries[1].DetectedName);
    }

    [Fact]
    public void Add_KeepsOnlyLatestTwoHundredEntries()
    {
        var history = new ResolutionHistory();
        for (var index = 0; index < 250; index++)
        {
            history.Add("Movies", Accepted(index.ToString()));
        }

        var entries = history.GetRecent();

        Assert.Equal(200, entries.Count);
        Assert.Equal("249", entries[0].DetectedName);
        Assert.Equal("50", entries[^1].DetectedName);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var history = new ResolutionHistory();
        history.Add("Movies", Accepted("Movie"));

        history.Clear();

        Assert.Empty(history.GetRecent());
    }

    private static ResolverDecision Accepted(string name)
    {
        return ResolverDecision.Accept(
            "Outer",
            "Resolved",
            name,
            2025,
            ResolverEvidence.MovieVideoFile);
    }
}
