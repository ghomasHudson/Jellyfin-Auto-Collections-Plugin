using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MagicCollections.Tests;

public sealed class JellyfinQuerySyncTests
{
    [Fact]
    public async Task Genre_and_year_select_exact_movies_and_whole_series_in_the_configured_library()
    {
        var (library, index, provider) = CreateLibrary();
        using var services = provider;
        var (selected, root) = index.AddLibrary();
        var (_, otherRoot) = index.AddLibrary();
        var (_, disabledRoot) = index.AddLibrary(enabled: false);
        var noProvider = index.Add(new Movie(), root);
        var series = index.Add(new Series(), root);
        var matchingCopy = index.Add(new Movie { ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1234567" } }, root);
        var wrongGenreCopy = index.Add(new Movie { ProviderIds = matchingCopy.ProviderIds }, root, genres: ["Comedy"]);
        var outsideCopy = index.Add(new Movie { ProviderIds = matchingCopy.ProviderIds }, otherRoot);
        index.Add(new Movie(), disabledRoot);
        index.Add(new Movie(), root, year: 2023);
        index.Add(new Episode(), root);
        index.Add(new Movie { IsVirtualItem = true }, root);
        index.Add(new Movie { IsPlaceHolder = true }, root);
        index.Add(new Movie { ChannelId = Guid.NewGuid() }, root);
        index.Add(new Movie { IsShortcut = true }, root);
        index.Add(new Movie { ExtraType = ExtraType.Trailer }, root);
        library.Media.Add(new(wrongGenreCopy.Id, new(SourceMediaType.Movie, wrongGenreCopy.ProviderIds)));
        library.Media.Add(new(outsideCopy.Id, new(SourceMediaType.Movie, outsideCopy.ProviderIds)));
        var sync = services.GetRequiredService<CollectionSync>();

        await sync.RunAsync(
            [$"jellyfin/genres=Mystery&years=2025&parentId={selected:N}&list_name=Mystery%202025"],
            Assert.Fail, CancellationToken.None);

        var collection = Assert.Single(library.ManagedCollections);
        Assert.Equal("Mystery 2025", collection.Name);
        Assert.Contains("Jellyfin", collection.Tags);
        Assert.Equal(new[] { noProvider.Id, series.Id, matchingCopy.Id }.Order(), collection.Members.Order());

        await sync.RunAsync(["jellyfin/genres=Mystery&years=2025"], Assert.Fail, CancellationToken.None);

        var global = Assert.Single(library.ManagedCollections, c => c.Id != collection.Id);
        Assert.Equal("Jellyfin library query", global.Name);
        Assert.Equal(new[] { noProvider.Id, series.Id, matchingCopy.Id, outsideCopy.Id }.Order(), global.Members.Order());
    }

    [Fact]
    public async Task Movies_of_the_day_limits_the_eligible_movies_after_checking_critic_ratings()
    {
        var (library, index, provider) = CreateLibrary();
        using var services = provider;
        var (_, root) = index.AddLibrary();
        index.Add(new Movie { CriticRating = 8 }, root);
        index.Add(new Movie(), root);
        index.Add(new Series { CriticRating = 100 }, root);
        var eligible = new[]
        {
            index.Add(new Movie { CriticRating = 9 }, root).Id,
            index.Add(new Movie { CriticRating = 20 }, root).Id,
            index.Add(new Movie { CriticRating = 30 }, root).Id,
            index.Add(new Movie { CriticRating = 40 }, root).Id
        };

        await services.GetRequiredService<CollectionSync>().RunAsync(
            ["jellyfin/mincriticRating=9&sortBy=Random&limit=3&includeItemTypes=Movie&list_name=Movies%20of%20the%20Day&list_desc=3%20highly-rated%20movies"],
            Assert.Fail, CancellationToken.None);

        var collection = Assert.Single(library.ManagedCollections);
        Assert.Equal("Movies of the Day", collection.Name);
        Assert.Equal("3 highly-rated movies", collection.Overview);
        Assert.Equal(3, collection.Members.Distinct().Count());
        Assert.All(collection.Members, id => Assert.Contains(id, eligible));
    }

    [Fact]
    public async Task Tags_person_title_and_known_studios_narrow_a_query_without_creating_studios()
    {
        var (library, index, provider) = CreateLibrary();
        using var services = provider;
        var (_, root) = index.AddLibrary();
        index.AddStudio("Studio A");
        var match = index.Add(new Movie { Name = "Asterix and Obelix", Tags = ["film noir"], Studios = ["Studio A"] }, root);
        var withoutTag = index.Add(new Movie { Name = "Asterix Returns", Studios = ["Studio A"] }, root);
        var differentStudio = index.Add(new Movie { Name = "Asterix Again", Tags = ["film noir"], Studios = ["Studio C"] }, root);
        var differentTitle = index.Add(new Movie { Name = "Tintin", Tags = ["film noir"], Studios = ["Studio A"] }, root);
        index.Add(new Movie { Name = "Asterix Extra", Tags = ["film noir"], Studios = ["Studio A"] }, root);
        foreach (var movie in new[] { match, withoutTag, differentStudio, differentTitle })
        {
            index.Credit(movie, "Wes Anderson");
        }
        var sync = services.GetRequiredService<CollectionSync>();

        await sync.RunAsync(
            ["jellyfin/tags=film%20noir&person=Wes%20Anderson&searchTerm=Asterix&studios=Studio%20A,Unknown"],
            Assert.Fail, CancellationToken.None);

        Assert.Equal([match.Id], Assert.Single(library.ManagedCollections).Members);
        await sync.RunAsync(["jellyfin/studios=Unknown"], Assert.Fail, CancellationToken.None);
        Assert.Single(library.ManagedCollections);
    }

    [Fact]
    public async Task Equivalent_queries_reuse_membership_while_only_the_first_entry_supplies_metadata_per_run()
    {
        var (library, index, provider) = CreateLibrary();
        using var services = provider;
        var (scope, root) = index.AddLibrary();
        index.Add(new Movie { Name = "Asterix", CriticRating = 20 }, root, genres: ["Mystery"]);
        var sync = services.GetRequiredService<CollectionSync>();
        var first = $"jellyfin/genres=Mystery,Science%20Fiction&searchTerm=ASTERIX&years=2025,2024&parentId={scope:D}&minCriticRating=009.0&list_name=Original";
        var equivalent = $"Jellyfin/YeaRS=02024,2025,2024&GENRES=SCIENCE+FICTION,%6dystery&searchTerm=asterix&parentid={scope:N}&includeItemTypes=Series,movie&MinCriticRating=9&list_name=New&list_desc=Updated";

        await sync.RunAsync([first, equivalent], Assert.Fail, CancellationToken.None);

        var collection = Assert.Single(library.ManagedCollections);
        Assert.Equal("Original", collection.Name);
        Assert.Null(collection.Overview);

        await sync.RunAsync([equivalent], Assert.Fail, CancellationToken.None);

        Assert.Same(collection, Assert.Single(library.ManagedCollections));
        Assert.Equal("Original", collection.Name);
        Assert.Equal("Updated", collection.Overview);
    }

    [Fact]
    public async Task Invalid_or_failed_queries_preserve_membership_while_a_successful_empty_read_clears_it()
    {
        var (library, index, provider) = CreateLibrary();
        using var services = provider;
        var (scope, root) = index.AddLibrary();
        var mystery = index.Add(new Movie(), root);
        var comedy = index.Add(new Movie(), root, genres: ["Comedy"]);
        var sync = services.GetRequiredService<CollectionSync>();
        var source = $"jellyfin/genres=Mystery&parentId={scope:N}";
        var reports = new List<string>();
        await sync.RunAsync([source], Assert.Fail, CancellationToken.None, reportJellyfinFailure: reports.Add);
        var collection = Assert.Single(library.ManagedCollections);
        Assert.Equal([mystery.Id], collection.Members);

        index.FailNextRead = true;
        await sync.RunAsync(["jellyfin/genres=Mystery&userId=secret", source, "jellyfin/genres=Comedy"],
            Assert.Fail, CancellationToken.None, reportJellyfinFailure: reports.Add);

        Assert.Collection(reports,
            invalid => Assert.StartsWith("Invalid Jellyfin library query:", invalid),
            failed => Assert.Contains("Index temporarily unavailable", failed));
        Assert.DoesNotContain(reports, report => report.Contains("secret", StringComparison.Ordinal));
        Assert.Equal([mystery.Id], collection.Members);
        Assert.Equal([comedy.Id], Assert.Single(library.ManagedCollections, c => c.Id != collection.Id).Members);

        reports.Clear();
        index.SetLibraryEnabled(scope, false);
        await sync.RunAsync([source, $"jellyfin/parentId={root:N}"], Assert.Fail, CancellationToken.None,
            reportJellyfinFailure: reports.Add);
        Assert.Equal(2, reports.Count);
        Assert.All(reports, report => Assert.Contains("does not identify an enabled, configured Jellyfin library", report));
        Assert.Equal([mystery.Id], collection.Members);

        reports.Clear();
        index.SetLibraryEnabled(scope, true);
        mystery.Genres = ["Thriller"];
        await sync.RunAsync([source, "jellyfin/genres=Unmatched"], Assert.Fail, CancellationToken.None,
            reportJellyfinFailure: reports.Add);
        Assert.Empty(reports);
        Assert.Empty(collection.Members);
        Assert.Equal(2, library.ManagedCollections.Count);
    }

    [Fact]
    public async Task Missing_physical_roots_fail_without_falling_back_to_unscoped_items()
    {
        var (library, index, provider) = CreateLibrary();
        using var services = provider;
        var (scope, root) = index.AddLibrary();
        var movie = index.Add(new Movie(), root);
        var sync = services.GetRequiredService<CollectionSync>();
        var source = $"jellyfin/parentId={scope:N}";
        await sync.RunAsync([source], Assert.Fail, CancellationToken.None);
        var collection = Assert.Single(library.ManagedCollections);

        index.SetUnindexed(scope);
        var reports = new List<string>();
        await sync.RunAsync([source, "jellyfin/genres=Mystery"], Assert.Fail, CancellationToken.None,
            reportJellyfinFailure: reports.Add);

        Assert.Equal(2, reports.Count);
        Assert.All(reports, report => Assert.Contains("isn't indexed yet", report));
        Assert.Equal([movie.Id], collection.Members);
        Assert.Single(library.ManagedCollections);
    }

    [Fact]
    public async Task Limit_without_random_has_stable_membership_even_when_Jellyfin_changes_its_result_order()
    {
        var (library, index, provider) = CreateLibrary();
        using var services = provider;
        var (_, root) = index.AddLibrary();
        var first = index.Add(new Movie(), root);
        var second = index.Add(new Movie(), root);
        var third = index.Add(new Series(), root);
        var expected = new[] { first.Id, second.Id, third.Id }.Order().Take(2);
        var sync = services.GetRequiredService<CollectionSync>();

        await sync.RunAsync(["jellyfin/limit=2"], Assert.Fail, CancellationToken.None);
        var collection = Assert.Single(library.ManagedCollections);
        Assert.Equal(expected, collection.Members.Order());

        index.ReverseOrder();
        await sync.RunAsync(["jellyfin/limit=2"], Assert.Fail, CancellationToken.None);
        Assert.Equal(expected, collection.Members.Order());
    }

    private static (ControlledLibrary Collections, LibraryIndex Index, ServiceProvider Services) CreateLibrary()
    {
        var manager = DispatchProxy.Create<ILibraryManager, LibraryIndex>();
        var index = (LibraryIndex)(object)manager;
        var collections = new ControlledLibrary();
        var services = PluginTestServices.BuildProvider(services =>
        {
            services.AddSingleton(manager);
            services.AddSingleton<ICollectionLibrary>(collections);
        });
        return (collections, index, services);
    }

    public class LibraryIndex : DispatchProxy
    {
        private readonly List<VirtualFolderInfo> _libraries = [];
        private readonly Dictionary<Guid, BaseItem> _folders = [];
        private readonly List<(BaseItem Item, Guid Root)> _media = [];
        private readonly Dictionary<string, Guid> _studios = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, string> _credits = [];

        public bool FailNextRead { get; set; }
        public void AddStudio(string name) => _studios.Add(name, Guid.NewGuid());
        public void Credit(BaseItem item, string person) => _credits.Add(item.Id, person);
        public void ReverseOrder() => _media.Reverse();
        public void SetLibraryEnabled(Guid id, bool enabled) =>
            _libraries.Single(folder => folder.ItemId == id.ToString("N")).LibraryOptions.Enabled = enabled;
        public void SetUnindexed(Guid id) => ((CollectionFolder)_folders[id]).PhysicalFolderIds = [];

        public (Guid Id, Guid Root) AddLibrary(bool enabled = true)
        {
            var id = Guid.NewGuid();
            var root = Guid.NewGuid();
            _libraries.Add(new() { ItemId = id.ToString("N"), Locations = ["/media"], LibraryOptions = new() { Enabled = enabled } });
            _folders[id] = new CollectionFolder { Id = id, PhysicalFolderIds = [root] };
            _folders[root] = new Folder { Id = root, Path = "/media" };
            return (id, root);
        }

        public T Add<T>(T item, Guid root, int year = 2025, string[]? genres = null) where T : BaseItem
        {
            item.Id = Guid.NewGuid();
            item.Path = $"/media/{item.Id:N}";
            item.Genres = genres ?? ["Mystery"];
            item.ProductionYear = year;
            _media.Add((item, root));
            return item;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name switch
            {
                nameof(ILibraryManager.GetVirtualFolders) => _libraries,
                nameof(ILibraryManager.GetItemById) => _folders.GetValueOrDefault((Guid)args![0]!),
                nameof(ILibraryManager.GetItemList) => Query((InternalItemsQuery)args![0]!),
                nameof(ILibraryManager.GetItemIds) => FindStudio((InternalItemsQuery)args![0]!),
                _ => throw new NotSupportedException(method?.Name)
            };

        private Guid[] FindStudio(InternalItemsQuery query)
        {
            Assert.Equal([BaseItemKind.Studio], query.IncludeItemTypes);
            return _studios.TryGetValue(query.Name!, out var id) ? [id] : [];
        }

        private BaseItem[] Query(InternalItemsQuery query)
        {
            if (FailNextRead)
            {
                FailNextRead = false;
                throw new IOException("Index temporarily unavailable");
            }
            Assert.Null(query.User);
            Assert.False(query.GroupByPresentationUniqueKey);
            Assert.False(query.GroupBySeriesPresentationUniqueKey);
            Assert.NotEmpty(query.AncestorIds);
            return _media
                .Where(entry => query.AncestorIds.Contains(entry.Root))
                .Select(entry => entry.Item)
                .Where(item => query.IncludeItemTypes.Contains(item.GetBaseItemKind()))
                .Where(item => query.Genres.Count == 0 || item.Genres.Intersect(query.Genres, StringComparer.OrdinalIgnoreCase).Any())
                .Where(item => query.Tags.Length == 0 || item.Tags.Intersect(query.Tags, StringComparer.OrdinalIgnoreCase).Any())
                .Where(item => query.Person is null || _credits.GetValueOrDefault(item.Id) == query.Person)
                .Where(item => query.SearchTerm is null || item.Name.Contains(query.SearchTerm, StringComparison.OrdinalIgnoreCase))
                .Where(item => query.StudioIds.Length == 0 ||
                    item.Studios.Any(name => query.StudioIds.Contains(_studios.GetValueOrDefault(name))))
                .Where(item => query.Years.Length == 0 || query.Years.Contains(item.ProductionYear!.Value))
                .Where(item => query.IsVirtualItem is null || query.IsVirtualItem == item.IsVirtualItem)
                .Where(item => query.IsPlaceHolder is null || query.IsPlaceHolder == (item is Video { IsPlaceHolder: true }))
                .ToArray();
        }
    }
}
