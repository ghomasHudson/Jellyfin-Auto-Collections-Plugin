using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.MagicCollections.Sync;
using Xunit;

namespace MagicCollections.Tests;

public sealed class ImdbCollectionSyncTests
{
    private const string ListUrl = "https://www.imdb.com/list/ls055592025/";
    private const string MovieChart = "https://www.imdb.com/chart/top/";
    private const string TvChart = "https://www.imdb.com/chart/toptv/";

    private readonly RecordedPages _pages = new();
    private readonly ControlledLibrary _library = new();

    [Theory]
    [InlineData(ListUrl, ListUrl)]
    [InlineData("https://www.imdb.com/list/ls055592025", ListUrl)]
    [InlineData("https://IMDb.com/list/ls055592025/?ref_=ext_shr_lnk#items", ListUrl)]
    [InlineData("https://www.IMDb.com:443/list/ls055592025/#items", ListUrl)]
    [InlineData(MovieChart, MovieChart)]
    [InlineData("https://imdb.com/chart/top?ref_=nv_mv_250", MovieChart)]
    [InlineData(TvChart, TvChart)]
    [InlineData("https://www.IMDB.com/chart/toptv#shows", TvChart)]
    public void Canonicalizes_supported_urls_without_rendering(string url, string canonical)
    {
        var handler = new ImdbHandler(_pages);
        Assert.True(handler.Supports(new(url)));
        Assert.Equal(canonical, handler.GetCanonicalUrl(new(url)).AbsoluteUri);
        Assert.Empty(_pages.Requests);
    }

