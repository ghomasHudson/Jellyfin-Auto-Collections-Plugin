using System.Globalization;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.MagicCollections.Configuration;
using Jellyfin.Plugin.MagicCollections.Sync;
using Xunit;

namespace MagicCollections.Tests;

public sealed class TraktCollectionSyncTests : IDisposable
{
    private const string ListUrl = "https://app.trakt.tv/users/justin/lists/open-your-eyes";
    private const string ListSummary = "https://api.trakt.tv/users/justin/lists/open-your-eyes/";
    private const string ListItems = ListSummary + "items/movie,show,season,episode";
    private readonly ControlledLibrary _library = new();
    private readonly TraktResponses _responses = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly HttpClient _http;
    private readonly CollectionSync _sync;

    public TraktCollectionSyncTests()
    {
        _http = new HttpClient(_responses);
        _sync = new CollectionSync([new TraktHandler(_http, () => _configuration)], _library);
    }

    [Fact]
    public async Task Adds_typed_library_matches_to_one_managed_collection_for_equivalent_public_list_links()
    {
        _responses.Summary();
        _responses.Page(ListItems, page: 1, limit: 50, itemCount: 6, """
            [
              {"type":"movie","movie":{"ids":{"trakt":999,"imdb":"tt1234567","tmdb":101,"tvdb":null}}},
              {"type":"show","show":{"ids":{"trakt":998,"imdb":null,"tmdb":0,"tvdb":202}}},
              {"type":"season","season":{"ids":{"tvdb":303}}},
              {"type":"episode","episode":{"ids":{"tvdb":404}}},
              {"type":"person","person":{"ids":{"imdb":"tt5555555"}}},
              {"type":"movie","movie":{"ids":{"trakt":888}}}
            ]
            """);
        var movieByTmdb = LibraryItem(SourceMediaType.Movie, "Tmdb", "101");
        var movieByImdb = LibraryItem(SourceMediaType.Movie, "Imdb", "tt1234567");
        var showByTvdb = LibraryItem(SourceMediaType.Series, "Tvdb", "202");
        LibraryItem(SourceMediaType.Series, "Tmdb", "101"); // Wrong media type.
        LibraryItem(SourceMediaType.Movie, "Tvdb", "202"); // Wrong media type.
        LibraryItem(SourceMediaType.Series, "Tvdb", "303");
        LibraryItem(SourceMediaType.Series, "Tvdb", "404");
        LibraryItem(SourceMediaType.Movie, "Imdb", "tt5555555");
        LibraryItem(SourceMediaType.Movie, "Tmdb", "888"); // A Trakt ID is not a TMDB ID.

        await SyncAsync("https://trakt.tv/users/Justin/lists/OPEN-YOUR-EYES/?access_token=not-for-trakt#items", ListUrl);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("Open Your Eyes", collection.Name);
        Assert.Equal(["Magic Collection", ListUrl, "Trakt"], collection.Tags);
        Assert.Equal(3, collection.Members.Count);
        Assert.Contains(movieByTmdb, collection.Members);
        Assert.Contains(movieByImdb, collection.Members);
        Assert.Contains(showByTvdb, collection.Members);
        Assert.Equal(new Uri("https://app.trakt.tv/pwa/android/icon_192.png"), Assert.Single(collection.PosterFaviconUrls));
        Assert.Equal([ListSummary, ListItems + "?page=1&limit=50"], _responses.Requests);
    }

    [Fact]
    public async Task Reads_every_public_list_page_even_beyond_the_chart_cap()
    {
        _responses.Summary();
        _responses.Page(ListItems, page: 1, limit: 50, itemCount: 101, Movies(1, 50, wrapped: true));
        _responses.Page(ListItems, page: 2, limit: 50, itemCount: 101, Movies(51, 50, wrapped: true));
        _responses.Page(ListItems, page: 3, limit: 50, itemCount: 101, Movies(101, 1, wrapped: true));
        var first = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        var last = LibraryItem(SourceMediaType.Movie, "Tmdb", "101");

        await SyncAsync(ListUrl);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal(2, collection.Members.Count);
        Assert.Contains(first, collection.Members);
        Assert.Contains(last, collection.Members);
        Assert.Equal(
            [ListSummary, ListItems + "?page=1&limit=50", ListItems + "?page=2&limit=50", ListItems + "?page=3&limit=50"],
            _responses.Requests);
    }

