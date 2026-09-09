using Xunit;

namespace RussianMetadata.Tests;

public sealed class BoxSetIdentityTests
{
    [Fact]
    public void ResolveMemberConsensus_AcceptsTwoMoviesFromSameCollection()
    {
        var result = ChooseYourMetaBoxSetProvider.ResolveMemberConsensus([735, 735]);

        Assert.Equal(735, result);
    }

    [Fact]
    public void ResolveMemberConsensus_RejectsSingleMovie()
    {
        var result = ChooseYourMetaBoxSetProvider.ResolveMemberConsensus([735]);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveMemberConsensus_RejectsMixedCollections()
    {
        var result = ChooseYourMetaBoxSetProvider.ResolveMemberConsensus([735, 263]);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveMemberConsensus_RejectsMovieWithoutCollection()
    {
        var result = ChooseYourMetaBoxSetProvider.ResolveMemberConsensus([735, null]);

        Assert.Null(result);
    }
}
