using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Moq;

public sealed class LibraryPolicyTests : IDisposable
{
    private readonly CompanionTestContext _context = new(false);
    public void Dispose() => _context.Dispose();
    [Theory]
    [InlineData(false,false,false)] [InlineData(false,false,true)]
    [InlineData(false,true,false)] [InlineData(false,true,true)]
    [InlineData(true,false,false)] [InlineData(true,false,true)]
    [InlineData(true,true,false)] [InlineData(true,true,true)]
    public void EveryModuleCombinationIsIndependent(bool resolver, bool metadata, bool artwork)
    {
        _context.Config.AllLibraries = true;
        _context.Config.ResolverEnabled = resolver; _context.Config.MetadataEnabled = metadata; _context.Config.ArtworkEnabled = artwork;
        var path = Path.Combine(_context.Root,"movie.mkv");
        Assert.Equal(resolver, LibraryPolicy.AllowsPath(CompanionModule.Resolver,path));
        Assert.Equal(metadata, LibraryPolicy.AllowsPath(CompanionModule.Metadata,path));
        Assert.Equal(artwork, LibraryPolicy.AllowsPath(CompanionModule.Artwork,path));
        _context.Config.Enabled = false;
        foreach (var module in Enum.GetValues<CompanionModule>()) Assert.False(LibraryPolicy.AllowsPath(module,path));
    }
    [Fact]
    public void ExplicitRulesSurviveRenameAndDoNotMatchSiblingPrefix()
    {
        var id = Guid.NewGuid(); var path = Path.Combine(_context.Root,"movies");
        _context.Manager.Setup(value => value.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        { new() { ItemId=id.ToString(),Name="Renamed",Locations=[path] } });
        _context.Config.Libraries = [new LibraryRule { LibraryId=id,Metadata=true,Artwork=false,Resolver=false }];
        Assert.True(LibraryPolicy.AllowsPath(CompanionModule.Metadata,Path.Combine(path,"a.mkv")));
        Assert.False(LibraryPolicy.AllowsPath(CompanionModule.Artwork,Path.Combine(path,"a.mkv")));
        Assert.False(LibraryPolicy.AllowsPath(CompanionModule.Metadata,Path.Combine(path+"-other","a.mkv")));
    }
    [Fact]
    public void NestedExcludedLibraryWinsOverEnabledParent()
    {
        var parent=Guid.NewGuid(); var child=Guid.NewGuid(); var path=Path.Combine(_context.Root,"private");
        _context.Config.AllLibraries=true; _context.Config.Libraries=[new LibraryRule { LibraryId=child,Metadata=false }];
        _context.Manager.Setup(value=>value.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        { new() {ItemId=parent.ToString(),Locations=[_context.Root]}, new() {ItemId=child.ToString(),Locations=[path]} });
        Assert.False(LibraryPolicy.AllowsPath(CompanionModule.Metadata,Path.Combine(path,"a.mkv")));
        Assert.True(LibraryPolicy.AllowsPath(CompanionModule.Metadata,Path.Combine(_context.Root,"a.mkv")));
    }
    [Fact]
    public void SharedPathRequiresAllLibrariesToAllowModule()
    {
        var first=Guid.NewGuid(); var second=Guid.NewGuid();
        _context.Config.AllLibraries=true; _context.Config.Libraries=[new LibraryRule {LibraryId=second,Artwork=false}];
        _context.Manager.Setup(value=>value.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        { new() {ItemId=first.ToString(),Locations=[_context.Root]}, new() {ItemId=second.ToString(),Locations=[_context.Root]} });
        Assert.False(LibraryPolicy.AllowsPath(CompanionModule.Artwork,Path.Combine(_context.Root,"a.mkv")));
    }
    [Fact]
    public void CollectionsUseExplicitPolicyAndGlobalModuleSwitch()
    {
        var collection=new BoxSet { Id=Guid.NewGuid() };
        Assert.False(LibraryPolicy.Allows(CompanionModule.Metadata,collection));
        _context.Config.CollectionMetadataEnabled=true;
        Assert.True(LibraryPolicy.Allows(CompanionModule.Metadata,collection));
        Assert.False(LibraryPolicy.Allows(CompanionModule.Artwork,collection));
        _context.Config.MetadataEnabled=false; Assert.False(LibraryPolicy.Allows(CompanionModule.Metadata,collection));
    }
    [Fact]
    public void UnknownPathAndLoadedLegacyModuleFailClosed()
    {
        _context.Config.AllLibraries=true; Assert.False(LibraryPolicy.AllowsPath(CompanionModule.Metadata,null));
        CompanionPlugin.BlockedModules=[CompanionModule.Artwork];
        Assert.False(LibraryPolicy.AllowsPath(CompanionModule.Artwork,Path.Combine(_context.Root,"a.mkv")));
        Assert.True(LibraryPolicy.AllowsPath(CompanionModule.Metadata,Path.Combine(_context.Root,"a.mkv")));
    }
    [Fact]
    public void ResolvingRootWhileLoadingLibrariesDoesNotRecurse()
    {
        _context.Manager.Setup(value=>value.GetVirtualFolders()).Returns(()=>
        {
            Assert.False(LibraryPolicy.AllowsPath(CompanionModule.Resolver,_context.Root));
            return new List<VirtualFolderInfo>();
        });
        Assert.Empty(CompanionPlugin.Policy!.GetLibraries());
    }
}