    [Fact]
    public async Task Rejects_a_public_list_whose_total_changes_before_the_read_completes()
    {
        _responses.Summary();
        _responses.Page(ListItems, page: 1, limit: 50, itemCount: 51, Movies(1, 50, wrapped: true));
        _responses.Page(ListItems, page: 2, limit: 50, itemCount: 52, Movies(51, 2, wrapped: true));
        LibraryItem(SourceMediaType.Movie, "Tmdb", "1");

        await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(ListUrl));

        Assert.Empty(_library.ManagedCollections);
    }

    [Fact]
    public async Task Accepts_a_valid_empty_public_list_without_creating_a_managed_collection()
    {
        _responses.Summary();
        _responses.Page(ListItems, page: 1, limit: 50, itemCount: 0, "[]");
        LibraryItem(SourceMediaType.Movie, "Tmdb", "1");

        await SyncAsync(ListUrl);

        Assert.Empty(_library.ManagedCollections);
    }

    [Fact]
    public async Task Uses_the_list_slug_when_Trakt_has_items_but_no_list_summary()
    {
        const string official = "https://app.trakt.tv/users/justin/lists/imdb-top-rated-movies";
        const string api = "https://api.trakt.tv/users/justin/lists/imdb-top-rated-movies/";
        _responses.Record(api, () => new(HttpStatusCode.NoContent));
        _responses.Page(api + "items/movie,show,season,episode", page: 1, limit: 50, itemCount: 1,
            Movies(1, 1, wrapped: true));
        var member = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");

        await SyncAsync(official + "/?access_token=ignore-me");

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("imdb top rated movies", collection.Name);
        Assert.Equal(["Magic Collection", official, "Trakt"], collection.Tags);
        Assert.Equal([member], collection.Members);
    }

    [Theory]
    [InlineData("""{"name":"Open Your Eyes","description":"  A short list.\nMovies & shows.  "}""", "A short list.\nMovies & shows.")]
    [InlineData("""{"name":"Open Your Eyes","description":null}""", "Earlier notes")]
    [InlineData("""{"name":"Open Your Eyes","description":"  "}""", "Earlier notes")]
    public async Task Uses_a_nonempty_plain_list_description_for_the_managed_collection_overview(string summary, string expected)
    {
        _responses.Summary(summary);
        _responses.Page(ListItems, page: 1, limit: 50, itemCount: 1, Movies(1, 1, wrapped: true));
        var collectionId = await _library.CreateAsync("Open Your Eyes", "Magic Collection", ListUrl, "Trakt");
        await _library.SetOverviewAsync(collectionId, "Earlier notes", CancellationToken.None);

        await SyncAsync(ListUrl);

        Assert.Equal(expected, Assert.Single(_library.ManagedCollections).Overview);
    }

    [Fact]
    public async Task A_rate_limit_on_a_later_page_reports_when_to_retry_and_leaves_the_managed_collection_unchanged()
    {
        _configuration.TraktClientId = "override-credential";
        _responses.Summary("""{"name":"Open Your Eyes","description":"New notes"}""");
        _responses.Page(ListItems, page: 1, limit: 1, itemCount: 2, Movies(1, 1, wrapped: true));
        _responses.Record(ListItems + "?page=2&limit=50", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("untrusted response echoing override-credential")
            };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(90));
            return response;
        });
        LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        var previousMember = LibraryItem(SourceMediaType.Movie, "Tmdb", "9");
        var collectionId = await _library.CreateAsync("Existing collection", "Magic Collection", ListUrl, "Trakt");
        await _library.AddMembersAsync(collectionId, [previousMember]);
        await _library.SetOverviewAsync(collectionId, "Earlier notes", CancellationToken.None);

        var error = await Assert.ThrowsAsync<SourceListReadException>(() =>
            SyncAsync("https://trakt.tv/users/justin/lists/open-your-eyes/?access_token=local-token#private-fragment"));

        Assert.Contains(ListUrl, error.Message, StringComparison.Ordinal);
        Assert.Contains("429", error.Message, StringComparison.Ordinal);
        Assert.Contains("90 seconds", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("override-credential", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("local-token", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-fragment", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("untrusted response", error.ToString(), StringComparison.Ordinal);
        Assert.Equal([ListSummary, ListItems + "?page=1&limit=50", ListItems + "?page=2&limit=50"], _responses.Requests);
        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal([previousMember], collection.Members);
        Assert.Equal("Earlier notes", collection.Overview);
        Assert.Null(collection.PosterTitle);
    }

    [Fact]
    public async Task A_rejected_Client_ID_reports_HTTP_401_without_echoing_the_credential_or_response()
    {
        _configuration.TraktClientId = "rejected-client-id";
        _responses.Record(ListSummary, () => new(HttpStatusCode.Unauthorized)
        {
            ReasonPhrase = "rejected-client-id",
            Content = new StringContent("rejected-client-id")
        });

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(ListUrl));

        Assert.Contains(ListUrl, error.Message, StringComparison.Ordinal);
        Assert.Contains("401", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected-client-id", error.ToString(), StringComparison.Ordinal);
        Assert.Equal([ListSummary], _responses.Requests);
        Assert.Empty(_library.ManagedCollections);
    }

    [Fact]
    public async Task A_malformed_Client_ID_is_rejected_locally_without_echoing_the_credential()
    {
        _configuration.TraktClientId = "bad-id\r\nX-Test: injected";

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(ListUrl + "?access_token=local-token"));

        Assert.Contains(ListUrl, error.Message, StringComparison.Ordinal);
        Assert.Contains("Trakt Client ID", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bad-id", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("local-token", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_responses.Requests);
    }

    [Fact]
    public async Task Uses_the_bundled_Client_ID_unless_the_current_settings_supply_an_override()
    {
        const string bundled = "ciP9eCYn_QzrN3q_7LABgpl6B-wi5UIdOgvZz3Jg1Fc";
        _responses.Summary();
        _responses.Page(ListItems, page: 1, limit: 50, itemCount: 0, "[]");

        await SyncAsync(ListUrl);
        _configuration.TraktClientId = "  my_client-id  ";
        await SyncAsync(ListUrl);
        _configuration.TraktClientId = "  ";
        await SyncAsync(ListUrl);

        Assert.Equal([bundled, bundled, "my_client-id", "my_client-id", bundled, bundled], _responses.ClientIds);
        Assert.Empty(_http.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData("""[{"type":"movie","movie":{"ids":{"tmdb":2}}}]""", null)]
    [InlineData("""[{"type":"movie","movie":{"ids":{"tmdb":2}}}]""", 1)]
    [InlineData("""[{"type":"movie","movie":{"ids":{"tmdb":"not-an-id"}}}]""", 2)]
    [InlineData("""[{"type":"show","movie":{"ids":{"tmdb":2}}}]""", 2)]
    [InlineData("""[{"type":""", 2)]
    public async Task A_malformed_later_page_fails_the_whole_read_without_creating_a_managed_collection(string body, int? reportedPage)
    {
        _responses.Summary();
        _responses.Page(ListItems, page: 1, limit: 1, itemCount: 2, Movies(1, 1, wrapped: true));
        _responses.Record(ListItems + "?page=2&limit=50", () =>
            PaginatedResponse(reportedPage, limit: 1, itemCount: 2, body));
        LibraryItem(SourceMediaType.Movie, "Tmdb", "1");

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(ListUrl));

        Assert.Contains(ListUrl, error.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid Trakt source list data", error.Message, StringComparison.Ordinal);
        Assert.Empty(_library.ManagedCollections);
    }

    [Fact]
    public async Task An_inaccessible_list_is_not_treated_as_empty_when_Trakt_returns_no_summary()
    {
        _responses.Record(ListSummary, () => new(HttpStatusCode.NoContent));
        _responses.Record(ListItems + "?page=1&limit=50", () => new(HttpStatusCode.Forbidden));

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(ListUrl));

        Assert.Contains("403", error.Message, StringComparison.Ordinal);
        Assert.Equal([ListSummary, ListItems + "?page=1&limit=50"], _responses.Requests);
        Assert.Empty(_library.ManagedCollections);
    }

    [Theory]
    [InlineData("https://api.trakt.tv/movies/popular")]
    [InlineData("https://trakt.tv.example/users/justin/lists/open-your-eyes")]
    [InlineData("https://app.trakt.tv:8080/users/justin/lists/open-your-eyes")]
    [InlineData("https://user:password@app.trakt.tv/users/justin/lists/open-your-eyes")]
    [InlineData("https://app.trakt.tv/users/justin/lists/a%2fb")]
    [InlineData("https://app.trakt.tv/users/justin/lists/a%3Fb")]
    [InlineData("https://app.trakt.tv/users/justin/watchlist")]
    [InlineData("https://app.trakt.tv/users/me/lists/saved-list")]
    [InlineData("https://trakt.tv/movies/recommended")]
    [InlineData("https://app.trakt.tv/discover/popular")]
    [InlineData("https://app.trakt.tv/discover/trending?mode=show&genres=comedy")]
    [InlineData("https://app.trakt.tv/discover/popular?mode=show&mode=movie")]
    public async Task Does_not_send_requests_for_other_Trakt_URLs_or_filtered_charts(string url)
    {
        var unsupported = new List<string>();

        await _sync.RunAsync([url], unsupported.Add, CancellationToken.None);

        Assert.Equal([url], unsupported);
        Assert.Empty(_responses.Requests);
        Assert.Empty(_library.ManagedCollections);
    }

    [Theory]
    [InlineData("movies", "popular", "movie", "Popular Movies", SourceMediaType.Movie, """[{"ids":{"tmdb":101}}]""")]
    [InlineData("shows", "popular", "show", "Popular Shows", SourceMediaType.Series, """[{"ids":{"tmdb":101}}]""")]
    [InlineData("movies", "trending", "movie", "Trending Movies", SourceMediaType.Movie, """[{"watchers":30,"movie":{"ids":{"tmdb":101}}}]""")]
    [InlineData("shows", "trending", "show", "Trending Shows", SourceMediaType.Series, """[{"watchers":30,"show":{"ids":{"tmdb":101}}}]""")]
    public async Task Uses_one_managed_collection_for_legacy_and_shareable_links_to_a_public_chart(
        string media, string chart, string mode, string name, SourceMediaType type, string body)
    {
        var legacy = $"https://trakt.tv/{media}/{chart}";
        var shareable = $"https://app.trakt.tv/discover/{chart}?mode={mode}";
        var endpoint = $"https://api.trakt.tv/{media}/{chart}";
        _responses.Page(endpoint, page: 1, limit: 50, itemCount: 1, body);
        var member = LibraryItem(type, "Tmdb", "101");
        LibraryItem(type == SourceMediaType.Movie ? SourceMediaType.Series : SourceMediaType.Movie, "Tmdb", "101");

        await SyncAsync(legacy, shareable);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal(name, collection.Name);
        Assert.Equal(["Magic Collection", shareable, "Trakt"], collection.Tags);
        Assert.Equal([member], collection.Members);
        Assert.Equal(new Uri("https://app.trakt.tv/pwa/android/icon_192.png"), Assert.Single(collection.PosterFaviconUrls));
        Assert.Equal([endpoint + "?page=1&limit=50"], _responses.Requests);
    }

    [Fact]
    public async Task Caps_the_chart_at_100_raw_ranked_entries_even_when_the_API_clamps_page_size()
    {
        const string endpoint = "https://api.trakt.tv/movies/popular";
        _responses.Page(endpoint, page: 1, limit: 40, itemCount: 200, Movies(1, 40, withoutProviderAt: 1));
        _responses.Page(endpoint, page: 2, limit: 40, itemCount: 200, Movies(41, 40, withoutProviderAt: 51));
        _responses.Page(endpoint, page: 3, limit: 40, itemCount: 200, Movies(81, 40));
        var first = LibraryItem(SourceMediaType.Movie, "Tmdb", "2");
        var last = LibraryItem(SourceMediaType.Movie, "Tmdb", "100");
        LibraryItem(SourceMediaType.Movie, "Tmdb", "101");

        await SyncAsync("https://trakt.tv/movies/popular");

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal(2, collection.Members.Count);
        Assert.Contains(first, collection.Members);
        Assert.Contains(last, collection.Members);
        Assert.Equal([endpoint + "?page=1&limit=50", endpoint + "?page=2&limit=50", endpoint + "?page=3&limit=50"], _responses.Requests);
    }

    [Theory]
    [InlineData(197)] // Live response; the item count changed but the page count did not.
    [InlineData(201)] // The chart grew by a page as well.
    public async Task Syncs_the_top_100_ranks_when_live_chart_totals_change_between_pages(int secondPageTotal)
    {
        const string endpoint = "https://api.trakt.tv/movies/trending";
        _responses.Page(endpoint, page: 1, limit: 50, itemCount: 189, TrendingMovies(1, 50));
        _responses.Page(endpoint, page: 2, limit: 50, itemCount: secondPageTotal, TrendingMovies(51, 50));
        var first = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        var last = LibraryItem(SourceMediaType.Movie, "Tmdb", "100");
        LibraryItem(SourceMediaType.Movie, "Tmdb", "101");

        await SyncAsync("https://trakt.tv/movies/trending");

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal(2, collection.Members.Count);
        Assert.Contains(first, collection.Members);
        Assert.Contains(last, collection.Members);
        Assert.Equal([endpoint + "?page=1&limit=50", endpoint + "?page=2&limit=50"], _responses.Requests);
    }

    [Theory]
    [InlineData(40, 40)] // Changing the effective limit shifts the page offset.
    [InlineData(50, 49)] // Even with a stable limit, a truncated page is invalid.
    public async Task Rejects_invalid_later_chart_pages_without_creating_a_managed_collection(int limit, int rows)
    {
        const string endpoint = "https://api.trakt.tv/movies/trending";
        _responses.Page(endpoint, page: 1, limit: 50, itemCount: 189, TrendingMovies(1, 50));
        _responses.Page(endpoint, page: 2, limit, itemCount: 197, TrendingMovies(limit + 1, rows));
        LibraryItem(SourceMediaType.Movie, "Tmdb", "1");

        await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync("https://trakt.tv/movies/trending"));

        Assert.Empty(_library.ManagedCollections);
    }

    public void Dispose() => _http.Dispose();

    private static string TrendingMovies(int first, int count) =>
        JsonSerializer.Serialize(Enumerable.Range(first, count).Select(id => new { watchers = 1, movie = new { ids = new { tmdb = id } } }));

    private static string Movies(int first, int count, bool wrapped = false, int? withoutProviderAt = null)
    {
        var movies = Enumerable.Range(first, count).Select(id =>
            new { ids = new { trakt = 1000 + id, tmdb = id == withoutProviderAt ? (int?)null : id } });
        return wrapped
            ? JsonSerializer.Serialize(movies.Select(movie => new { type = "movie", movie }))
            : JsonSerializer.Serialize(movies);
    }

    private Task SyncAsync(params string[] urls) => _sync.RunAsync(urls,
        url => Assert.Fail($"Unexpected unsupported source list URL: {url}"), CancellationToken.None);

    private Guid LibraryItem(SourceMediaType type, string provider, string id)
    {
        var itemId = Guid.NewGuid();
        _library.Media.Add(new(itemId, new(type, new Dictionary<string, string> { [provider] = id })));
        return itemId;
    }

    private static HttpResponseMessage PaginatedResponse(int? reportedPage, int limit, int itemCount, string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        if (reportedPage is { } page)
        {
            response.Headers.Add("X-Pagination-Page", page.ToString(CultureInfo.InvariantCulture));
        }
        response.Headers.Add("X-Pagination-Limit", limit.ToString(CultureInfo.InvariantCulture));
        response.Headers.Add("X-Pagination-Page-Count", ((itemCount + limit - 1) / limit).ToString(CultureInfo.InvariantCulture));
        response.Headers.Add("X-Pagination-Item-Count", itemCount.ToString(CultureInfo.InvariantCulture));
        return response;
    }

    private sealed class TraktResponses : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = [];
        public List<string> Requests { get; } = [];
        public List<string> ClientIds { get; } = [];

        public void Summary(string json = """{"name":"Open Your Eyes"}""") => Record(ListSummary, () =>
            new(HttpStatusCode.OK) { Content = new StringContent(json) });

        public void Page(string path, int page, int limit, int itemCount, string json) =>
            Record($"{path}?page={page}&limit=50", () => PaginatedResponse(page, limit, itemCount, json));

        public void Record(string url, Func<HttpResponseMessage> response) => _responses[url] = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.trakt.tv", request.RequestUri!.Host);
            Assert.Equal(Uri.UriSchemeHttps, request.RequestUri.Scheme);
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Equal(["2"], request.Headers.GetValues("trakt-api-version"));
            Assert.False(request.Headers.Contains("Authorization"));
            ClientIds.Add(Assert.Single(request.Headers.GetValues("trakt-api-key")));
            var endpoint = request.RequestUri.AbsoluteUri;
            Requests.Add(endpoint);
            return Task.FromResult(_responses.TryGetValue(endpoint, out var respond)
                ? respond()
                : throw new InvalidOperationException($"No Trakt recording for {endpoint}"));
        }
    }
}