    [Theory]
    [InlineData("/list/ls055592025/")]
    [InlineData("http://www.imdb.com/list/ls055592025/")]
    [InlineData("https://www.imdb.com.example/list/ls055592025/")]
    [InlineData("https://www.imdb.com./list/ls055592025/")]
    [InlineData("https://user@www.imdb.com/list/ls055592025/")]
    [InlineData("https://@www.imdb.com/list/ls055592025/")]
    [InlineData("https://www.imdb.com@localhost/list/ls055592025/")]
    [InlineData("https://www.imdb.com:8443/list/ls055592025/")]
    [InlineData(ListUrl + "?page=2")]
    [InlineData(ListUrl + "?ref_=ext_shr_lnk&page=2#items")]
    [InlineData(ListUrl + "?sort=rank")]
    [InlineData(ListUrl + "edit")]
    [InlineData(ListUrl + "/")]
    [InlineData("https://www.imdb.com/list/ls/")]
    [InlineData("https://www.imdb.com/list/%6cs055592025/")]
    [InlineData("https://www.imdb.com/chart/other/../top/")]
    [InlineData("https://www.imdb.com/chart/%2e/top/")]
    [InlineData(@"https://www.imdb.com\chart\top\")]
    [InlineData("https://www.imdb.com/chart/boxoffice/")]
    [InlineData("https://www.imdb.com/chart/moviemeter/")]
    [InlineData("https://www.imdb.com/title/tt0111161/")]
    public async Task Reports_other_IMDb_URLs_as_unsupported_without_rendering(string url)
    {
        var unsupported = new List<string>();
        await new CollectionSync([new ImdbHandler(_pages)], _library)
            .RunAsync([url], unsupported.Add, CancellationToken.None);

        Assert.Equal([url], unsupported);
        Assert.Empty(_pages.Requests);
    }

    [Fact]
    public async Task Syncs_a_mixed_title_list_by_typed_Imdb_ID()
    {
        _pages.Record(ListUrl, ListFixture());
        var movie = AddMedia("Alternate movie name", "tt0111161", SourceMediaType.Movie);
        var series = AddMedia("Localized series name", "tt0903747", SourceMediaType.Series);
        var miniseries = AddMedia("Chernobyl", "tt7366338", SourceMediaType.Series);
        AddMedia("The Shawshank Redemption", "tt0000001", SourceMediaType.Movie);
        AddMedia("Different media type", "tt0111161", SourceMediaType.Series);
        AddMedia("Different media type", "tt0903747", SourceMediaType.Movie);
        AddMedia("Episode not a series", "tt2301451", SourceMediaType.Series);
        AddMedia("Unrelated title", "tt0468569", SourceMediaType.Movie);

        await SyncAsync("https://IMDb.com/list/ls055592025?ref_=ext_shr_lnk#items", ListUrl);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("Shared Favorites", collection.Name);
        Assert.Equal("Picks for a long weekend.", collection.Overview);
        Assert.Equal(["Magic Collection", ListUrl, "IMDb"], collection.Tags);
        Assert.Equal(new[] { movie, series, miniseries }.Order(), collection.Members.Order());
        Assert.Equal([ListUrl], _pages.Requests);
    }

    [Fact]
    public async Task Covers_all_raw_title_rows_across_numbered_pages_before_filtering()
    {
        _pages.Record(ListUrl, ListPage(1, 501, true, first: 1, count: 250, kind: "tvEpisode"));
        _pages.Record(ListUrl + "?page=2", ListPage(2, 501, true, first: 251, count: 250));
        _pages.Record(ListUrl + "?page=3", ListPage(3, 501, false, first: 501, count: 1, kind: "tvMiniSeries"));

        var list = await ReadAsync(ListUrl);

        Assert.Equal(251, list.ProviderItems.Count);
        Assert.Equal(Id(251), list.ProviderItems[0].ProviderIds["Imdb"]);
        Assert.Equal(Id(501), list.ProviderItems[^1].ProviderIds["Imdb"]);
        Assert.Equal(SourceMediaType.Series, list.ProviderItems[^1].Type);
        Assert.Equal([ListUrl, ListUrl + "?page=2", ListUrl + "?page=3"], _pages.Requests);
    }

    [Fact]
    public async Task Counts_repeated_titles_at_different_positions_but_returns_each_identity_once()
    {
        _pages.Record(ListUrl, ListPage(1, 251, true, first: 1, count: 250));
        var last = ListPage(2, 251, false, first: 251, count: 1);
        Rows(last)[0]!["listItem"]!["id"] = Id(1);
        Rows(last)[0]!["node"]!["itemId"] = Id(1);
        _pages.Record(ListUrl + "?page=2", last);

        var list = await ReadAsync(ListUrl);

        Assert.Equal(Enumerable.Range(1, 250).Select(Id), list.ProviderItems.Select(item => item.ProviderIds["Imdb"]));
        Assert.Equal([ListUrl, ListUrl + "?page=2"], _pages.Requests);
    }

    [Fact]
    public async Task Fails_closed_if_IMDb_reports_more_pages_after_the_pagination_limit()
    {
        for (var page = 1; page <= 60; page++)
        {
            var url = page == 1 ? ListUrl : FormattableString.Invariant($"{ListUrl}?page={page}");
            _pages.Record(url, ListPage(page, 61, true, first: page, count: 1));
        }

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => ReadAsync(ListUrl));

        Assert.Contains("exceeds the limit", error.Message);
        Assert.Equal(60, _pages.Requests.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Accepts_a_verified_empty_or_wholly_excluded_title_list(int count)
    {
        var empty = ListPage(1, count, false, first: 1, count, kind: "tvEpisode");
        List(empty)["name"] = "An empty or wholly excluded list";
        _pages.Record(ListUrl, empty);

        var list = await ReadAsync(ListUrl);

        Assert.Equal("An empty or wholly excluded list", list.Name);
        Assert.Empty(list.ProviderItems);
        Assert.Equal([ListUrl], _pages.Requests);
    }

    [Theory]
    [InlineData("wrong page")]
    [InlineData("conflicting type")]
    [InlineData("wrong position")]
    [InlineData("missing position")]
    [InlineData("total changed")]
    [InlineData("truncated")]
    [InlineData("empty")]
    [InlineData("missing next flag")]
    [InlineData("malformed JSON")]
    [InlineData("renderer failure")]
    public async Task A_failed_later_page_does_not_update_the_managed_collection(string problem)
    {
        _pages.Record(ListUrl, ListPage(1, 252, true, first: 1, count: 250));
        var nextUrl = ListUrl + "?page=2";
        var next = ListPage(2, 252, false, first: 251, count: 2);
        switch (problem)
        {
            case "wrong page": PageProps(next)["initialPageNumber"] = 1; break;
            case "conflicting type":
                Rows(next)[0]!["listItem"]!["id"] = Id(1);
                Rows(next)[0]!["listItem"]!["titleType"]!["id"] = "tvSeries";
                break;
            case "wrong position": Rows(next)[0]!["node"]!["absolutePosition"] = 250; break;
            case "missing position": Rows(next)[0]!["node"]!.AsObject().Remove("absolutePosition"); break;
            case "total changed": Search(next)["total"] = 253; break;
            case "truncated": Rows(next).RemoveAt(1); break;
            case "empty": Rows(next).Clear(); break;
            case "missing next flag": Search(next).AsObject()["pageInfo"] = new JsonObject(); break;
        }
        _pages.Record(nextUrl, next);
        if (problem == "malformed JSON")
        {
            _pages.Responses[nextUrl] = _ => Task.FromResult("<html>Could not load the next page</html>");
        }
        else if (problem == "renderer failure")
        {
            _pages.Responses[nextUrl] = _ => Task.FromException<string>(
                new SourceListReadException(new(nextUrl), "IMDb did not return the requested page."));
        }

        AddMedia("First-page movie", Id(1), SourceMediaType.Movie);
        await _library.CreateAsync("Existing", "Magic Collection", ListUrl, "IMDb");
        var collection = Assert.Single(_library.ManagedCollections);
        collection.Overview = "Keep my overview";

        await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(ListUrl));

        Assert.Empty(collection.Members);
        Assert.Equal("Keep my overview", collection.Overview);
        Assert.Null(collection.PosterTitle);
        Assert.Equal([ListUrl, nextUrl], _pages.Requests);
    }

    [Theory]
    [InlineData("PRIVATE", "TITLES", "ls055592025", "tt0111161", "movie")]
    [InlineData("PUBLIC", "NAMES", "ls055592025", "tt0111161", "movie")]
    [InlineData("PUBLIC", "TITLES", "ls000000000", "tt0111161", "movie")]
    [InlineData("PUBLIC", "TITLES", "ls055592025", "tt0111161 ", "tvEpisode")]
    [InlineData("PUBLIC", "TITLES", "ls055592025", "tt0111161", null)]
    [InlineData("PUBLIC", "TITLES", "ls055592025", "tt0111161", "unrecognized")]
    public async Task Rejects_unexpected_list_identity_visibility_or_title_data(
        string visibility, string type, string listId, string itemId, string? itemType)
    {
        var data = ListFixture();
        List(data)["visibility"]!["id"] = visibility;
        List(data)["listType"]!["id"] = type;
        List(data)["id"] = listId;
        Rows(data)[0]!["listItem"]!["id"] = itemId;
        Rows(data)[0]!["listItem"]!["titleType"]!["id"] = itemType;
        _pages.Record(ListUrl, data);

        await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(ListUrl));
        Assert.Empty(_library.ManagedCollections);
    }

    [Theory]
    [InlineData(MovieChart, false, "IMDb Top 250 Movies")]
    [InlineData(TvChart, true, "IMDb Top 250 TV Shows")]
    public async Task Reads_each_complete_IMDb_chart_in_order(string url, bool television, string name)
    {
        _pages.Responses[url] = _ => Task.FromResult(Chart(250, television));

        var list = await ReadAsync(url);

        Assert.Equal(name, list.Name);
        Assert.Equal(Enumerable.Range(1, 250).Select(Id), list.ProviderItems.Select(item => item.ProviderIds["Imdb"]));
        Assert.All(list.ProviderItems, item => Assert.Equal(television ? SourceMediaType.Series : SourceMediaType.Movie, item.Type));

        var match = AddMedia("Matching title", Id(250), television ? SourceMediaType.Series : SourceMediaType.Movie);
        AddMedia("Wrong media type", Id(250), television ? SourceMediaType.Movie : SourceMediaType.Series);
        await SyncAsync(url);
        Assert.Equal([match], Assert.Single(_library.ManagedCollections).Members);
    }

    [Theory]
    [InlineData(175, false, false, false)]
    [InlineData(249, false, false, false)]
    [InlineData(251, false, false, false)]
    [InlineData(250, true, false, false)]
    [InlineData(250, false, true, false)]
    [InlineData(250, false, false, true)]
    public async Task Rejects_chart_data_that_does_not_prove_250_distinct_typed_titles(
        int count, bool duplicate, bool wrongType, bool hasNextPage)
    {
        _pages.Responses[TvChart] = _ => Task.FromResult(Chart(count, television: true, duplicate, wrongType, hasNextPage));

        await Assert.ThrowsAsync<SourceListReadException>(() => ReadAsync(TvChart));
    }

    [Fact]
    public async Task Preserves_caller_cancellation_but_reports_renderer_timeouts_as_failures()
    {
        _pages.Responses[ListUrl] = _ => Task.FromCanceled<string>(new CancellationToken(true));
        await Assert.ThrowsAsync<SourceListReadException>(() => ReadAsync(ListUrl));

        using var cancellation = new CancellationTokenSource();
        _pages.Responses[ListUrl] = ct =>
        {
            Assert.Equal(cancellation.Token, ct);
            cancellation.Cancel();
            return Task.FromCanceled<string>(ct);
        };
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ImdbHandler(_pages).ReadAsync(new(ListUrl), cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    private Task<SourceList> ReadAsync(string url) => new ImdbHandler(_pages).ReadAsync(new(url), CancellationToken.None);

    private Task SyncAsync(params string[] urls) =>
        new CollectionSync([new ImdbHandler(_pages)], _library)
            .RunAsync(urls, url => Assert.Fail($"Unexpected unsupported URL: {url}"), CancellationToken.None);

    private Guid AddMedia(string title, string id, SourceMediaType type)
    {
        var item = Guid.NewGuid();
        _library.Media.Add(new(item, new(type, new Dictionary<string, string> { ["Imdb"] = id }), new(title, 1994)));
        return item;
    }

    private static string Id(int position) => FormattableString.Invariant($"tt{position + 10_000_000}");

    private static JsonNode ListFixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "imdb-title-list.json")))!;

