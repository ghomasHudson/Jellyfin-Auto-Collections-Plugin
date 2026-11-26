using System.Net;
using Jellyfin.Plugin.AutoCollections.Sync;
using Xunit;

namespace AutoCollections.Tests;

public class CollectionSyncTests
{
    [Fact]
    public async Task Syncs_a_public_MDBList_movie_by_Tmdb_ID_without_duplicating_the_collection_or_member()
    {
        const string url = "https://mdblist.com/lists/garycrawfordgc/top-movies";
        using var http = new HttpClient(new RecordedResponses(new Dictionary<string, string>
        {
            [url] = Fixture("top-movies.html"),
            [url + "/json"] = Fixture("top-movies.json")
        }));
        var library = new ControlledLibrary();
        var tenet = Guid.NewGuid();
        library.Media.Add(new(tenet, new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "577922" })));
        var sync = new CollectionSync([new MdblistHandler(http)], library);
        var unsupported = new List<string>();

        await sync.RunAsync([url], unsupported.Add, CancellationToken.None);
        var originalId = Assert.Single(library.Collections).Id;
        await sync.RunAsync([url], unsupported.Add, CancellationToken.None);

        Assert.Empty(unsupported);
        var collection = Assert.Single(library.Collections);
        Assert.Equal(originalId, collection.Id);
        Assert.Equal("Top Movies", collection.Name);
        Assert.Equal("AutoCollections:https://mdblist.com/lists/garycrawfordgc/top-movies", collection.Tag);
        Assert.Equal([tenet], collection.Members);
    }

    [Fact]
    public async Task Syncs_an_official_MDBList_source_with_whole_series_matched_by_Imdb_or_Tvdb()
    {
        const string url = "https://mdblist.com/lists/official/shows/popular";
        using var http = new HttpClient(new RecordedResponses(new Dictionary<string, string>
        {
            [url] = Fixture("popular-shows.html"),
            [url + "/json"] = Fixture("popular-shows.json")
        }));
        var library = new ControlledLibrary();
        var carrie = Guid.NewGuid();
        var visionQuest = Guid.NewGuid();
        library.Media.AddRange([
            new(carrie, new(SourceMediaType.Series, new Dictionary<string, string> { ["Tvdb"] = "455755" })),
            new(visionQuest, new(SourceMediaType.Series, new Dictionary<string, string> { ["Imdb"] = "tt23112594" })),
            new(Guid.NewGuid(), new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "288673" }))
        ]);
        var sync = new CollectionSync([new MdblistHandler(http)], library);

        await sync.RunAsync([url], unsupported => Assert.Fail($"Unexpected unsupported URL: {unsupported}"), CancellationToken.None);

        var collection = Assert.Single(library.Collections);
        Assert.Equal("Popular Shows", collection.Name);
        Assert.Equal("AutoCollections:https://mdblist.com/lists/official/shows/popular", collection.Tag);
        Assert.Equal(2, collection.Members.Count);
        Assert.Contains(carrie, collection.Members);
        Assert.Contains(visionQuest, collection.Members);
    }

    [Fact]
    public async Task Uses_the_MDBList_page_slug_when_the_name_is_missing()
    {
        const string url = "https://mdblist.com/lists/garycrawfordgc/top-movies/?sort=rank#items";
        using var http = new HttpClient(new RecordedResponses(new Dictionary<string, string>
        {
            ["https://mdblist.com/lists/garycrawfordgc/top-movies"] = "<html><h3>Gary</h3></html>",
            ["https://mdblist.com/lists/garycrawfordgc/top-movies/json"] = Fixture("top-movies.json")
        }));
        var library = new ControlledLibrary();
        var movie = Guid.NewGuid();
        library.Media.Add(new(movie, new(SourceMediaType.Movie, new Dictionary<string, string> { ["Imdb"] = "tt6723592" })));
        var sync = new CollectionSync([new MdblistHandler(http)], library);

        await sync.RunAsync([url], unsupported => Assert.Fail($"Unexpected unsupported URL: {unsupported}"), CancellationToken.None);

        var collection = Assert.Single(library.Collections);
        Assert.Equal("top movies", collection.Name);
        Assert.Equal([movie], collection.Members);
    }

    [Theory]
    [InlineData("{\"error\":\"unavailable\"}")]
    [InlineData("null")]
    [InlineData("<html>An error occurred</html>")]
    [InlineData("""[{"id":577922,"mediatype":"movie"},{"id":"not-an-id","mediatype":"movie"}]""")]
    [InlineData("""[{"id":577922,"mediatype":"movie"},{"id":123}]""")]
    public async Task Rejects_malformed_MDBList_responses_but_accepts_a_valid_empty_list(string malformed)
    {
        const string url = "https://mdblist.com/lists/garycrawfordgc/top-movies";
        var responses = new Dictionary<string, string>
        {
            [url] = Fixture("top-movies.html"),
            [url + "/json"] = "[]"
        };
        using var http = new HttpClient(new RecordedResponses(responses));
        var library = new ControlledLibrary();
        library.Media.Add(new(Guid.NewGuid(), new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "577922" })));
        var sync = new CollectionSync([new MdblistHandler(http)], library);
        Task Run() => sync.RunAsync([url], unsupported => Assert.Fail($"Unexpected unsupported URL: {unsupported}"), CancellationToken.None);

        await Run();
        Assert.Empty(library.Collections);

        responses[url + "/json"] = malformed;
        var failure = await Assert.ThrowsAsync<SourceListReadException>(Run);
        Assert.Contains(url, failure.Message);
        Assert.Contains("invalid", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(library.Collections);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(null)] // No HTTP response: a connection failure.
    public async Task Reports_an_MDBList_fetch_failure_with_the_source_URL(HttpStatusCode? status)
    {
        const string url = "https://mdblist.com/lists/garycrawfordgc/top-movies";
        using var http = new HttpClient(new RecordedResponses(requestUrl =>
        {
            if (requestUrl == url)
            {
                return new(HttpStatusCode.OK) { Content = new StringContent(Fixture("top-movies.html")) };
            }
            Assert.Equal(url + "/json", requestUrl);
            return status is { } code ? new(code) : throw new HttpRequestException("Connection failed.");
        }));
        var library = new ControlledLibrary();
        var sync = new CollectionSync([new MdblistHandler(http)], library);

        var failure = await Assert.ThrowsAsync<SourceListReadException>(() =>
            sync.RunAsync([url], unsupported => Assert.Fail($"Unexpected unsupported URL: {unsupported}"), CancellationToken.None));
        Assert.Contains(url, failure.Message);
        Assert.Contains("fetch failed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(library.Collections);
    }

    [Fact]
    public async Task Reports_paths_and_non_list_page_URLs_as_unsupported()
    {
        string[] urls =
        [
            "/lists/garycrawfordgc/top-movies",
            "https://mdblist.com/lists/garycrawfordgc",
            "https://mdblist.com/lists/garycrawfordgc/top-movies/json",
            "https://mdblist.com.example/lists/garycrawfordgc/top-movies"
        ];
        using var http = new HttpClient(new RecordedResponses(url => throw new InvalidOperationException($"Unexpected request: {url}")));
        var unsupported = new List<string>();
        var sync = new CollectionSync([new MdblistHandler(http)], new ControlledLibrary());

        await sync.RunAsync(urls, unsupported.Add, CancellationToken.None);

        Assert.Equal(urls, unsupported);
    }

    [Fact]
    public async Task Reports_each_unsupported_source_list_URL()
    {
        string[] sourceListUrls = ["not a URL", "https://unsupported.example/list"];
        var unsupported = new List<string>();
        await new CollectionSync([], new ControlledLibrary()).RunAsync(sourceListUrls, unsupported.Add, CancellationToken.None);
        Assert.Equal(sourceListUrls, unsupported);
    }

    [Fact]
    public async Task Unsupported_source_list_URL_does_not_prevent_syncing_a_named_managed_collection_idempotently()
    {
        const string unsupportedUrl = "https://unsupported.example/list";
        const string supportedUrl = "https://lists.example/festival-picks";
        var movie = Guid.NewGuid();
        var library = new ControlledLibrary();
        library.Media.AddRange([
            new(movie, new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "104", ["Imdb"] = "tt0000104" })),
            new(Guid.NewGuid(), new(SourceMediaType.Series, new Dictionary<string, string> { ["Tmdb"] = "104" })),
            new(Guid.NewGuid(), new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tvdb"] = "104" })),
            new(Guid.NewGuid(), new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "115" }))
        ]);
        var entry = new MediaIdentity(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "104" });
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
    public async Task Names_the_managed_collection_from_the_source_list_URL_when_the_handler_has_no_list_name()
    {
        var library = new ControlledLibrary();
        var ids = new Dictionary<string, string> { ["Tvdb"] = "123" };
        library.Media.Add(new(Guid.NewGuid(), new(SourceMediaType.Series, ids)));
        var handler = new TestHandler(new(null, [new(SourceMediaType.Series, ids)]));
        var sync = new CollectionSync([handler], library);

        await sync.RunAsync(["https://lists.example/lists/Festival%20TV-picks_2026/?lang=en"], _ => { }, CancellationToken.None);

        Assert.Equal("Festival TV picks 2026", Assert.Single(library.Collections).Name);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed class RecordedResponses(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public RecordedResponses(IReadOnlyDictionary<string, string> responses) : this(url =>
            responses.TryGetValue(url, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) }
                : throw new InvalidOperationException($"No recording for {url}"))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!.AbsoluteUri));
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
