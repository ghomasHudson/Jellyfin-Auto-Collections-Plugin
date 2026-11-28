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
    private const string WatchlistUrl = "https://app.trakt.tv/users/me/watchlist";
    private const string WatchlistItems = "https://api.trakt.tv/users/me/watchlist/movie,show/rank";
    private const string RecommendedUrl = "https://app.trakt.tv/discover/recommended";
    private const string RecommendationQuery = "?limit=100&ignore_collected=true&ignore_watched=true";
    private const string SmartListUrl = "https://app.trakt.tv/lists/smart/view/hidden-gems-6ae39104c63e248a";
    private const string SmartListSummary = "https://api.trakt.tv/smart-lists/hidden-gems-6ae39104c63e248a";
    private const string SmartListItems = SmartListSummary + "/items";
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
    [InlineData("""{"name":"Open Your Eyes","description":null}""", null)]
    [InlineData("""{"name":"Open Your Eyes","description":"  "}""", null)]
    public async Task Uses_a_nonempty_plain_source_list_description_for_a_missing_managed_collection_overview(string summary, string? expected)
    {
        _responses.Summary(summary);
        _responses.Page(ListItems, page: 1, limit: 50, itemCount: 1, Movies(1, 1, wrapped: true));
        await _library.CreateAsync("Open Your Eyes", "Magic Collection", ListUrl, "Trakt");

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
        Assert.Single(_library.ManagedCollections).Overview = "Earlier notes";

        var error = await Assert.ThrowsAsync<SourceListReadException>(() =>
            SyncAsync("https://trakt.tv/users/justin/lists/open-your-eyes/?access_token=local-token#private-fragment"));

        Assert.DoesNotContain(ListUrl, error.Message, StringComparison.Ordinal);
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

        Assert.DoesNotContain(ListUrl, error.Message, StringComparison.Ordinal);
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

        Assert.DoesNotContain(ListUrl, error.Message, StringComparison.Ordinal);
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

        Assert.DoesNotContain(ListUrl, error.Message, StringComparison.Ordinal);
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
    [InlineData(RecommendedUrl + "?mode=episode")]
    [InlineData(RecommendedUrl + "?mode=movie&mode=show")]
    [InlineData(RecommendedUrl + "?mode=movie&ratings=100-78")]
    [InlineData(RecommendedUrl + "?mode=movie&ratings=78-101")]
    [InlineData(RecommendedUrl + "?mode=movie&runtimes=500")]
    [InlineData(RecommendedUrl + "?mode=movie&genres=action&genres=comedy")]
    [InlineData(RecommendedUrl + "?mode=movie&genres=action%26limit%3D1")]
    [InlineData(RecommendedUrl + "?mode=movie&limit=1")]
    [InlineData("https://app.trakt.tv/lists/smart/view/a%2Fb")]
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

    [Fact]
    public async Task Leaves_chart_membership_unchanged_when_the_chart_shrinks_within_the_rank_cap_mid_read()
    {
        const string endpoint = "https://api.trakt.tv/movies/trending";
        const string url = "https://trakt.tv/movies/trending";
        _responses.Page(endpoint, page: 1, limit: 50, itemCount: 189, TrendingMovies(1, 50));
        _responses.Page(endpoint, page: 2, limit: 50, itemCount: 189, TrendingMovies(51, 50));
        var first = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        var last = LibraryItem(SourceMediaType.Movie, "Tmdb", "100");
        await SyncAsync(url);
        var collection = Assert.Single(_library.ManagedCollections);

        _responses.Page(endpoint, page: 2, limit: 50, itemCount: 51, TrendingMovies(51, 1));

        var failure = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(url));
        Assert.Contains("Invalid Trakt source list data", failure.Message);
        Assert.Equal([first, last], collection.Members);
    }

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task A_public_smart_list_imports_its_name_and_matches_beyond_the_chart_cap_without_sign_in()
    {
        _responses.Record(SmartListSummary, () => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"name":"Hidden Gems","privacy":"public","source":"discover","filters":{"ratings":[78,100]}}""")
        });
        _responses.Page(SmartListItems, 1, 50, 101, Movies(1, 50, wrapped: true));
        _responses.Page(SmartListItems, 2, 50, 101, Movies(51, 50, wrapped: true));
        _responses.Page(SmartListItems, 3, 50, 101, """[{"type":"show","show":{"ids":{"tvdb":101}},"rank":101}]""");
        var movie = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        var show = LibraryItem(SourceMediaType.Series, "Tvdb", "101");

        await SyncAsync(SmartListUrl + "/?access_token=ignored#items", SmartListUrl);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("Hidden Gems", collection.Name);
        Assert.Equal(["Magic Collection", SmartListUrl, "Trakt"], collection.Tags);
        Assert.Equal(2, collection.Members.Count);
        Assert.Contains(movie, collection.Members);
        Assert.Contains(show, collection.Members);
        Assert.Equal([SmartListSummary, SmartListItems + "?page=1&limit=50",
            SmartListItems + "?page=2&limit=50", SmartListItems + "?page=3&limit=50"], _responses.Requests);
        Assert.All(_responses.Authorizations, Assert.Null);
    }

    [Fact]
    public async Task A_nonpublic_smart_list_does_not_send_the_connected_accounts_token()
        => await SyncWithTraktAccount(async (sync, _, _) =>
    {
        _responses.Record(SmartListSummary, () => new(HttpStatusCode.NotFound));
        var error = await Assert.ThrowsAsync<SourceListReadException>(() => sync.RunAsync([SmartListUrl], Assert.Fail, default));

        Assert.Equal([SmartListSummary], _responses.Requests);
        Assert.Null(Assert.Single(_responses.Authorizations));
        Assert.DoesNotContain("hidden-gems", error.Message, StringComparison.Ordinal);
        Assert.Empty(_library.ManagedCollections);
    });

    [Theory]
    [InlineData("movie", "movies", "", "Trakt Recommended Movies")]
    [InlineData("show", "shows", "", "Trakt Recommended Shows")]
    [InlineData("media", "media", "&genres=action", "Trakt Recommended Movies and Shows")]
    public async Task Reads_filtered_Trakt_recommendations_for_the_connected_account(string mode, string apiType, string genres, string name)
        => await SyncWithTraktAccount(async (sync, _, _) =>
    {
        const string filters = "&ratings=78-100&runtimes=80-500";
        var url = $"{RecommendedUrl}?mode={mode}{filters}{genres}";
        var endpoint = $"https://api.trakt.tv/{apiType}/recommendations{RecommendationQuery}{filters}{genres}";
        const string movieRow = """{"movie":{"ids":{"tmdb":1}}}""";
        const string showRow = """{"show":{"ids":{"tmdb":1}}}""";
        var rows = mode == "movie" ? movieRow : mode == "show" ? showRow : $"{movieRow},{showRow}";
        _responses.Record(endpoint, () => new(HttpStatusCode.OK) { Content = new StringContent($"[{rows}]") });
        var movie = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        var show = LibraryItem(SourceMediaType.Series, "Tmdb", "1");
        LibraryItem(SourceMediaType.Movie, "Tmdb", "2");

        await sync.RunAsync([$"{RecommendedUrl}/?runtimes=80-500{genres}&ratings=78-100&mode={mode}&access_token=ignored#results", url],
            Assert.Fail, default);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal(name, collection.Name);
        Guid[] expected = mode == "movie" ? [movie] : mode == "show" ? [show] : [movie, show];
        Assert.Equal(expected.Order(), collection.Members.Order());
        Assert.Contains(collection.Tags, tag => tag.StartsWith("trakt-private:", StringComparison.Ordinal));
        Assert.DoesNotContain(collection.Tags, tag => tag.Contains(RecommendedUrl, StringComparison.Ordinal));
        Assert.Equal([endpoint], _responses.Requests);
        Assert.Equal(["Bearer access-0"], _responses.Authorizations);
    });

    [Fact]
    public async Task Different_filters_or_connected_accounts_keep_separate_recommendation_collections()
    {
        const string endpoint = "https://api.trakt.tv/movies/recommendations" + RecommendationQuery;
        const string url = RecommendedUrl + "?mode=movie";
        var first = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        var second = LibraryItem(SourceMediaType.Movie, "Tmdb", "2");
        _responses.Record(endpoint, () => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"movie":{"ids":{"tmdb":1}}}]""")
        });
        _responses.Record(endpoint + "&genres=action", () => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"movie":{"ids":{"tmdb":2}}}]""")
        });
        await SyncWithTraktAccount((sync, _, _) => sync.RunAsync([url, url + "&genres=action"], Assert.Fail, default));
        Assert.Equal(2, _library.ManagedCollections.Count);
        var original = Assert.Single(_library.ManagedCollections, collection => collection.Members.Contains(first));

        _responses.Record(endpoint, () => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"movie":{"ids":{"tmdb":2}}}]""")
        });
        await SyncWithTraktAccount((sync, _, _) => sync.RunAsync([url], Assert.Fail, default), owner: "other-owner");

        Assert.Equal(3, _library.ManagedCollections.Count);
        Assert.Equal([first], original.Members);
    }

    [Fact]
    public async Task A_malformed_recommendation_response_leaves_the_existing_collection_unchanged()
        => await SyncWithTraktAccount(async (sync, _, _) =>
    {
        const string url = RecommendedUrl + "?mode=movie";
        const string endpoint = "https://api.trakt.tv/movies/recommendations" + RecommendationQuery;
        var original = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        LibraryItem(SourceMediaType.Movie, "Tmdb", "2");
        _responses.Record(endpoint, () => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"movie":{"ids":{"tmdb":1}}}]""")
        });
        await sync.RunAsync([url], Assert.Fail, default);
        var collection = Assert.Single(_library.ManagedCollections);

        _responses.Record(endpoint, () => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"movie":{"ids":{"tmdb":2}}},{"show":{"ids":{"tmdb":3}}}]""")
        });
        var warnings = new List<string>();
        await sync.RunAsync([url], Assert.Fail, default, reportPrivateFailure: warnings.Add);

        Assert.Contains("Invalid Trakt source list data", Assert.Single(warnings), StringComparison.Ordinal);
        Assert.DoesNotContain(url, warnings[0], StringComparison.Ordinal);
        Assert.Equal(collection.Id, Assert.Single(_library.ManagedCollections).Id);
        Assert.Equal([original], collection.Members);
    });

    [Fact]
    public async Task The_connected_watchlist_imports_movies_and_shows_from_every_page()
        => await SyncWithTraktAccount(async (sync, _, _) =>
    {
        _responses.Page(WatchlistItems, 1, 1, 2, Movies(1, 1, wrapped: true));
        _responses.Page(WatchlistItems, 2, 1, 2, """[{"type":"show","show":{"ids":{"tvdb":2}}}]""");
        var movie = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        var show = LibraryItem(SourceMediaType.Series, "Tvdb", "2");
        LibraryItem(SourceMediaType.Series, "Tmdb", "1");

        await sync.RunAsync([WatchlistUrl, WatchlistUrl + "/?access_token=ignored#items"], Assert.Fail, default);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("Trakt Watchlist", collection.Name);
        Assert.Equal(2, collection.Members.Count);
        Assert.Contains(movie, collection.Members);
        Assert.Contains(show, collection.Members);
        Assert.Contains(collection.Tags, tag => tag.StartsWith("trakt-private:", StringComparison.Ordinal));
        Assert.DoesNotContain(collection.Tags, tag => tag.Contains("watchlist", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([WatchlistItems + "?page=1&limit=50", WatchlistItems + "?page=2&limit=50"], _responses.Requests);
        Assert.Equal(["Bearer access-0", "Bearer access-0"], _responses.Authorizations);
    });

    [Fact]
    public async Task A_confirmed_empty_watchlist_clears_its_collection()
        => await SyncWithTraktAccount(async (sync, _, _) =>
    {
        LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        _responses.Page(WatchlistItems, 1, 50, 1, Movies(1, 1, wrapped: true));
        await sync.RunAsync([WatchlistUrl], Assert.Fail, default);
        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Single(collection.Members);

        _responses.Page(WatchlistItems, 1, 50, 0, "[]");
        await sync.RunAsync([WatchlistUrl], Assert.Fail, default);

        Assert.Equal(collection.Id, Assert.Single(_library.ManagedCollections).Id);
        Assert.Empty(collection.Members);
    });

    [Theory]
    [InlineData(WatchlistUrl)]
    [InlineData(RecommendedUrl + "?mode=media&genres=action")]
    public async Task Account_specific_sources_require_a_connection_but_do_not_block_public_sync(string url)
    {
        const string chart = "https://trakt.tv/movies/popular";
        const string chartApi = "https://api.trakt.tv/movies/popular";
        _responses.Page(chartApi, 1, 50, 1, Movies(1, 1));
        var member = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        var warnings = new List<string>();

        await _sync.RunAsync([url, chart], Assert.Fail, default, reportPrivateFailure: warnings.Add);

        Assert.Contains("Connect Trakt", Assert.Single(warnings));
        Assert.Equal([chartApi + "?page=1&limit=50"], _responses.Requests);
        Assert.Equal([member], Assert.Single(_library.ManagedCollections).Members);
    }

    [Fact]
    public async Task A_failed_watchlist_page_does_not_change_the_existing_collection()
        => await SyncWithTraktAccount(async (sync, _, _) =>
    {
        var original = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        LibraryItem(SourceMediaType.Movie, "Tmdb", "2");
        _responses.Page(WatchlistItems, 1, 50, 1, Movies(1, 1, wrapped: true));
        await sync.RunAsync([WatchlistUrl], Assert.Fail, default);
        var collection = Assert.Single(_library.ManagedCollections);

        _responses.Page(WatchlistItems, 1, 1, 2, Movies(2, 1, wrapped: true));
        _responses.Record(WatchlistItems + "?page=2&limit=50", () => new(HttpStatusCode.ServiceUnavailable));
        var warnings = new List<string>();
        await sync.RunAsync([WatchlistUrl], Assert.Fail, default, reportPrivateFailure: warnings.Add);

        Assert.Contains("HTTP 503", Assert.Single(warnings));
        Assert.DoesNotContain("watchlist", warnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(collection.Id, Assert.Single(_library.ManagedCollections).Id);
        Assert.Equal([original], collection.Members);
    });

    [Fact]
    public async Task Changing_the_connected_account_does_not_add_its_watchlist_to_the_previous_accounts_collection()
    {
        var first = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        var second = LibraryItem(SourceMediaType.Movie, "Tmdb", "2");
        _responses.Page(WatchlistItems, 1, 50, 1, Movies(1, 1, wrapped: true));
        await SyncWithTraktAccount((sync, _, _) => sync.RunAsync([WatchlistUrl], Assert.Fail, default));
        var original = Assert.Single(_library.ManagedCollections);

        _responses.Page(WatchlistItems, 1, 50, 1, Movies(2, 1, wrapped: true));
        await SyncWithTraktAccount((sync, _, _) => sync.RunAsync([WatchlistUrl], Assert.Fail, default), owner: "other-owner");

        Assert.Equal([first], original.Members);
        Assert.Equal([second], Assert.Single(_library.ManagedCollections, collection => collection.Id != original.Id).Members);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Connected_account_imports_an_inaccessible_list_from_the_regular_source_URLs(HttpStatusCode denial)
        => await SyncWithTraktAccount(async (sync, _, _) =>
    {
        _responses.DeniedWithoutAccount.Add(ListSummary, denial);
        _responses.Summary();
        _responses.Page(ListItems, 1, 50, 1, Movies(1, 1, wrapped: true));
        var member = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");

        await sync.RunAsync([ListUrl], Assert.Fail, default);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal([member], collection.Members);
        Assert.DoesNotContain(ListUrl, collection.Tags);
        Assert.Equal([null, "Bearer access-0", "Bearer access-0"], _responses.Authorizations);
    });

    [Fact]
    public async Task Public_summary_with_inaccessible_items_retries_the_entire_list_with_the_connected_account()
        => await SyncWithTraktAccount(async (sync, _, _) =>
    {
        var itemsPage = ListItems + "?page=1&limit=50";
        _responses.DeniedWithoutAccount.Add(itemsPage, HttpStatusCode.NotFound);
        _responses.Summary();
        _responses.Page(ListItems, 1, 50, 1, Movies(1, 1, wrapped: true));
        var member = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");

        await sync.RunAsync([ListUrl], Assert.Fail, default);

        Assert.Equal([member], Assert.Single(_library.ManagedCollections).Members);
        Assert.Equal([ListSummary, itemsPage, ListSummary, itemsPage], _responses.Requests);
        Assert.Equal([null, null, "Bearer access-0", "Bearer access-0"], _responses.Authorizations);
    });

    [Fact]
    public async Task A_denied_private_page_leaves_the_existing_collection_unchanged_and_public_sync_continues()
        => await SyncWithTraktAccount(async (sync, _, _) =>
    {
        const string chart = "https://trakt.tv/movies/popular";
        const string chartApi = "https://api.trakt.tv/movies/popular";
        _responses.DeniedWithoutAccount.Add(ListSummary, HttpStatusCode.NotFound);
        _responses.Summary("""{"name":"Owner's list","description":"Original notes"}""");
        _responses.Page(ListItems, 1, 1, 1, Movies(1, 1, wrapped: true));
        var original = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        var newMember = LibraryItem(SourceMediaType.Movie, "Tmdb", "2");
        var warnings = new List<string>();
        Task Run(params string[] urls) => sync.RunAsync(urls, Assert.Fail, default, reportPrivateFailure: warnings.Add);

        await Run(ListUrl);
        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal([original], collection.Members);
        Assert.DoesNotContain(collection.Tags, tag => tag.Contains("open-your-eyes", StringComparison.Ordinal));
        Assert.Empty(warnings);

        _responses.Summary("""{"name":"Owner's list","description":"New notes"}""");
        _responses.Page(ListItems, 1, 1, 2, Movies(2, 1, wrapped: true));
        _responses.Record(ListItems + "?page=2&limit=50", () => new(HttpStatusCode.NotFound));
        _responses.Page(chartApi, 1, 50, 1, Movies(2, 1));
        await Run(ListUrl, chart, "https://trakt.tv/users/justin/lists/open-your-eyes/");

        Assert.Equal([original], collection.Members);
        Assert.Equal("Original notes", collection.Overview);
        Assert.DoesNotContain("open-your-eyes", Assert.Single(warnings));
        Assert.Equal([newMember], _library.ManagedCollections.Single(c => c.Name == "Popular Movies").Members);
        Assert.Equal(5, _responses.Authorizations.Count(header => header == "Bearer access-0"));
        Assert.Null(_responses.Authorizations[_responses.Requests.IndexOf(chartApi + "?page=1&limit=50")]);
    });

    [Fact]
    public async Task Scheduled_reads_refresh_a_token_once_and_reuse_the_rotated_refresh_token()
        => await SyncWithTraktAccount(async (sync, clock, account) =>
    {
        _responses.DeniedWithoutAccount.Add(ListSummary, HttpStatusCode.NotFound);
        _responses.Summary();
        _responses.Page(ListItems, 1, 50, 0, "[]");
        var warnings = new List<string>();
        Task Run() => sync.RunAsync([ListUrl], Assert.Fail, default, reportPrivateFailure: warnings.Add);

        clock.Now += TimeSpan.FromDays(7);
        await Run();
        Assert.Equal([null, "Bearer access-1", "Bearer access-1"], _responses.Authorizations);
        clock.Now += TimeSpan.FromDays(7);
        await Run();
        Assert.Equal(["refresh-0", "refresh-1"], account.RefreshRequests);
        Assert.Equal([null, "Bearer access-1", "Bearer access-1", null, "Bearer access-2", "Bearer access-2"], _responses.Authorizations);
        Assert.Empty(warnings);

        clock.Now += TimeSpan.FromDays(7);
        account.RejectRefresh = true;
        await Run();
        Assert.Contains("Connect Trakt again", Assert.Single(warnings));
        Assert.Equal(7, _responses.Requests.Count);
        await Run();
        Assert.Equal(["refresh-0", "refresh-1", "refresh-2"], account.RefreshRequests);
    });

    [Fact]
    public async Task A_list_that_becomes_private_keeps_one_collection_and_loses_its_public_tag()
        => await SyncWithTraktAccount(async (sync, _, _) =>
    {
        _responses.Summary();
        _responses.Page(ListItems, 1, 50, 1, Movies(1, 1, wrapped: true));
        var member = LibraryItem(SourceMediaType.Movie, "Tmdb", "1");
        await sync.RunAsync([ListUrl], Assert.Fail, default);
        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Contains(ListUrl, collection.Tags);
        Assert.All(_responses.Authorizations, Assert.Null);

        _responses.DeniedWithoutAccount.Add(ListSummary, HttpStatusCode.NotFound);
        _responses.Record(ListSummary, () => new(HttpStatusCode.Forbidden));
        var warnings = new List<string>();
        await sync.RunAsync([ListUrl], Assert.Fail, default, reportPrivateFailure: warnings.Add);
        Assert.Single(warnings);
        Assert.Contains(ListUrl, collection.Tags);

        _responses.Summary();
        await sync.RunAsync([ListUrl], Assert.Fail, default, reportPrivateFailure: Assert.Fail);
        Assert.Equal(collection.Id, Assert.Single(_library.ManagedCollections).Id);
        Assert.DoesNotContain(ListUrl, collection.Tags);
        Assert.Equal([member], collection.Members);

        _responses.DeniedWithoutAccount.Remove(ListSummary);
        await sync.RunAsync([ListUrl], Assert.Fail, default);
        Assert.Equal(collection.Id, Assert.Single(_library.ManagedCollections).Id);
        Assert.Contains(ListUrl, collection.Tags);
    });

    [Fact]
    public async Task Private_sync_does_not_use_the_connected_token_for_another_owners_list()
        => await SyncWithTraktAccount(async (sync, _, _) =>
    {
        const string otherOwner = "https://api.trakt.tv/users/someone-else/lists/example/";
        _responses.DeniedWithoutAccount.Add(otherOwner, HttpStatusCode.NotFound);
        var warnings = new List<string>();
        await sync.RunAsync(["https://trakt.tv/users/someone-else/lists/example"], Assert.Fail, default, reportPrivateFailure: warnings.Add);
        Assert.Equal([otherOwner], _responses.Requests);
        Assert.Equal([null], _responses.Authorizations);
        Assert.Contains("must belong to the connected Trakt account", Assert.Single(warnings));
    });

    private async Task SyncWithTraktAccount(Func<CollectionSync, TestClock, TraktTestAccount, Task> run, string owner = "justin")
    {
        var data = Path.Combine(Path.GetTempPath(), $"magic-trakt-{Guid.NewGuid():N}");
        var configuration = new PluginConfiguration();
        using var account = new TraktTestAccount(_responses, owner);
        using var http = new HttpClient(account, disposeHandler: false);
        var clock = new TestClock();
        var connection = new TraktConnection(http, () => configuration, data, clock);
        try
        {
            var admin = new TraktAdminSession(Guid.NewGuid(), "admin");
            var signIn = await connection.StartAsync(admin, default);
            var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(signIn.AuthorizationUrl).Query)["state"].ToString();
            await connection.CompleteAsync(admin, new("test-code", state), default);
            _responses.ExpectAuthenticatedLists = true;
            await run(new CollectionSync([new TraktHandler(http, () => configuration, connection)], _library), clock, account);
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
        }
    }

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
        public List<string?> Authorizations { get; } = [];
        public bool ExpectAuthenticatedLists { get; set; }
        public Dictionary<string, HttpStatusCode> DeniedWithoutAccount { get; } = [];

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
            if (request.Headers.Authorization is not null)
            {
                Assert.True(ExpectAuthenticatedLists &&
                    (request.RequestUri.AbsolutePath.StartsWith("/users/", StringComparison.Ordinal) ||
                        request.RequestUri.AbsolutePath is "/movies/recommendations" or "/shows/recommendations" or "/media/recommendations"));
            }
            Authorizations.Add(request.Headers.Authorization?.ToString());
            ClientIds.Add(Assert.Single(request.Headers.GetValues("trakt-api-key")));
            var endpoint = request.RequestUri.AbsoluteUri;
            Requests.Add(endpoint);
            if (request.Headers.Authorization is null && DeniedWithoutAccount.TryGetValue(endpoint, out var denial))
            {
                return Task.FromResult(new HttpResponseMessage(denial));
            }

            return Task.FromResult(_responses.TryGetValue(endpoint, out var respond)
                ? respond()
                : throw new InvalidOperationException($"No Trakt recording for {endpoint}"));
        }
    }

    private sealed class TraktTestAccount : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _lists;
        private readonly string _owner;
        public List<string> RefreshRequests { get; } = [];
        public bool RejectRefresh { get; set; }

        public TraktTestAccount(HttpMessageHandler lists, string owner)
        {
            _lists = new(lists, disposeHandler: false);
            _owner = owner;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsoluteUri == "https://auth.trakt.tv/oauth/token")
            {
                Assert.Equal("MagicCollections", Assert.Single(request.Headers.UserAgent).Product?.Name);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.False(body.RootElement.TryGetProperty("client_secret", out _));
                if (body.RootElement.GetProperty("grant_type").GetString() == "refresh_token")
                {
                    RefreshRequests.Add(body.RootElement.GetProperty("refresh_token").GetString()!);
                    Assert.False(body.RootElement.TryGetProperty("code_verifier", out _));
                }
                if (RejectRefresh)
                {
                    return Reply("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                }
                return Reply(JsonSerializer.Serialize(new
                {
                    access_token = $"access-{RefreshRequests.Count}",
                    refresh_token = $"refresh-{RefreshRequests.Count}",
                    expires_in = 604800
                }));
            }
            return request.RequestUri.AbsoluteUri == "https://api.trakt.tv/users/settings"
                ? Reply(JsonSerializer.Serialize(new { user = new { username = _owner, ids = new { slug = _owner } } }))
                : await _lists.SendAsync(request, token);
        }

        private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json) };

        protected override void Dispose(bool disposing)
        {
            if (disposing) _lists.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