    private static JsonNode PageProps(JsonNode data) => data["props"]!["pageProps"]!;
    private static JsonNode List(JsonNode data) => PageProps(data)["mainColumnData"]!["list"]!;
    private static JsonNode Search(JsonNode data) => List(data)["titleListItemSearch"]!;
    private static JsonArray Rows(JsonNode data) => Search(data)["edges"]!.AsArray();

    private static JsonNode ListPage(int page, int total, bool hasNext, int first, int count, string kind = "movie")
    {
        var data = ListFixture();
        PageProps(data)["initialPageNumber"] = page;
        Search(data)["total"] = total;
        Search(data)["pageInfo"]!["hasNextPage"] = hasNext;
        Search(data)["edges"] = JsonSerializer.SerializeToNode(Enumerable.Range(first, count).Select(position => new
        {
            node = new { absolutePosition = position, itemId = Id(position) },
            listItem = Title(Id(position), kind)
        }));
        return data;
    }

    private static string Chart(int count, bool television, bool duplicate = false, bool wrongType = false, bool hasNextPage = false)
    {
        var edges = Enumerable.Range(1, count).Select(rank => new
        {
            currentRank = rank,
            node = Title(Id(duplicate && rank == 250 ? 1 : rank),
                television && !(wrongType && rank == 250) ? (rank % 5 == 0 ? "tvMiniSeries" : "tvSeries") : "movie")
        });
        var chartTitles = new { edges, pageInfo = television ? new { hasNextPage } : null as object };
        return JsonSerializer.Serialize(new { props = new { pageProps = new { pageData = new { chartTitles } } } });
    }

    private static JsonObject Title(string id, string kind) => new() { ["id"] = id, ["titleType"] = new JsonObject { ["id"] = kind } };

    private sealed class RecordedPages : IImdbPageRenderer
    {
        public List<string> Requests { get; } = [];
        public Dictionary<string, Func<CancellationToken, Task<string>>> Responses { get; } = [];

        public void Record(string url, JsonNode data) => Responses[url] = _ => Task.FromResult(data.ToJsonString());

        public Task<string> RenderAsync(Uri canonicalUrl, CancellationToken ct)
        {
            Requests.Add(canonicalUrl.AbsoluteUri);
            return Responses[canonicalUrl.AbsoluteUri](ct);
        }
    }
}
