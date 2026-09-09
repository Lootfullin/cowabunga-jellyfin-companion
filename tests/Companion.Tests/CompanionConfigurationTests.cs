using System.Xml.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

public sealed class CompanionConfigurationTests : IDisposable
{
    private readonly CompanionTestContext _context = new();
    public void Dispose()=>_context.Dispose();
    [Fact]
    public void ConfigurationRoundTripsAllModulesAndLibraryIds()
    {
        var config=_context.Config; var id=Guid.NewGuid();
        config.Libraries=[new LibraryRule {LibraryId=id,Resolver=false,Metadata=true,Artwork=false}];
        config.CustomRegex="Сериал.*";config.ForeignMovieLogoPreference=ArtworkLanguagePreference.RussianFirst;
        config.StorageMode=PluginConfiguration.MediaFolderStorage;
        var serializer=new XmlSerializer(typeof(PluginConfiguration));
        using var writer=new StringWriter();serializer.Serialize(writer,config);
        using var reader=new StringReader(writer.ToString());var copy=(PluginConfiguration)serializer.Deserialize(reader)!;
        Assert.Equal(id,Assert.Single(copy.Libraries).LibraryId);
        Assert.False(copy.Libraries[0].Resolver);Assert.Equal("Сериал.*",copy.CustomRegex);
        Assert.Equal(config.ForeignMovieLogoPreference,copy.ForeignMovieLogoPreference);Assert.Equal(config.StorageMode,copy.StorageMode);
    }
    [Fact]
    public void OneLibraryWriterPreservesOtherProvidersAndDisablingRemovesOnlyOwnEntries()
    {
        var options=new LibraryOptions { TypeOptions=[new TypeOptions { Type="Movie",MetadataFetchers=["Nfo","TheMovieDb"],ImageFetchers=["TheMovieDb","Fanart"] }] };
        Assert.True(LibraryConfigurator.ApplyOptions(options,CollectionTypeOptions.movies,true,true));
        var movie=options.TypeOptions.Single(type=>type.Type=="Movie");
        Assert.Equal(new[]{CompanionPlugin.ArtworkProviderName,CompanionPlugin.MetadataImageProviderName,"TheMovieDb","Fanart"},movie.ImageFetchers);
        Assert.False(LibraryConfigurator.ApplyOptions(options,CollectionTypeOptions.movies,true,true));
        Assert.True(LibraryConfigurator.ApplyOptions(options,CollectionTypeOptions.movies,false,false));
        Assert.Equal(new[]{"TheMovieDb","Fanart"},movie.ImageFetchers);Assert.Equal(new[]{"Nfo","TheMovieDb"},movie.MetadataFetchers);
    }
    [Fact]
    public void ImportResolverSwitchDoesNotDisableWholePluginOrExpandLibraries()
    {
        _context.Config.AllLibraries=false;
        LegacyMigration.ApplyXml(_context.Config,XDocument.Parse("<PluginConfiguration><Enabled>false</Enabled><MoviesEnabled>false</MoviesEnabled></PluginConfiguration>"),["Enabled","MoviesEnabled"],true);
        Assert.True(_context.Config.Enabled);Assert.False(_context.Config.ResolverEnabled);
        Assert.False(_context.Config.MoviesEnabled);Assert.False(_context.Config.AllLibraries);
    }
    [Fact]
    public void CollectionOnlyConfigurationWorksWithoutEnablingMovieModules()
    {
        _context.Config.CollectionMetadataEnabled=true;
        var options=new LibraryOptions();
        Assert.True(LibraryConfigurator.ApplyOptions(options,CollectionTypeOptions.movies,false,false));
        var collection=Assert.Single(options.TypeOptions);
        Assert.Equal("BoxSet",collection.Type);
        Assert.Equal(new[]{CompanionPlugin.MetadataProviderName},collection.MetadataFetchers);
    }
    [Fact]
    public void AssemblyHasOnePluginAndAllConfigurationResources()
    {
        var assembly=typeof(CompanionPlugin).Assembly;
        Assert.Single(assembly.GetTypes(),type=>typeof(MediaBrowser.Common.Plugins.IPlugin).IsAssignableFrom(type)&&!type.IsAbstract);
        Assert.Equal("Cowabunga Jellyfin Companion",_context.Plugin.Name);
        foreach(var page in _context.Plugin.GetPages()) Assert.NotNull(assembly.GetManifestResourceStream(page.EmbeddedResourcePath));
    }
}
