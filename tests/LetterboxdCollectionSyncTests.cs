using System.Net;
using Jellyfin.Plugin.MagicCollections.Sync;
using Xunit;

namespace MagicCollections.Tests;

public sealed class LetterboxdCollectionSyncTests : IDisposable
{
    private const string Origin = "https://letterboxd.com";
    private const string ListPath = "/alex_test/list/weekend_films/";
    private const string ListUrl = Origin + ListPath;
    private const string MoviePath = "/film/the-example/";
    private const string MovieProviderLink = """<a href="https://www.themoviedb.org/movie/496243/" class="micro-button track-event" data-track-action="TMDB">TMDB</a>""";
    private readonly ControlledLibrary _library = new();
    private readonly RecordedHttpResponses _responses = new();
    private readonly HttpClient _http;
    private readonly CollectionSync _sync;

    public LetterboxdCollectionSyncTests()
    {
        _http = new HttpClient(_responses);
        _sync = new CollectionSync([new LetterboxdHandler(_http)], _library);
    }

    [Fact]
    public async Task Syncs_a_public_Letterboxd_list_by_provider_ID()
    {
        RecordHtml(ListPath, ListPage(ListPath, 1, Poster(0, MoviePath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html"));
        var movie = AddMedia(SourceMediaType.Movie, "Tmdb", "496243");
        AddMedia(SourceMediaType.Movie, "Tmdb", "900");

        await SyncListAsync();

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("Weekend Films & Friends", collection.Name);
        Assert.Equal($"AutoCollections:{ListUrl}", collection.Tag);
        Assert.Equal([movie], collection.Members);
    }

    [Fact]
    public async Task Syncs_every_page_and_matches_whole_series_using_typed_Tmdb_or_Imdb()
    {
        const string pageTwo = ListPath + "page/2/";
        const string seriesPath = "/film/the-example-series/";
        RecordHtml(ListPath, ListPage(ListPath, 2, Poster(0, MoviePath), pageTwo));
        RecordHtml(pageTwo, ListPage(pageTwo, 2, Poster(1, seriesPath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html"));
        RecordHtml(seriesPath, Fixture("letterboxd-series.html"));
        var movie = AddMedia(SourceMediaType.Movie, "Imdb", "tt6751668");
        var tmdbSeries = AddMedia(SourceMediaType.Series, "Tmdb", "87108");
        var imdbSeries = AddMedia(SourceMediaType.Series, "Imdb", "tt7366338");
        AddMedia(SourceMediaType.Movie, "Tmdb", "87108");
        AddMedia(SourceMediaType.Movie, "Imdb", "tt7366338");

        await SyncListAsync();

        var members = Assert.Single(_library.ManagedCollections).Members;
        Assert.Equal(new[] { movie, tmdbSeries, imdbSeries }.Order(), members.Order());
    }

    [Fact]
    public async Task Accepts_a_confirmed_public_empty_list()
    {
        RecordHtml(ListPath, ListPage(ListPath, 0, ""));
        AddMedia(SourceMediaType.Movie, "Tmdb", "496243");

        await SyncListAsync();

        Assert.Empty(_library.ManagedCollections);
    }

    [Theory]
    [InlineData("private list")]
    [InlineData("challenge")]
    [InlineData("wrong page")]
    [InlineData("missing next")]
    [InlineData("changed count")]
    [InlineData("missing poster")]
    [InlineData("shifted index")]
    [InlineData("truncated HTML")]
    public async Task Leaves_the_managed_collection_untouched_if_list_membership_cannot_be_verified(string failure)
    {
        const string pageTwo = ListPath + "page/2/";
        var firstPage = ListPage(ListPath, 2, Poster(0, MoviePath), pageTwo);
        var secondPage = ListPage(pageTwo, 2, Poster(1, MoviePath));
        var (first, second) = failure switch
        {
            "private list" => (firstPage.Replace("data-list-type=\"public\"", "data-list-type=\"private\""), secondPage),
            "challenge" => (firstPage, "<html><title>Just a moment...</title></html>"),
            "wrong page" => (firstPage, ListPage(ListPath, 2, Poster(1, MoviePath))),
            "missing next" => (ListPage(ListPath, 2, Poster(0, MoviePath)), secondPage),
            "changed count" => (firstPage, ListPage(pageTwo, 1, "")),
            "missing poster" => (firstPage, ListPage(pageTwo, 2, """<li class="posteritem" data-object-id="filmListEntry:901"></li>""")),
            "shifted index" => (firstPage, ListPage(pageTwo, 2, Poster(2, MoviePath))),
            "truncated HTML" => (firstPage, secondPage[..secondPage.IndexOf("<footer", StringComparison.Ordinal)]),
            _ => throw new ArgumentException(failure)
        };
        RecordHtml(ListPath, first);
        RecordHtml(pageTwo, second);
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html"));
        AddMedia(SourceMediaType.Movie, "Tmdb", "496243");

        await AssertFailedWithoutChangesAsync();
    }

    [Fact]
    public async Task Skips_valid_details_without_verified_provider_ids_and_never_matches_a_title_alone()
    {
        const string untypedPath = "/film/the-example-series/";
        const string missingIdsPath = "/film/no-provider-ids/";
        RecordHtml(ListPath, ListPage(ListPath, 3, Poster(0, MoviePath) + Poster(1, untypedPath) + Poster(2, missingIdsPath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html"));
        RecordHtml(untypedPath, Fixture("letterboxd-series.html")
            .Replace("https://www.themoviedb.org/tv/87108/", "https://www.themoviedb.org/87108/"));
        RecordHtml(missingIdsPath, Fixture("letterboxd-no-provider.html"));
        var movie = AddMedia(SourceMediaType.Movie, "Tmdb", "496243");
        AddMedia(SourceMediaType.Movie, "Imdb", "tt7366338");
        AddMedia(SourceMediaType.Series, "Imdb", "tt7366338");
        AddMedia(SourceMediaType.Movie, "Tmdb", "902"); // Site-local list entry ID.
        _library.Media.Add(new(Guid.NewGuid(), new(SourceMediaType.Movie, new Dictionary<string, string>())));

        await SyncListAsync();

        Assert.Equal([movie], Assert.Single(_library.ManagedCollections).Members);
    }

    [Fact]
    public async Task Can_use_an_Imdb_link_when_the_detail_page_contains_a_valid_typed_Tmdb_ID()
    {
        RecordHtml(ListPath, ListPage(ListPath, 1, Poster(0, MoviePath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html")
            .Replace(MovieProviderLink, ""));
        var movie = AddMedia(SourceMediaType.Movie, "Imdb", "tt6751668");

        await SyncListAsync();

        Assert.Equal([movie], Assert.Single(_library.ManagedCollections).Members);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("redirect")]
    [InlineData("partial HTTP")]
    [InlineData("challenge")]
    [InlineData("wrong film")]
    [InlineData("truncated HTML")]
    [InlineData("connection failed")]
    public async Task Leaves_the_managed_collection_untouched_when_a_detail_page_fails_after_a_valid_match(string problem)
    {
        const string failingFilmPath = "/film/failing-film/";
        RecordHtml(ListPath, ListPage(ListPath, 2, Poster(0, MoviePath) + Poster(1, failingFilmPath)));
        var moviePage = Fixture("letterboxd-movie.html");
        RecordHtml(MoviePath, moviePage);
        var failingPage = moviePage.Replace(MoviePath, failingFilmPath);
        RecordHtml(failingFilmPath, failingPage);
        switch (problem)
        {
            case "blocked":
                _responses.Record(Origin + failingFilmPath, () => new(HttpStatusCode.Forbidden));
                break;
            case "redirect":
                _responses.Record(Origin + failingFilmPath, () => new(HttpStatusCode.Found)
                {
                    Headers = { Location = new("https://example.org/film/failing-film/") }
                });
                break;
            case "partial HTTP":
                _responses.Record(Origin + failingFilmPath, () => new(HttpStatusCode.PartialContent)
                {
                    Content = new StringContent(failingPage, null, "text/html")
                });
                break;
            case "challenge":
                RecordHtml(failingFilmPath, "<html><title>Just a moment...</title></html>");
                break;
            case "wrong film":
                RecordHtml(failingFilmPath, moviePage);
                break;
            case "truncated HTML":
                RecordHtml(failingFilmPath, failingPage[..failingPage.IndexOf("</body>", StringComparison.Ordinal)]);
                break;
            case "connection failed":
                _responses.Record(Origin + failingFilmPath, () => throw new HttpRequestException("Connection failed."));
                break;
        }
        AddMedia(SourceMediaType.Movie, "Tmdb", "496243");

        await AssertFailedWithoutChangesAsync();
    }

    [Fact]
    public async Task Does_not_make_requests_for_noncanonical_Letterboxd_source_URLs()
    {
        string[] urls =
        [
            "http://letterboxd.com/alex_test/list/weekend_films/",
            "https://www.letterboxd.com/alex_test/list/weekend_films/",
            "https://letterboxd.com.example/alex_test/list/weekend_films/",
            "https://user@letterboxd.com/alex_test/list/weekend_films/",
            "https://letterboxd.com:8080/alex_test/list/weekend_films/",
            "https://letterboxd.com/alex_test/list/weekend_films",
            ListUrl + "?sort=rating",
            ListUrl + "#items",
            ListUrl + "page/2/",
            Origin + "/alex_test/watchlist/",
            Origin + "/alex_test/likes/films/",
            Origin + "/alex_test/list/skipped/../",
            Origin + "/film/the-example/"
        ];
        var unsupported = new List<string>();

        await _sync.RunAsync(urls, unsupported.Add, CancellationToken.None);

        Assert.Equal(urls, unsupported);
        Assert.Empty(_library.ManagedCollections);
    }

    [Fact]
    public async Task A_direct_Tmdb_link_controls_type_even_when_the_body_has_a_valid_numeric_ID()
    {
        RecordHtml(ListPath, ListPage(ListPath, 1, Poster(0, MoviePath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html")
            .Replace("/movie/496243/", "/tv/87108/")
            .Replace("data-track-action=\"TMDB\"", "data-track-action=\"TMDb\""));
        var series = AddMedia(SourceMediaType.Series, "Tmdb", "87108");
        AddMedia(SourceMediaType.Movie, "Tmdb", "496243");
        AddMedia(SourceMediaType.Movie, "Imdb", "tt6751668");

        await SyncListAsync();

        Assert.Equal([series], Assert.Single(_library.ManagedCollections).Members);
    }

    [Theory]
    [InlineData("https://www.themoviedb.org.example/movie/496243/")]
    [InlineData("https://www.themoviedb.org/tv/87108/season/1/")]
    [InlineData("https://www.themoviedb.org/collection/496243/")]
    public async Task Does_not_use_invalid_provider_links_to_establish_type_or_identity(string tmdbLink)
    {
        RecordHtml(ListPath, ListPage(ListPath, 1, Poster(0, MoviePath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html")
            .Replace("https://www.themoviedb.org/movie/496243/", tmdbLink));
        AddMedia(SourceMediaType.Movie, "Tmdb", "496243");
        AddMedia(SourceMediaType.Movie, "Imdb", "tt6751668");
        AddMedia(SourceMediaType.Series, "Tmdb", "87108");

        await SyncListAsync();

        Assert.Empty(_library.ManagedCollections);
    }

    [Theory]
    [InlineData(true, "//example.org/alex_test/list/weekend_films/page/2/")]
    [InlineData(true, "https://letterboxd.com/another/list/weekend_films/page/2/")]
    [InlineData(true, ListPath)]
    [InlineData(true, ListPath + "page/3/")]
    [InlineData(false, "https://example.org/film/the-example/")]
    [InlineData(false, "/alex_test/watchlist/")]
    [InlineData(false, "/film/../film/the-example/")]
    [InlineData(false, "/film/the-example/?next=1")]
    public async Task Rejects_links_outside_the_expected_list_or_film_route_without_fetching_them(bool pagination, string link)
    {
        RecordHtml(ListPath, pagination
            ? ListPage(ListPath, 2, Poster(0, MoviePath), link)
            : ListPage(ListPath, 1, Poster(0, link)));

        await AssertFailedWithoutChangesAsync();
    }

    public void Dispose() => _http.Dispose();

    private Task SyncListAsync() =>
        _sync.RunAsync([ListUrl], unsupported => Assert.Fail($"Unexpected unsupported URL: {unsupported}"), CancellationToken.None);

    private async Task AssertFailedWithoutChangesAsync()
    {
        var collectionId = await _library.CreateAsync("Original name", $"AutoCollections:{ListUrl}");
        var originalMember = Guid.NewGuid();
        await _library.AddMembersAsync(collectionId, [originalMember]);

        var failure = await Assert.ThrowsAsync<SourceListReadException>(SyncListAsync);

        Assert.Contains(ListUrl, failure.Message);
        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal(collectionId, collection.Id);
        Assert.Equal("Original name", collection.Name);
        Assert.Equal([originalMember], collection.Members);
        Assert.Null(collection.PosterTitle);
    }

    private Guid AddMedia(SourceMediaType type, string provider, string id)
    {
        var key = Guid.NewGuid();
        _library.Media.Add(new(key, new(type, new Dictionary<string, string> { [provider] = id })));
        return key;
    }

    private void RecordHtml(string path, string html) =>
        _responses.Record(Origin + path, () => new(HttpStatusCode.OK) { Content = new StringContent(html, null, "text/html") });

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string ListPage(string path, int count, string posters, string? next = null) =>
        $$"""
        <!DOCTYPE html><html><head>
          <meta property="og:type" content="letterboxd:list">
          <meta property="og:url" content="{{Origin}}{{path}}">
          <meta name="description" content="A list of {{count}} films compiled on Letterboxd.">
        </head><body class="list-page">
          <main id="content">
            <section id="userpanel" data-component-class="ListSidebar" data-list-type="public"
              data-list-name="Weekend Films &amp; Friends"></section>
            <section class="col-main">
              {{(count == 0 ? "" : $"""<ul class="poster-list -p125 -grid">{posters}</ul>""")}}
              {{(next is null ? "" : $"""<div class="pagination"><div class="paginate-nextprev"><a class="next" href="{next}">Next</a></div></div>""")}}
            </section>
          </main>
          <footer id="page-footer"></footer>
        </body></html>
        """;

    private static string Poster(int index, string link) =>
        $$"""
        <li class="posteritem" data-object-id="filmListEntry:{{900 + index}}">
          <div class="react-component" data-component-class="LazyPoster"
            data-item-name="The Example" data-item-link="{{link}}" data-list-index="{{index}}"></div>
        </li>
        """;
}
