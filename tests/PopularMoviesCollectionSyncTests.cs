using System.Net;
using Jellyfin.Plugin.MagicCollections;
using Jellyfin.Plugin.MagicCollections.Sync;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MagicCollections.Tests;

public sealed class PopularMoviesCollectionSyncTests : IDisposable
{
    private const string Origin = "https://popular-movies-data.stevenlu.com";
    private const string PopularFeed = Origin + "/movies.json";
    private readonly ControlledLibrary _library = new();
    private readonly FeedResponses _responses = new();
    private readonly TestClock _clock = new();
    private readonly HttpClient _http;
    private readonly CollectionSync _sync;

    public PopularMoviesCollectionSyncTests()
    {
        _http = new HttpClient(_responses);
        _sync = MakeSync();
    }

    [Fact]
    public void Registers_a_Popular_Movies_handler_without_replacing_the_MDBList_handler()
    {
        var services = new ServiceCollection();
        new ServiceRegistrator().RegisterServices(services, null!);
        using var provider = services.BuildServiceProvider();

        var handlers = provider.GetServices<ISourceListHandler>().ToArray();

        var popular = Assert.Single(handlers, h => h.Supports(new(PopularFeed)));
        Assert.Same(popular, provider.GetServices<ISourceListHandler>().Single(h => h.Supports(new(PopularFeed))));
        Assert.Single(handlers, h => h.Supports(new("https://mdblist.com/lists/garycrawfordgc/top-movies")));
    }

