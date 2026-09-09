using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using RussianMetadata;

public class PeopleReconciliationTests
{
    [Fact]
    public async Task TaskOnlyUpdatesAllowedUnlockedItemsAndStopsWhenDisabled()
    {
        using var context = new CompanionTestContext();
        var cards = new[] { Card("Tom Hardy"), Card("Том Харди") };
        var allowed = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = Path.Combine(context.Root, "a.mkv") };
        var locked = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = allowed.Path, LockedFields = [MetadataField.Cast] };
        var excluded = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid() };
        context.Manager.Setup(m => m.GetItemList(Moq.It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.IncludeItemTypes.Contains(BaseItemKind.Person) ? cards.Cast<BaseItem>().ToList() : new List<BaseItem> { allowed, locked, excluded });
        context.Manager.Setup(m => m.GetPeople(allowed)).Returns(new[] { new PersonInfo { Name = "Tom Hardy", Type = PersonKind.Actor, Role = "Harry" } });
        context.Manager.Setup(m => m.UpdatePeopleAsync(Moq.It.IsAny<BaseItem>(), Moq.It.IsAny<IReadOnlyList<PersonInfo>>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var task = new ReconcilePeopleTask(context.Manager.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReconcilePeopleTask>.Instance);
        await task.ExecuteAsync(new Progress<double>(), default);
        context.Manager.Verify(m => m.UpdatePeopleAsync(allowed, Moq.It.Is<IReadOnlyList<PersonInfo>>(p => p[0].Name == "Том Харди"), Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
        context.Manager.Verify(m => m.GetPeople(locked), Moq.Times.Never);
        context.Manager.Verify(m => m.GetPeople(excluded), Moq.Times.Never);
        context.Manager.Invocations.Clear();
        context.Config.EnableRussianPeople = false;
        await task.ExecuteAsync(new Progress<double>(), default);
        context.Manager.Verify(m => m.GetItemList(Moq.It.IsAny<InternalItemsQuery>()), Moq.Times.Never);
    }

    private static Person Card(string name, string tmdb = "2524", string imdb = "nm0362766")
    {
        var person = new Person { Id = Guid.NewGuid(), Name = name };
        if (!string.IsNullOrWhiteSpace(tmdb)) person.SetProviderId("Tmdb", tmdb);
        if (!string.IsNullOrWhiteSpace(imdb)) person.SetProviderId("Imdb", imdb);
        return person;
    }

    [Fact]
    public void HardyAliasesShareRussianCardAndKeepRolesAndOrdering()
    {
        var english = Card("Tom Hardy"); var russian = Card("Том Харди");
        var aliases = ReconcilePeopleTask.BuildAliases([english, russian]);
        Assert.Same(russian, aliases["Tom Hardy"]);
        var credits = new[]
        {
            new PersonInfo { Name = "Tom Hardy", Type = PersonKind.Actor, Role = "Harry", SortOrder = 0 },
            new PersonInfo { Name = "Tom Hardy", Type = PersonKind.Producer, Role = "Executive Producer", SortOrder = 3 },
            new PersonInfo { Name = "Someone else", Type = PersonKind.Actor, Role = "Other" }
        };
        var rewritten = ReconcilePeopleTask.Rewrite(credits, aliases)!;
        Assert.Equal(3, rewritten.Count);
        Assert.Equal("Том Харди", rewritten[0].Name);
        Assert.Equal("2524", rewritten[0].ProviderIds["Tmdb"]);
        Assert.Equal("nm0362766", rewritten[0].ProviderIds["Imdb"]);
        Assert.Equal("Harry", rewritten[0].Role);
        Assert.Equal(0, rewritten[0].SortOrder);
        Assert.Equal(PersonKind.Producer, rewritten[1].Type);
        Assert.Equal("Executive Producer", rewritten[1].Role);
        Assert.Same(credits[2], rewritten[2]);
        Assert.Equal("Tom Hardy", credits[0].Name);
        Assert.Null(ReconcilePeopleTask.Rewrite(rewritten, aliases));
    }

    [Fact]
    public void ConflictingIdsOrLockedPersonPreventMerge()
    {
        Assert.Empty(ReconcilePeopleTask.BuildAliases([Card("Tom Hardy"), Card("Том Харди", imdb: "nm9999999")]));
        Assert.Empty(ReconcilePeopleTask.BuildAliases([Card("Tom Hardy"), Card("Том Харди", tmdb: "9999")]));
        var locked = Card("Том Харди"); locked.IsLocked = true;
        Assert.Empty(ReconcilePeopleTask.BuildAliases([Card("Tom Hardy"), locked]));
        locked.IsLocked = false; locked.LockedFields = [MetadataField.Name];
        Assert.Empty(ReconcilePeopleTask.BuildAliases([Card("Tom Hardy"), locked]));
    }

    [Fact]
    public void NamesAloneAndConflictingCreditIdsNeverAuthorizeMerge()
    {
        Assert.Empty(ReconcilePeopleTask.BuildAliases([Card("Tom Hardy", "", ""), Card("Том Харди", "", "")]));
        var aliases = ReconcilePeopleTask.BuildAliases([Card("Tom Hardy"), Card("Том Харди")]);
        var credit = new PersonInfo { Name = "Tom Hardy" };
        credit.SetProviderId("Tmdb", "9999");
        Assert.Null(ReconcilePeopleTask.Rewrite([credit], aliases));
    }
}
