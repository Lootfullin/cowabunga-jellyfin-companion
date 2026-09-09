using MediaBrowser.Model.Entities;
using Xunit;

namespace RussianMetadata.Tests;

public sealed class ArtworkPreferenceRefreshServiceTests
{
    [Theory]
    [InlineData(false, false, new ImageType[0])]
    [InlineData(true, false, new[] { ImageType.Primary })]
    [InlineData(false, true, new[] { ImageType.Logo })]
    [InlineData(true, true, new[] { ImageType.Primary, ImageType.Logo })]
    public void GetImageTypesQueuesOnlyChangedRoles(
        bool posters,
        bool logos,
        ImageType[] expected)
    {
        Assert.Equal(expected, ArtworkPreferenceRefreshService.GetImageTypes(posters, logos));
    }
}
