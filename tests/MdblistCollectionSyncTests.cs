using System.Net;
using Jellyfin.Plugin.MagicCollections.Sync;
using Xunit;

namespace MagicCollections.Tests;

public sealed class MdblistCollectionSyncTests : IDisposable
{
    private const string TopMoviesUrl = "https://mdblist.com/lists/garycrawfordgc/top-movies";
    private readonly ControlledLibrary _library = new();
    private readonly RecordedHttpResponses _responses = new();
    private readonly HttpClient _http;
    private readonly CollectionSync _sync;

    public MdblistCollectionSyncTests()
    {
        _http = new HttpClient(_responses);
        _sync = new CollectionSync([new MdblistHandler(_http)], _library);
    }

    [Fact]
    public async Task Passes_MDBList_favicons_but_not_its_branded_list_image_to_the_poster()
    {
        const string source = "http://www.mdblist.com/lists/garycrawfordgc/top-movies/";
        RecordSourceList(TopMoviesUrl, """
            <head>
                <base href="https://other.example/">
                <meta property="og:image" content="/media/list.jpg?id=2618">
                <link rel="mask-icon" href="/mask.svg">
                <link rel="icon" href="https://other.example/logo.png">
                <link rel="icon" href="https://mdblist.com:8443/logo.png">
                <link rel="icon" href="http://mdblist.com/logo.png">
                <link rel="icon" href="https://user@mdblist.com/logo.png">
                <link rel="SHORTCUT ICON" href="../../icons/favicon.png">
            </head>
            <body><h3 class="textdark">Top Movies</h3></body>
            """, """[{"mediatype":"movie","id":577922}]""");
        _library.Media.Add(new(Guid.NewGuid(), new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "577922" })));

        await SyncSourceListAsync(source);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal(
            [new Uri("https://mdblist.com/icons/favicon.png"), new Uri("https://mdblist.com/favicon.ico")],
            collection.PosterFaviconUrls);
        Assert.Null(collection.PosterListImageUrl);
    }

    [Fact]
    public async Task Uses_the_MDBList_source_list_description_as_the_new_managed_collection_overview()
    {
        RecordSourceList(TopMoviesUrl, """
            <h3 class="textdark">Top movies</h3>
            <span class="ui small text wrap"><p>  <strong>Festival</strong> picks &amp; discoveries.  </p></span>
            <div class="card"><div class="description">A movie synopsis.</div></div>
            """, Fixture("top-movies.json"));
        _library.Media.Add(new(Guid.NewGuid(), new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "577922" })));

        await SyncSourceListAsync(TopMoviesUrl);

        Assert.Equal("Festival picks & discoveries.", Assert.Single(_library.ManagedCollections).Overview);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""<span class="ui small text wrap"><p>&nbsp;</p></span>""")]
    [InlineData("""<h3 class="textdark">Other lists</h3><span class="text wrap">Unrelated notes</span>""")]
    public async Task Preserves_the_managed_collection_overview_without_an_MDBList_description(string descriptionMarkup)
    {
        RecordSourceList(TopMoviesUrl, $"""
            <h3 class="textdark">Top movies</h3>
            {descriptionMarkup}
            <div class="description">A movie synopsis.</div>
            """, "[]");
        await _library.CreateAsync("Top movies", $"AutoCollections:{TopMoviesUrl}");
        var collection = Assert.Single(_library.ManagedCollections);
        collection.Overview = "My overview";

        await SyncSourceListAsync(TopMoviesUrl);

        Assert.Equal("My overview", collection.Overview);
    }

    [Fact]
    public async Task Syncs_a_public_MDBList_source_list_by_Tmdb_ID_without_duplicating_the_managed_collection_or_member()
    {
        const string sourceListUrl = "https://mdblist.com/lists/garycrawfordgc/top-movies";
        RecordSourceList(sourceListUrl, Fixture("top-movies.html"), Fixture("top-movies.json"));
        var tenet = Guid.NewGuid();
        _library.Media.Add(new(tenet, new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "577922" })));

        await SyncSourceListAsync(sourceListUrl);
        var originalId = Assert.Single(_library.ManagedCollections).Id;
        await SyncSourceListAsync(sourceListUrl);

        var managedCollection = Assert.Single(_library.ManagedCollections);
        Assert.Equal(originalId, managedCollection.Id);
        Assert.Equal("Top Movies", managedCollection.Name);
        Assert.Equal("AutoCollections:https://mdblist.com/lists/garycrawfordgc/top-movies", managedCollection.Tag);
        Assert.Equal([tenet], managedCollection.Members);
    }

    [Fact]
    public async Task Syncs_an_official_MDBList_source_list_with_whole_series_matched_by_Imdb_or_Tvdb()
    {
        const string sourceListUrl = "https://mdblist.com/lists/official/shows/popular";
        RecordSourceList(sourceListUrl, Fixture("popular-shows.html"), Fixture("popular-shows.json"));
        var carrie = Guid.NewGuid();
        var visionQuest = Guid.NewGuid();
        _library.Media.AddRange([
            new(carrie, new(SourceMediaType.Series, new Dictionary<string, string> { ["Tvdb"] = "455755" })),
            new(visionQuest, new(SourceMediaType.Series, new Dictionary<string, string> { ["Imdb"] = "tt23112594" })),
            new(Guid.NewGuid(), new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "288673" }))
        ]);

        await SyncSourceListAsync(sourceListUrl);

        var managedCollection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("Popular Shows", managedCollection.Name);
        Assert.Equal("AutoCollections:https://mdblist.com/lists/official/shows/popular", managedCollection.Tag);
        Assert.Equal(2, managedCollection.Members.Count);
        Assert.Contains(carrie, managedCollection.Members);
        Assert.Contains(visionQuest, managedCollection.Members);
    }

    [Fact]
    public async Task Syncs_the_ordinary_MDBList_streaming_charts_source_list()
    {
        const string sourceListUrl = "https://mdblist.com/lists/official/movies/streaming-charts";
        RecordSourceList(
            sourceListUrl,
            """<h3 class="textdark">Streaming Charts Movies</h3>""",
            """[{"id":70160,"title":"The Hunger Games","imdb_id":"tt1392170","mediatype":"movie"}]""");
        var hungerGames = Guid.NewGuid();
        _library.Media.Add(new(hungerGames, new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "70160" })));

        await SyncSourceListAsync(sourceListUrl);

        var managedCollection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("Streaming Charts Movies", managedCollection.Name);
        Assert.Equal([hungerGames], managedCollection.Members);
    }

    [Fact]
    public async Task Names_the_managed_collection_from_the_source_list_URL_when_the_MDBList_page_has_no_name()
    {
        const string sourceListUrl = "https://mdblist.com/lists/garycrawfordgc/top-movies/?sort=rank#items";
        RecordSourceList("https://mdblist.com/lists/garycrawfordgc/top-movies", "<html><h3>Gary</h3></html>", Fixture("top-movies.json"));
        var tenet = Guid.NewGuid();
        _library.Media.Add(new(tenet, new(SourceMediaType.Movie, new Dictionary<string, string> { ["Imdb"] = "tt6723592" })));

        await SyncSourceListAsync(sourceListUrl);

        var managedCollection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("top movies", managedCollection.Name);
        Assert.Equal([tenet], managedCollection.Members);
    }

    [Fact]
    public async Task Accepts_an_empty_MDBList_source_list_without_creating_a_managed_collection()
    {
        const string sourceListUrl = "https://mdblist.com/lists/garycrawfordgc/top-movies";
        RecordSourceList(sourceListUrl, Fixture("top-movies.html"), "[]");
        _library.Media.Add(new(Guid.NewGuid(), new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "577922" })));

        await SyncSourceListAsync(sourceListUrl);

        Assert.Empty(_library.ManagedCollections);
    }

    [Fact]
    public async Task Still_matches_short_Imdb_IDs_from_an_MDBList_source_list()
    {
        RecordSourceList(TopMoviesUrl, "<html></html>", """[{"imdb_id":"tt1","mediatype":"movie"}]""");
        var movie = Guid.NewGuid();
        _library.Media.Add(new(movie, new(SourceMediaType.Movie, new Dictionary<string, string> { ["Imdb"] = "tt1" })));

        await SyncSourceListAsync(TopMoviesUrl);

        Assert.Equal([movie], Assert.Single(_library.ManagedCollections).Members);
    }

    [Theory]
    [InlineData("""[{"id":577922,"mediatype":"movie"}]""")]
    [InlineData("""[{"id":577922,"mediatype":"movie","imdb_id":null}]""")]
    [InlineData("""[{"id":577922,"mediatype":"movie","imdb_id":""}]""")]
    public async Task Matches_an_MDBList_source_list_entry_by_Tmdb_when_the_Imdb_ID_is_absent(string json)
    {
        const string sourceListUrl = "https://mdblist.com/lists/garycrawfordgc/top-movies";
        RecordSourceList(sourceListUrl, Fixture("top-movies.html"), json);
        var tenet = Guid.NewGuid();
        _library.Media.Add(new(tenet, new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "577922" })));

        await SyncSourceListAsync(sourceListUrl);

        var managedCollection = Assert.Single(_library.ManagedCollections);
        Assert.Equal([tenet], managedCollection.Members);
    }

    [Theory]
    [InlineData("{\"error\":\"unavailable\"}")]
    [InlineData("null")]
    [InlineData("<html>An error occurred</html>")]
    [InlineData("""[{"id":577922,"mediatype":"movie"},{"id":"not-an-id","mediatype":"movie"}]""")]
    [InlineData("""[{"id":577922,"mediatype":"movie"},{"id":123}]""")]
    [InlineData("""[{"id":577922,"mediatype":"movie"},{"imdb_id":"not-an-id","mediatype":"movie"}]""")]
    [InlineData("""[{"id":577922,"mediatype":"movie"},{"imdb_id":"tt","mediatype":"movie"}]""")]
    [InlineData("""[{"id":577922,"mediatype":"movie"},{"imdb_id":"tt123x","mediatype":"movie"}]""")]
    [InlineData("""[{"id":577922,"mediatype":"movie"},{"imdb_id":"tt123 ","mediatype":"movie"}]""")]
    public async Task Rejects_malformed_MDBList_source_list_data_without_creating_a_managed_collection(string malformed)
    {
        const string sourceListUrl = "https://mdblist.com/lists/garycrawfordgc/top-movies";
        RecordSourceList(sourceListUrl, Fixture("top-movies.html"), malformed);
        _library.Media.Add(new(Guid.NewGuid(), new(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "577922" })));

        var failure = await Assert.ThrowsAsync<SourceListReadException>(() => SyncSourceListAsync(sourceListUrl));

        Assert.Contains(sourceListUrl, failure.Message);
        Assert.Contains("invalid", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_library.ManagedCollections);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(null)] // No HTTP response: a connection failure.
    public async Task Reports_an_MDBList_fetch_failure_with_the_source_list_URL(HttpStatusCode? status)
    {
        var failure = await SyncFailingTopMoviesSourceListAsync(status);

        Assert.Contains(TopMoviesUrl, failure.Message);
        Assert.Contains("fetch failed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_library.ManagedCollections);
    }

    [Fact]
    public async Task Leaves_a_managed_collection_without_a_poster_when_its_source_list_fetch_fails()
    {
        await _library.CreateAsync("Top Movies", $"AutoCollections:{TopMoviesUrl}");

        await SyncFailingTopMoviesSourceListAsync(HttpStatusCode.ServiceUnavailable);

        Assert.Null(Assert.Single(_library.ManagedCollections).PosterTitle);
    }

    [Fact]
    public async Task Reports_MDBList_paths_and_non_list_page_URLs_as_unsupported_source_list_URLs()
    {
        string[] sourceListUrls =
        [
            "/lists/garycrawfordgc/top-movies",
            "https://mdblist.com/lists/garycrawfordgc",
            "https://mdblist.com/lists/garycrawfordgc/top-movies/json",
            "https://mdblist.com.example/lists/garycrawfordgc/top-movies"
        ];
        var unsupported = new List<string>();

        await _sync.RunAsync(sourceListUrls, unsupported.Add, CancellationToken.None);

        Assert.Equal(sourceListUrls, unsupported);
    }

    [Theory]
    [InlineData("https://mdblist.com/lists/official/movies/justwatch-streaming-charts?locale=en_GB&rank=7")]
    [InlineData("https://mdblist.com/lists/official/shows/justwatch-streaming-charts")]
    public async Task Reports_interactive_JustWatch_charts_as_unsupported_source_list_URLs(string chartUrl)
    {
        var unsupported = new List<string>();

        await _sync.RunAsync([chartUrl], unsupported.Add, CancellationToken.None);

        Assert.Equal(chartUrl, Assert.Single(unsupported));
        Assert.Empty(_library.ManagedCollections);
    }

    public void Dispose() => _http.Dispose();

    private Task SyncSourceListAsync(string sourceListUrl) =>
        _sync.RunAsync([sourceListUrl], unsupported => Assert.Fail($"Unexpected unsupported source list URL: {unsupported}"), CancellationToken.None);

    private Task<SourceListReadException> SyncFailingTopMoviesSourceListAsync(HttpStatusCode? status)
    {
        _responses.RecordBody(TopMoviesUrl, Fixture("top-movies.html"));
        _responses.Record(TopMoviesUrl + "/json", () =>
            status is { } code ? new(code) : throw new HttpRequestException("Connection failed."));
        return Assert.ThrowsAsync<SourceListReadException>(() => SyncSourceListAsync(TopMoviesUrl));
    }

    private void RecordSourceList(string pageUrl, string html, string json)
    {
        _responses.RecordBody(pageUrl, html);
        _responses.RecordBody(pageUrl + "/json", json);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
