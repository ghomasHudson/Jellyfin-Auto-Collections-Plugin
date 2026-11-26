using Jellyfin.Plugin.AutoCollections.Sync;
using Xunit;

namespace AutoCollections.Tests;

public class CollectionSyncTests
{
    [Fact]
    public async Task Reports_each_unsupported_source_url()
    {
        string[] urls = ["not a URL", "https://unsupported.example/list"];
        var unsupported = new List<string>();
        await new CollectionSync([], new ControlledLibrary()).RunAsync(urls, unsupported.Add, CancellationToken.None);
        Assert.Equal(urls, unsupported);
    }

    [Fact]
    public async Task Unsupported_source_does_not_prevent_syncing_a_named_tagged_collection_idempotently()
    {
        const string unsupportedUrl = "https://unsupported.example/list";
        const string supportedUrl = "https://lists.example/festival-picks";
        var movie = Guid.NewGuid();
        var library = new ControlledLibrary();
        library.Media.AddRange([
            new(movie, SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "104", ["Imdb"] = "tt0000104" }),
            new(Guid.NewGuid(), SourceMediaType.Series, new Dictionary<string, string> { ["Tmdb"] = "104" }),
            new(Guid.NewGuid(), SourceMediaType.Movie, new Dictionary<string, string> { ["Tvdb"] = "104" }),
            new(Guid.NewGuid(), SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "115" })
        ]);
        var entry = new SourceItem(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "104" });
        var handler = new TestHandler(new("Festival picks", [entry, entry]));
        var sync = new CollectionSync([handler], library);
        Guid? originalId = null;

        for (int i = 0; i < 2; i++)
        {
            var unsupported = new List<string>();
            await sync.RunAsync([unsupportedUrl, supportedUrl], unsupported.Add, CancellationToken.None);
            Assert.Equal([unsupportedUrl], unsupported);
            var collection = Assert.Single(library.Collections);
            Assert.Equal("Festival picks", collection.Name);
            Assert.Equal("AutoCollections:https://lists.example/festival-picks", collection.Tag);
            Assert.Equal([movie], collection.Members);
            originalId ??= collection.Id;
            Assert.Equal(originalId, collection.Id);
        }
    }

    [Fact]
    public async Task Uses_a_readable_source_slug_when_the_handler_has_no_list_name()
    {
        var library = new ControlledLibrary();
        var ids = new Dictionary<string, string> { ["Tvdb"] = "123" };
        library.Media.Add(new(Guid.NewGuid(), SourceMediaType.Series, ids));
        var handler = new TestHandler(new(null, [new(SourceMediaType.Series, ids)]));
        var sync = new CollectionSync([handler], library);

        await sync.RunAsync(["https://lists.example/lists/Festival%20TV-picks_2026/?lang=en"], _ => { }, CancellationToken.None);

        Assert.Equal("Festival TV picks 2026", Assert.Single(library.Collections).Name);
    }

    private sealed class TestHandler(SourceList source) : ISourceListHandler
    {
        public bool Supports(Uri url) => url.Host == "lists.example";
        public Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult(source);
    }

    private sealed record Collection(Guid Id, string Name, string Tag, List<Guid> Members);

    private sealed class ControlledLibrary : ICollectionLibrary
    {
        public List<LibraryMedia> Media { get; } = [];
        public List<Collection> Collections { get; } = [];

        public IEnumerable<LibraryMedia> GetMedia() => Media;
        public TaggedCollection? FindByTag(string tag) => Collections.SingleOrDefault(c => c.Tag == tag) is { } c
            ? new(c.Id, c.Members.ToArray()) : null;

        public Task<Guid> CreateAsync(string name, string tag)
        {
            var collection = new Collection(Guid.NewGuid(), name, tag, []);
            Collections.Add(collection);
            return Task.FromResult(collection.Id);
        }

        public Task AddMembersAsync(Guid id, IReadOnlyCollection<Guid> memberIds)
        {
            Collections.Single(c => c.Id == id).Members.AddRange(memberIds);
            return Task.CompletedTask;
        }
    }
}
