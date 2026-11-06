using Jellyfin.Plugin.Companion.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using RussianMetadata;

public class MovieYearAuditTests
{
    [Theory]
    [InlineData("Hellboy (2019) [UHD FEL].mkv", "Hellboy", 2004, 2019)]
    [InlineData("Aladdin (2019) [UHD DR].mkv", "Aladdin", 1992, 2019)]
    [InlineData("The.Lion.King.2019.2160p.UHDRemux.mkv", "The Lion King", 1994, 2019)]
    [InlineData("Blade Runner 2049 (2017) [UHD DR].mkv", "Blade Runner 2049", 1982, 2017)]
    [InlineData("Alien³ (1992) [UHD DR].mkv", "Alien³", 1986, 1992)]
    [InlineData("Fantasia 2000 (2000) [FHD].mkv", "Fantasia 2000", 1940, 2000)]
    public void ReportsExplicitYearMismatchWithoutChangingMetadata(string fileName, string title, int savedYear, int fileYear)
    {
        var movie = MakeMovie(fileName, savedYear);
        var mismatch = Assert.IsType<MovieYearMismatch>(MovieYearAudit.Check(movie));
        Assert.Equal(title, mismatch.SearchTitle);
        Assert.Equal(fileYear, mismatch.FileYear);
        Assert.Equal(savedYear, movie.ProductionYear);
        Assert.Equal("1487", movie.GetProviderId(MetadataProvider.Tmdb));
        Assert.Equal("tt0167190", movie.GetProviderId(MetadataProvider.Imdb));
    }

    [Theory]
    [InlineData("Fantasia 2000 (2000) [FHD].mkv", 1999, "Fantasia 2000")]
    [InlineData("Blade Runner 2049.mkv", 2017, "Blade Runner 2049")]
    [InlineData("1917 [UHD].mkv", 2019, "1917")]
    [InlineData("2012.mkv", 2009, "2012")]
    [InlineData("Wonder.Woman.1984.2160p.mkv", 2020, "Wonder Woman 1984")]
    public void SkipsTitleNumbersAndOneYearDifferences(string fileName, int savedYear, string originalTitle)
    {
        var movie = MakeMovie(fileName, savedYear);
        movie.OriginalTitle = originalTitle;
        Assert.Null(MovieYearAudit.Check(movie));
    }

    [Fact]
    public async Task AuditOnlyShowsMoviesFromEnabledLibrariesAndRejectsExcludedCandidateRequests()
    {
        using var context = new CompanionTestContext(allLibraries: false);
        var moviesRoot = Path.Combine(context.Root, "Movies");
        var excludedRoot = Path.Combine(context.Root, "Excluded");
        var libraryId = Guid.NewGuid();
        context.Config.Libraries = [new LibraryRule { LibraryId = libraryId, Metadata = true }];
        context.Manager.Setup(manager => manager.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { ItemId = libraryId.ToString(), Locations = [moviesRoot] },
            new() { ItemId = Guid.NewGuid().ToString(), Locations = [excludedRoot] }
        });

        var included = MakeMovie(Path.Combine(moviesRoot, "Hellboy (2019).mkv"), 2004);
        var excluded = MakeMovie(Path.Combine(excludedRoot, "Aladdin (2019).mkv"), 1992);
        context.Manager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new BaseItem[] { included, excluded });
        context.Manager.Setup(manager => manager.GetItemById(excluded.Id)).Returns(excluded);
        var controller = new ChooseYourMetaController(null!, null!, context.Manager.Object);

        var response = Assert.IsType<OkObjectResult>(controller.CheckMovieYears(default).Result);
        var audit = Assert.IsType<MovieYearAuditResult>(response.Value);
        Assert.Equal(1, audit.MoviesChecked);
        Assert.Equal(included.Id, Assert.Single(audit.Mismatches).ItemId);

        var excludedCandidates = await controller.GetMovieYearCandidates(excluded.Id, default);
        Assert.IsType<NotFoundResult>(excludedCandidates.Result);
    }

    private static Movie MakeMovie(string fileName, int savedYear)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Path = Path.Combine("Movies", fileName), ProductionYear = savedYear };
        movie.SetProviderId(MetadataProvider.Tmdb, "1487");
        movie.SetProviderId(MetadataProvider.Imdb, "tt0167190");
        return movie;
    }
}
