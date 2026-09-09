using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace RussianMetadata;

public sealed class ReconcilePeopleTask(ILibraryManager library, ILogger<ReconcilePeopleTask> logger) : IScheduledTask
{
    private static readonly SemaphoreSlim Gate = new(1);
    public string Name => "Объединить связи актёров по внешним ID";
    public string Key => "CowabungaReconcilePeople";
    public string Category => "Cowabunga Jellyfin Companion";
    public string Description => "Связывает фильмы, сериалы и эпизоды с общей карточкой человека при совпадении внешних ID. Карточки, фотографии и избранное не удаляются.";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(5).Ticks };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Enabled() || library.IsScanRunning) return;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var people = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Person], Recursive = true }).OfType<Person>().ToArray();
            var aliases = BuildAliases(people);
            if (aliases.Count == 0) { progress.Report(100); return; }
            var items = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode], Recursive = true,
                PersonIds = people.Where(person => aliases.ContainsKey(person.Name)).Select(person => person.Id).ToArray()
            });
            var updated = 0;
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Enabled() || library.IsScanRunning) return;
                var item = items[i];
                if (!LibraryPolicy.Allows(CompanionModule.Metadata, item) || item.IsLocked || item.LockedFields.Contains(MetadataField.Cast)) continue;
                var current = library.GetPeople(item);
                var replacement = Rewrite(current, aliases);
                if (replacement is not null)
                {
                    await library.UpdatePeopleAsync(item, replacement, cancellationToken).ConfigureAwait(false);
                    updated++;
                    logger.LogInformation("Companion: unified person links for {ItemId}", item.Id);
                }
                progress.Report((i + 1d) / Math.Max(1, items.Count) * 100);
            }
            logger.LogInformation("Companion: person reconciliation updated {Count} media items", updated);
            progress.Report(100);
        }
        finally { Gate.Release(); }
    }

    private static bool Enabled() => LibraryPolicy.Enabled(CompanionModule.Metadata) && CompanionPlugin.GetConfiguration().EnableRussianPeople;

    internal static Dictionary<string, Person> BuildAliases(IEnumerable<Person> people)
    {
        var all = people.ToArray();
        var aliases = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);
        var ambiguousNames = all.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in all.Where(p => Identity(p) is not null).GroupBy(p => Identity(p)!))
        {
            var members = group.ToArray();
            if (members.Length < 2 || members.Any(p => p.IsLocked || p.LockedFields.Contains(MetadataField.Name) || ambiguousNames.Contains(p.Name))) continue;
            // An agreeing TMDB ID must never override conflicting IMDb IDs (or vice versa).
            if (new[] { "Tmdb", "Imdb" }.Any(key => members.Select(p => p.GetProviderId(key)).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)) continue;
            var canonical = members.OrderByDescending(p => MovieTextLocalization.ContainsCyrillic(p.Name))
                .ThenByDescending(p => p.HasImage(ImageType.Primary)).ThenBy(p => p.Id).First();
            foreach (var member in members.Where(p => p.Name != canonical.Name)) aliases[member.Name] = canonical;
        }
        return aliases;
    }

    private static string? Identity(Person person)
    {
        var tmdb = person.GetProviderId("Tmdb");
        if (int.TryParse(tmdb, out var id) && id > 0) return "tmdb:" + id;
        var imdb = person.GetProviderId("Imdb");
        return imdb is { Length: > 2 } && imdb.StartsWith("nm", StringComparison.Ordinal) && imdb[2..].All(char.IsAsciiDigit) ? "imdb:" + imdb : null;
    }

    internal static List<PersonInfo>? Rewrite(IReadOnlyList<PersonInfo> credits, IReadOnlyDictionary<string, Person> aliases)
    {
        var changed = false;
        var result = new List<PersonInfo>();
        foreach (var credit in credits)
        {
            if (!aliases.TryGetValue(credit.Name, out var person)
                || credit.ProviderIds.Any(pair => person.ProviderIds.TryGetValue(pair.Key, out var value) && !string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase)))
            { result.Add(credit); continue; }
            var ids = new Dictionary<string, string>(credit.ProviderIds, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in person.ProviderIds) ids.TryAdd(pair.Key, pair.Value);
            result.Add(new PersonInfo { Name = person.Name, Role = credit.Role, Type = credit.Type, SortOrder = credit.SortOrder, ImageUrl = credit.ImageUrl, ProviderIds = ids });
            changed = true;
        }
        return changed ? result : null;
    }
}
