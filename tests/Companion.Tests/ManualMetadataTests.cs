using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using RussianMetadata;

public sealed class ManualMetadataTests
{
    [Fact]
    public void PathlessSearchIsAllowedButCannotAuthorizeMetadataWrites()
    {
        using var context = new CompanionTestContext(false);
        Assert.True(LibraryPolicy.AllowsMetadataSearch(null));
        Assert.True(LibraryPolicy.AllowsMetadataSearch(""));
        Assert.False(LibraryPolicy.AllowsPath(CompanionModule.Metadata, null));
        Assert.False(LibraryPolicy.AllowsMetadataSearch(Path.Combine(context.Root, "excluded.mkv")));
        context.Config.MetadataEnabled = false;
        Assert.False(LibraryPolicy.AllowsMetadataSearch(null));
    }

    [Fact]
    public void RussianDetailsReplaceEnglishTextAfterManualIdentificationAndAreIdempotent()
    {
        var item = new Movie { Overview = "English overview", Tagline = "English tagline" };
        var config = new PluginConfiguration();
        Assert.True(MovieTextLocalization.ApplyDescription(item, "Русское описание", "Русский слоган", config));
        Assert.Equal("Русское описание", item.Overview);
        Assert.Equal("Русский слоган", item.Tagline);
        Assert.False(MovieTextLocalization.ApplyDescription(item, "Русское описание", "Русский слоган", config));
    }

    [Fact]
    public void MissingRussianTranslationDoesNotEraseExistingText()
    {
        var item = new Movie { Overview = "English overview", Tagline = "English tagline" };
        Assert.False(MovieTextLocalization.ApplyDescription(item, "English fallback", null, new PluginConfiguration()));
        Assert.Equal("English overview", item.Overview);
        Assert.Equal("English tagline", item.Tagline);
    }

    [Fact]
    public void DisabledFieldsAndLocksAreRespected()
    {
        var item = new Movie { Overview = "Original", Tagline = "Original", LockedFields = [MetadataField.Overview] };
        var config = new PluginConfiguration { EnableRussianTaglines = false };
        Assert.False(MovieTextLocalization.ApplyDescription(item, "Описание", "Слоган", config));
        item.LockedFields = [];
        config.EnableRussianOverviews = false;
        Assert.False(MovieTextLocalization.ApplyDescription(item, "Описание", "Слоган", config));
        config.EnableRussianOverviews = true;
        config.EnableRussianTaglines = true;
        item.IsLocked = true;
        Assert.False(MovieTextLocalization.ApplyDescription(item, "Описание", "Слоган", config));
    }
}
