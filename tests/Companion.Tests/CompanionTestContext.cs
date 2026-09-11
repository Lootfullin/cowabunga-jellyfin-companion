using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Moq;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal sealed class CompanionTestContext : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "companion-tests", Guid.NewGuid().ToString("N"));
    public Mock<ILibraryManager> Manager { get; } = new();
    public CompanionPlugin Plugin { get; }
    public PluginConfiguration Config => Plugin.Configuration;
    public CompanionTestContext(bool allLibraries = true)
    {
        Directory.CreateDirectory(Root);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(value => value.PluginsPath).Returns(Root);
        paths.SetupGet(value => value.DataPath).Returns(Path.Combine(Root, "data"));
        paths.SetupGet(value => value.PluginConfigurationsPath).Returns(Path.Combine(Root, "configurations"));
        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(value => value.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration { AllLibraries = allLibraries });
        Plugin = new CompanionPlugin(paths.Object, serializer.Object);
        Manager.Setup(value => value.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = Guid.NewGuid().ToString(), Name = "Test library", Locations = [Path.GetPathRoot(Root)!, Directory.GetCurrentDirectory()] }
        });
        CompanionPlugin.Policy = new LibraryPolicy(Manager.Object);
        CompanionPlugin.Libraries = null;
        CompanionPlugin.Images = null;
        CompanionPlugin.BlockedModules = [];
    }
    public void Dispose()
    {
        CompanionPlugin.Policy = null;
        CompanionPlugin.Images = null;
        CompanionPlugin.Libraries = null;
        CompanionPlugin.BlockedModules = [];
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