    [Fact]
    public async Task Syncs_a_recorded_feed_by_Imdb_and_Tmdb_but_does_not_match_a_same_title_with_the_wrong_ID()
    {
        // The first three entries from movies.json on 2026-11-26 (UTC).
        RecordBody(PopularFeed, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "steven-lu-movies.json")));
        var imdbMatch = Movie("Spider-Man: Brand New Day", "Imdb", "tt22084616");
        var tmdbMatch = Movie("Coyote vs. Acme", "Tmdb", "1204680");
        var wrongId = Movie(imdbMatch.Title, "Imdb", "tt9999999");

        await SyncAsync(PopularFeed);

        var managedCollection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("Steven Lu Popular Movies", managedCollection.Name);
        Assert.Equal($"AutoCollections:{PopularFeed}", managedCollection.Tag);
        Assert.Contains(imdbMatch.Id, managedCollection.Members);
        Assert.Contains(tmdbMatch.Id, managedCollection.Members);
        Assert.DoesNotContain(wrongId.Id, managedCollection.Members);
        Assert.Equal(2, managedCollection.Members.Count);
    }

    [Theory]
    [InlineData("/all-movies.json", "Steven Lu All Movies")]
    [InlineData("/movies-metacritic-min50.json", "Steven Lu Movies (Metacritic 50+)")]
    [InlineData("/movies-metacritic-min60.json", "Steven Lu Movies (Metacritic 60+)")]
    [InlineData("/movies-metacritic-min70.json", "Steven Lu Movies (Metacritic 70+)")]
    [InlineData("/movies-metacritic-min80.json", "Steven Lu Movies (Metacritic 80+)")]
    [InlineData("/movies-imdb-min5.json", "Steven Lu Movies (IMDb 5+)")]
    [InlineData("/movies-imdb-min6.json", "Steven Lu Movies (IMDb 6+)")]
    [InlineData("/movies-imdb-min7.json", "Steven Lu Movies (IMDb 7+)")]
    [InlineData("/movies-imdb-min8.json", "Steven Lu Movies (IMDb 8+)")]
    [InlineData("/movies-rottentomatoes-min50.json", "Steven Lu Movies (Rotten Tomatoes 50+)")]
    [InlineData("/movies-rottentomatoes-min60.json", "Steven Lu Movies (Rotten Tomatoes 60+)")]
    [InlineData("/movies-rottentomatoes-min70.json", "Steven Lu Movies (Rotten Tomatoes 70+)")]
    [InlineData("/movies-rottentomatoes-min80.json", "Steven Lu Movies (Rotten Tomatoes 80+)")]
    public async Task Names_the_managed_collection_for_each_documented_feed(string path, string name)
    {
        var url = Origin + path;
        RecordBody(url, """[{"imdb_id":"tt22084616","tmdb_id":969681}]""");
        Movie("Spider-Man: Brand New Day", "Imdb", "tt22084616");

        await SyncAsync(url);

        Assert.Equal(name, Assert.Single(_library.ManagedCollections).Name);
    }

    [Fact]
    public async Task Rejects_non_feed_URLs_before_requesting_them()
    {
        string[] urls =
        [
            "http://popular-movies-data.stevenlu.com/movies.json",
            "https://popular-movies-data.stevenlu.com.example.org/movies.json",
            "https://www.popular-movies-data.stevenlu.com/movies.json",
            "https://popular-movies-data.stevenlu.com./movies.json",
            "https://popular-movies-data.stevenlu.com:443/movies.json",
            "https://popular-movies-data.stevenlu.com:8443/movies.json",
            "https://user@popular-movies-data.stevenlu.com/movies.json",
            "https://popular-movies-data.stevenlu.com@other.example/movies.json",
            "https://@popular-movies-data.stevenlu.com/movies.json",
            Origin + "/",
            Origin + "/movies-20191202.json",
            Origin + "/movies-imdb-min9.json",
            PopularFeed + "/",
            PopularFeed + "?",
            PopularFeed + "?bypass-cache=1",
            PopularFeed + "#movies",
            Origin + "/Movies.json",
            Origin + "/%6dovies.json",
            Origin + "/extra/../movies.json",
            Origin + @"\movies.json"
        ];
        var unsupported = new List<string>();

        await _sync.RunAsync(urls, unsupported.Add, CancellationToken.None);

        Assert.Equal(urls, unsupported);
    }

    [Theory]
    [InlineData("""[{"imdb_id":"tt22084616"}]""", "Imdb")]
    [InlineData("""[{"imdb_id":"tt22084616","tmdb_id":null}]""", "Imdb")]
    [InlineData("""[{"tmdb_id":969681}]""", "Tmdb")]
    [InlineData("""[{"tmdb_id":969681,"imdb_id":null}]""", "Tmdb")]
    [InlineData("""[{"tmdb_id":969681,"imdb_id":""}]""", "Tmdb")]
    public async Task Matches_an_entry_with_only_one_provider_ID(string json, string provider)
    {
        RecordBody(PopularFeed, json);
        var imdbMatch = Movie("Spider-Man: Brand New Day", "Imdb", "tt22084616");
        var tmdbMatch = Movie("Spider-Man: Brand New Day", "Tmdb", "969681");

        await SyncAsync(PopularFeed);

        var expected = provider == "Imdb" ? imdbMatch.Id : tmdbMatch.Id;
        Assert.Equal(expected, Assert.Single(Assert.Single(_library.ManagedCollections).Members));
    }

    [Fact]
    public async Task Accepts_an_empty_feed_without_creating_a_managed_collection()
    {
        RecordBody(PopularFeed, "[]");
        Movie("Spider-Man: Brand New Day", "Imdb", "tt22084616");

        await SyncAsync(PopularFeed);

        Assert.Empty(_library.ManagedCollections);
    }

    [Theory]
    [InlineData("""{"error":"unavailable"}""")]
    [InlineData("null")]
    [InlineData("<html>Service unavailable</html>")]
    [InlineData("[null]")]
    [InlineData("""[{"title":"Spider-Man: Brand New Day"}]""")]
    [InlineData("""[{"imdb_id":22084616}]""")]
    [InlineData("""[{"imdb_id":"tt"}]""")]
    [InlineData("""[{"imdb_id":"tt22084616 "}]""")]
    [InlineData("""[{"tmdb_id":"969681"}]""")]
    [InlineData("""[{"tmdb_id":1.5}]""")]
    [InlineData("""[{"tmdb_id":-1}]""")]
    [InlineData("""[{"tmdb_id":0}]""")]
    [InlineData("""[{"imdb_id":"tt22084616"},{"imdb_id":"tt7526136","tmdb_id":false}]""")]
    public async Task Reports_malformed_feed_data_without_syncing_any_entries(string malformed)
    {
        RecordBody(PopularFeed, malformed);
        Movie("Spider-Man: Brand New Day", "Imdb", "tt22084616");

        var failure = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(PopularFeed));

        Assert.Contains(PopularFeed, failure.Message);
        Assert.Contains("invalid", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_library.ManagedCollections);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(null)]
    public async Task Reports_an_unavailable_feed_as_a_source_list_read_failure(HttpStatusCode? status)
    {
        _responses.Bodies[PopularFeed] = () => status is { } code
            ? new(code) { Headers = { Location = new Uri("https://not-the-feed.example/elsewhere.json") } }
            : throw new HttpRequestException("Connection failed.");

        var failure = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(PopularFeed));

        Assert.Contains(PopularFeed, failure.Message);
        Assert.Contains("fetch failed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_library.ManagedCollections);
    }

    [Fact]
    public async Task Frequent_syncs_reuse_each_feeds_snapshot_for_24_hours_but_still_match_new_library_items()
    {
        const string allFeed = Origin + "/all-movies.json";
        RecordBody(PopularFeed, """[{"imdb_id":"tt22084616"}]""");
        RecordBody(allFeed, """[{"imdb_id":"tt1756855"}]""");

        await SyncAsync(PopularFeed);
        RecordBody(PopularFeed, """[{"imdb_id":"tt7526136"}]""");
        var spiderman = Movie("Spider-Man: Brand New Day", "Imdb", "tt22084616");
        var coyote = Movie("Coyote vs. Acme", "Imdb", "tt1756855");
        var heart = Movie("Heart of the Beast", "Imdb", "tt7526136");
        for (int hour = 1; hour < 24; hour++)
        {
            _clock.Advance(TimeSpan.FromHours(1));
            await SyncAsync(PopularFeed, allFeed);
        }

        Assert.Equal([spiderman.Id], _library.ManagedCollections.Single(c => c.Tag.EndsWith("/movies.json")).Members);
        Assert.Equal([coyote.Id], _library.ManagedCollections.Single(c => c.Tag.EndsWith("/all-movies.json")).Members);
        Assert.Equal([PopularFeed, allFeed], _responses.Requests);

        _clock.Advance(TimeSpan.FromHours(1));
        await SyncAsync("HTTPS://POPULAR-MOVIES-DATA.STEVENLU.COM/movies.json", PopularFeed, allFeed);
        Assert.Equal([spiderman.Id, heart.Id], _library.ManagedCollections.Single(c => c.Tag.EndsWith("/movies.json")).Members);
        Assert.Equal([PopularFeed, allFeed, PopularFeed], _responses.Requests);

        _clock.Advance(TimeSpan.FromHours(1));
        await SyncAsync(PopularFeed, allFeed);
        Assert.Equal([PopularFeed, allFeed, PopularFeed, allFeed], _responses.Requests);
    }

    [Fact]
    public async Task A_restart_allows_a_fresh_poll_but_removing_and_readding_the_URL_does_not()
    {
        RecordBody(PopularFeed, "[]");
        await SyncAsync(PopularFeed);
        await SyncAsync();
        await SyncAsync(PopularFeed);
        Assert.Equal([PopularFeed], _responses.Requests);

        await MakeSync().RunAsync([PopularFeed], ReportUnexpected, CancellationToken.None);

        Assert.Equal([PopularFeed, PopularFeed], _responses.Requests);
    }

    [Fact]
    public async Task Frequent_syncs_report_a_failed_feed_without_retrying_early()
    {
        _responses.Bodies[PopularFeed] = () => new(HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(PopularFeed));

        RecordBody(PopularFeed, "[]");
        _clock.Advance(TimeSpan.FromHours(23));
        await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(PopularFeed));
        Assert.Equal([PopularFeed], _responses.Requests);

        _clock.Advance(TimeSpan.FromHours(1));
        await SyncAsync(PopularFeed);
        Assert.Equal([PopularFeed, PopularFeed], _responses.Requests);
    }

    [Fact]
    public async Task Cancelling_a_sync_does_not_start_a_second_request_for_the_same_feed()
    {
        using var cancellation = new CancellationTokenSource();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _responses.PendingResponse = response.Task;
        var first = _sync.RunAsync([PopularFeed], ReportUnexpected, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        var next = SyncAsync(PopularFeed);
        response.SetResult(new(HttpStatusCode.OK) { Content = new StringContent("[]") });
        await next;

        Assert.Equal([PopularFeed], _responses.Requests);
    }

    public void Dispose() => _http.Dispose();

    private CollectionSync MakeSync() => new([new PopularMoviesHandler(_http, _clock)], _library);

    private Task SyncAsync(params string[] urls) =>
        _sync.RunAsync(urls, ReportUnexpected, CancellationToken.None);

    private static void ReportUnexpected(string unsupported) => Assert.Fail($"Unexpected unsupported source list URL: {unsupported}");

    private (Guid Id, string Title) Movie(string title, string provider, string providerId)
    {
        var id = Guid.NewGuid();
        // The Jellyfin adapter exposes only the media type and provider IDs, not display titles.
        _library.Media.Add(new(id, new(SourceMediaType.Movie, new Dictionary<string, string> { [provider] = providerId })));
        return (id, title);
    }

    private void RecordBody(string endpoint, string body) =>
        _responses.Bodies[endpoint] = () => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class FeedResponses : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Bodies { get; } = [];
        public List<string> Requests { get; } = [];
        public Task<HttpResponseMessage>? PendingResponse { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var endpoint = request.RequestUri!.AbsoluteUri;
            Requests.Add(endpoint);
            return (PendingResponse ?? Task.FromResult(Bodies[endpoint]())).WaitAsync(cancellationToken);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan span) => _ticks += span.Ticks;
    }
}
