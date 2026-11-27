using System.Net;
using Jellyfin.Plugin.MagicCollections.Sync;
using Xunit;

namespace MagicCollections.Tests;

public sealed class PopularMoviesCollectionSyncTests : IDisposable
{
    private const string Origin = "https://popular-movies-data.stevenlu.com";
    private const string PopularFeed = Origin + "/movies.json";
    private readonly ControlledLibrary _library = new();
    private readonly RecordedHttpResponses _responses = new();
    private readonly TestClock _clock = new();
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"magic-collections-tests-{Guid.NewGuid():N}");
    private readonly HttpClient _http;
    private readonly CollectionSync _sync;

    public PopularMoviesCollectionSyncTests()
    {
        _http = new HttpClient(_responses);
        _sync = MakeSync();
    }

    [Fact]
    public async Task Syncs_a_recorded_source_list_by_Imdb_and_Tmdb_and_ignores_the_wrong_provider_ID()
    {
        // The first three entries from movies.json on 2026-11-26 (UTC).
        RecordBody(PopularFeed, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "steven-lu-movies.json")));
        var imdbMatch = AddLibraryMovie("Imdb", "tt22084616");
        var tmdbMatch = AddLibraryMovie("Tmdb", "1204680");
        var wrongId = AddLibraryMovie("Imdb", "tt9999999");

        await SyncAsync(PopularFeed);

        var managedCollection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("Steven Lu Popular Movies", managedCollection.Name);
        Assert.Equal(["Magic Collection", PopularFeed, "Steven Lu Popular Movies"], managedCollection.Tags);
        Assert.Equal(new Uri("https://stevenlu.com/images/avatar.webp"), Assert.Single(managedCollection.PosterFaviconUrls));
        Assert.Contains(imdbMatch, managedCollection.Members);
        Assert.Contains(tmdbMatch, managedCollection.Members);
        Assert.DoesNotContain(wrongId, managedCollection.Members);
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
        AddLibraryMovie("Imdb", "tt22084616");

        await SyncAsync(url);

        var managedCollection = Assert.Single(_library.ManagedCollections);
        Assert.Equal(name, managedCollection.Name);
        Assert.Equal(["Magic Collection", url, "Steven Lu Popular Movies"], managedCollection.Tags);
    }

    [Fact]
    public async Task Rejects_unsupported_source_list_URLs_before_requesting_them()
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
        Assert.Empty(_responses.Requests);
    }

    [Theory]
    [InlineData("""[{"imdb_id":"tt22084616"}]""", "Imdb", "tt22084616")]
    [InlineData("""[{"imdb_id":"tt1234567","tmdb_id":null}]""", "Imdb", "tt1234567")]
    [InlineData("""[{"tmdb_id":2147483647}]""", "Tmdb", "2147483647")]
    [InlineData("""[{"tmdb_id":969681,"imdb_id":null}]""", "Tmdb", "969681")]
    [InlineData("""[{"tmdb_id":969681,"imdb_id":""}]""", "Tmdb", "969681")]
    public async Task Matches_a_source_list_entry_with_only_one_provider_ID(string json, string provider, string providerId)
    {
        RecordBody(PopularFeed, json);
        var match = AddLibraryMovie(provider, providerId);

        await SyncAsync(PopularFeed);

        Assert.Equal(match, Assert.Single(Assert.Single(_library.ManagedCollections).Members));
    }

    [Fact]
    public async Task Accepts_an_empty_source_list_without_creating_a_managed_collection()
    {
        RecordBody(PopularFeed, "[]");
        AddLibraryMovie("Imdb", "tt22084616");

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
    [InlineData("""[{"imdb_id":"tt123456","tmdb_id":969681}]""")]
    [InlineData("""[{"imdb_id":"tt22084616 "}]""")]
    [InlineData("""[{"tmdb_id":"969681"}]""")]
    [InlineData("""[{"tmdb_id":1.5}]""")]
    [InlineData("""[{"tmdb_id":-1}]""")]
    [InlineData("""[{"tmdb_id":0}]""")]
    [InlineData("""[{"tmdb_id":2147483648,"imdb_id":"tt22084616"}]""")]
    [InlineData("""[{"imdb_id":null,"imdb_id":"tt22084616"}]""")]
    [InlineData("""[{"tmdb_id":969681,"tmdb_\u0069d":969681}]""")]
    [InlineData("""[{"imdb_id":"tt22084616"},{"imdb_id":"tt7526136","tmdb_id":false}]""")]
    public async Task Reports_malformed_source_list_data_without_syncing_any_entries(string malformed)
    {
        RecordBody(PopularFeed, malformed);
        AddLibraryMovie("Imdb", "tt22084616");

        var failure = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(PopularFeed));

        Assert.Contains(PopularFeed, failure.Message);
        Assert.Contains("invalid", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_library.ManagedCollections);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(null)]
    public async Task Reports_an_unavailable_source_list_as_a_read_failure(HttpStatusCode? status)
    {
        _responses.Record(PopularFeed, () => status is { } code
            ? new(code) { Headers = { Location = new Uri("https://not-the-feed.example/elsewhere.json") } }
            : throw new HttpRequestException("Connection failed."));

        var failure = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(PopularFeed));

        Assert.Contains(PopularFeed, failure.Message);
        Assert.Contains("fetch failed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_library.ManagedCollections);
    }

    [Fact]
    public async Task Frequent_syncs_reuse_each_source_list_snapshot_for_24_hours_but_still_match_new_library_items()
    {
        const string allFeed = Origin + "/all-movies.json";
        RecordBody(PopularFeed, """[{"imdb_id":"tt22084616"}]""");
        RecordBody(allFeed, """[{"imdb_id":"tt1756855"}]""");

        await SyncAsync(PopularFeed);
        RecordBody(PopularFeed, """[{"imdb_id":"tt7526136"}]""");
        var spiderman = AddLibraryMovie("Imdb", "tt22084616");
        var coyote = AddLibraryMovie("Imdb", "tt1756855");
        var heart = AddLibraryMovie("Imdb", "tt7526136");
        for (int hour = 1; hour < 24; hour++)
        {
            _clock.Advance(TimeSpan.FromHours(1));
            await SyncAsync(PopularFeed, allFeed);
        }

        Assert.Equal([spiderman], _library.ManagedCollections.Single(c => c.Tags.Contains(PopularFeed)).Members);
        Assert.Equal([coyote], _library.ManagedCollections.Single(c => c.Tags.Contains(allFeed)).Members);
        Assert.Equal([PopularFeed, allFeed], _responses.Requests);

        _clock.Advance(TimeSpan.FromHours(1));
        await SyncAsync("HTTPS://POPULAR-MOVIES-DATA.STEVENLU.COM/movies.json", PopularFeed, allFeed);
        Assert.Equal([spiderman, heart], _library.ManagedCollections.Single(c => c.Tags.Contains(PopularFeed)).Members);
        Assert.Equal([PopularFeed, allFeed, PopularFeed], _responses.Requests);

        _clock.Advance(TimeSpan.FromHours(1));
        await SyncAsync(PopularFeed, allFeed);
        Assert.Equal([PopularFeed, allFeed, PopularFeed, allFeed], _responses.Requests);
    }

    [Fact]
    public async Task A_restart_and_readding_the_source_list_URL_reuse_the_snapshot_until_the_window_expires()
    {
        RecordBody(PopularFeed, """[{"imdb_id":"tt22084616"}]""");
        await SyncAsync(PopularFeed);
        await SyncAsync();
        await SyncAsync(PopularFeed);
        Assert.Equal([PopularFeed], _responses.Requests);

        RecordBody(PopularFeed, """[{"imdb_id":"tt7526136"}]""");
        var snapshotMatch = AddLibraryMovie("Imdb", "tt22084616");
        var freshMatch = AddLibraryMovie("Imdb", "tt7526136");
        var restarted = MakeSync();
        _clock.Advance(TimeSpan.FromHours(23));
        await restarted.RunAsync([PopularFeed], ReportUnexpected, CancellationToken.None);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal([snapshotMatch], collection.Members);
        Assert.Equal([PopularFeed], _responses.Requests);

        _clock.Advance(TimeSpan.FromHours(1));
        await restarted.RunAsync([PopularFeed], ReportUnexpected, CancellationToken.None);

        Assert.Equal([snapshotMatch, freshMatch], collection.Members);
        Assert.Equal([PopularFeed, PopularFeed], _responses.Requests);
    }

    [Fact]
    public async Task A_restart_reuses_an_empty_source_list_as_a_success()
    {
        RecordBody(PopularFeed, "[]");
        await SyncAsync(PopularFeed);

        AddLibraryMovie("Imdb", "tt22084616");
        RecordBody(PopularFeed, """[{"imdb_id":"tt22084616"}]""");
        await MakeSync().RunAsync([PopularFeed], ReportUnexpected, CancellationToken.None);

        Assert.Empty(_library.ManagedCollections);
        Assert.Equal([PopularFeed], _responses.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frequent_syncs_report_a_failed_source_list_without_retrying_early(bool malformed)
    {
        if (malformed)
        {
            RecordBody(PopularFeed, """[{"imdb_id":"bad-id"}]""");
        }
        else
        {
            _responses.Record(PopularFeed, () => new(HttpStatusCode.ServiceUnavailable));
        }
        var original = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(PopularFeed));
        RecordBody(PopularFeed, "[]");

        _clock.Advance(TimeSpan.FromHours(23));
        await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(PopularFeed));
        var restarted = MakeSync();
        var cached = await Assert.ThrowsAsync<SourceListReadException>(
            () => restarted.RunAsync([PopularFeed], ReportUnexpected, CancellationToken.None));
        Assert.Equal(original.Message, cached.Message);
        Assert.Equal([PopularFeed], _responses.Requests);

        _clock.Advance(TimeSpan.FromHours(1));
        await restarted.RunAsync([PopularFeed], ReportUnexpected, CancellationToken.None);
        Assert.Equal([PopularFeed, PopularFeed], _responses.Requests);
    }

    [Fact]
    public async Task Cancelling_a_sync_does_not_start_a_second_request_for_the_same_source_list()
    {
        using var cancellation = new CancellationTokenSource();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _responses.RecordPending(PopularFeed, response.Task);
        var first = _sync.RunAsync([PopularFeed], ReportUnexpected, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        var next = SyncAsync(PopularFeed);
        response.SetResult(new(HttpStatusCode.OK) { Content = new StringContent("[]") });
        await next;

        Assert.Equal([PopularFeed], _responses.Requests);
    }

    [Fact]
    public async Task A_restart_before_the_source_list_fetch_finishes_does_not_open_a_retry_window()
    {
        // A fresh sync owner can see only the on-disk attempt until the original HTTP request completes.
        using var cancellation = new CancellationTokenSource();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _responses.RecordPending(PopularFeed, response.Task);
        var first = _sync.RunAsync([PopularFeed], ReportUnexpected, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        _clock.Advance(TimeSpan.FromHours(23));
        var failure = await Assert.ThrowsAsync<SourceListReadException>(
            () => MakeSync().RunAsync([PopularFeed], ReportUnexpected, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("interrupted", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([PopularFeed], _responses.Requests);

        var stillWaiting = SyncAsync(PopularFeed);
        response.SetResult(new(HttpStatusCode.OK) { Content = new StringContent("[]") });
        await stillWaiting;
        RecordBody(PopularFeed, "[]");

        _clock.Advance(TimeSpan.FromHours(1));
        await MakeSync().RunAsync([PopularFeed], ReportUnexpected, CancellationToken.None);
        Assert.Equal([PopularFeed, PopularFeed], _responses.Requests);
    }

    [Fact]
    public async Task Does_not_fetch_a_source_list_when_the_attempt_cannot_be_saved()
    {
        // A file where the state folder's parent should be makes the configured data path unusable.
        File.WriteAllText(_dataPath, "not a directory");
        RecordBody(PopularFeed, "[]");

        var failure = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(PopularFeed));

        Assert.Contains(PopularFeed, failure.Message);
        Assert.Empty(_responses.Requests);
    }

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        else
        {
            File.Delete(_dataPath);
        }
    }

    private CollectionSync MakeSync() => new([new PopularMoviesHandler(_http, _clock, _dataPath)], _library);

    private Task SyncAsync(params string[] urls) =>
        _sync.RunAsync(urls, ReportUnexpected, CancellationToken.None);

    private static void ReportUnexpected(string unsupported) => Assert.Fail($"Unexpected unsupported source list URL: {unsupported}");

    private Guid AddLibraryMovie(string provider, string providerId)
    {
        var id = Guid.NewGuid();
        _library.Media.Add(new(id, new(SourceMediaType.Movie, new Dictionary<string, string> { [provider] = providerId })));
        return id;
    }

    private void RecordBody(string endpoint, string body) =>
        _responses.RecordBody(endpoint, body);

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }
}
