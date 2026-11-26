using System.Net;
using Jellyfin.Plugin.MagicCollections;
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
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"magic-collections-tests-{Guid.NewGuid():N}");
    private readonly LetterboxdHandler _handler;
    private readonly HttpClient _http;
    private readonly CollectionSync _sync;

    public LetterboxdCollectionSyncTests()
    {
        _http = new HttpClient(_responses);
        _handler = new LetterboxdHandler(_http, _dataPath);
        _sync = new CollectionSync([_handler], _library);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refreshes_the_existing_overview_from_full_Letterboxd_list_notes_without_new_members(bool collapsed)
    {
        await _library.CreateAsync("My weekend films", $"AutoCollections:{ListUrl}");
        var collection = Assert.Single(_library.ManagedCollections);
        collection.Overview = "Earlier notes";
        const string fullText = """
            <p>Picks for <em>you</em> &amp; me.</p>
            <p>Updated every Friday.</p>
            """;
        var notes = $"""<div class="body-text -prose" data-full-text-url="#list-notes">{(collapsed ? "<p>Picks for you…</p>" : fullText)}</div>""";
        if (collapsed)
        {
            notes += $"""<div id="list-notes" class="body-text -prose" hidden>{fullText}</div>""";
        }
        RecordHtml(ListPath, ListPage(ListPath, 0, "", notes: notes));

        await SyncListAsync();

        Assert.Equal("Picks for you & me.\n\nUpdated every Friday.", collection.Overview);
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
    public async Task A_missing_trailing_slash_reuses_the_existing_Letterboxd_collection_and_paginates_normally()
    {
        const string pageTwo = ListPath + "page/2/";
        const string seriesPath = "/film/the-example-series/";
        RecordHtml(ListPath, ListPage(ListPath, 2, Poster(0, MoviePath), pageTwo));
        RecordHtml(pageTwo, ListPage(pageTwo, 2, Poster(1, seriesPath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html"));
        RecordHtml(seriesPath, Fixture("letterboxd-series.html"));
        var existing = await _library.CreateAsync("My collection", $"AutoCollections:{ListUrl}");
        var movie = AddMedia(SourceMediaType.Movie, "Tmdb", "496243");
        var series = AddMedia(SourceMediaType.Series, "Tmdb", "87108");
        await _library.AddMembersAsync(existing, [movie]);

        await _sync.RunAsync([ListUrl.TrimEnd('/')], ReportUnexpected, CancellationToken.None);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal(existing, collection.Id);
        Assert.Equal("My collection", collection.Name);
        Assert.Equal(new[] { movie, series }.Order(), collection.Members.Order());
        Assert.Equal([ListUrl, Origin + pageTwo, Origin + MoviePath, Origin + seriesPath], _responses.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Configuring_both_Letterboxd_URL_forms_syncs_one_list_once(bool withSlashFirst)
    {
        RecordHtml(ListPath, ListPage(ListPath, 1, Poster(0, MoviePath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html"));
        var movie = AddMedia(SourceMediaType.Movie, "Tmdb", "496243");
        var withoutSlash = ListUrl.TrimEnd('/');
        string[] urls = withSlashFirst ? [ListUrl, withoutSlash] : [withoutSlash, ListUrl];

        await _sync.RunAsync(urls, ReportUnexpected, CancellationToken.None);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal($"AutoCollections:{ListUrl}", collection.Tag);
        Assert.Equal([movie], collection.Members);
        Assert.Equal([ListUrl, Origin + MoviePath], _responses.Requests);
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
    public async Task Resolves_a_Letterboxd_film_alias_on_a_later_page_without_matching_its_local_ID()
    {
        const string pageTwo = ListPath + "page/2/";
        const string filmAlias = "/film/film:42611/";
        RecordHtml(ListPath, ListPage(ListPath, 2, Poster(0, MoviePath), pageTwo));
        RecordHtml(pageTwo, ListPage(pageTwo, 2, Poster(1, filmAlias)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html"));
        RecordHtml(filmAlias, Fixture("letterboxd-movie.html")
            .Replace(MoviePath, filmAlias)
            .Replace("496243", "15083")
            .Replace("tt6751668", "tt0426155"));
        var movie = AddMedia(SourceMediaType.Movie, "Tmdb", "496243");
        var aliasedMovie = AddMedia(SourceMediaType.Movie, "Imdb", "tt0426155");
        AddMedia(SourceMediaType.Movie, "Tmdb", "42611");

        await SyncListAsync();

        Assert.Equal(new[] { movie, aliasedMovie }.Order(), Assert.Single(_library.ManagedCollections).Members.Order());
        Assert.Equal([ListUrl, Origin + pageTwo, Origin + MoviePath, Origin + filmAlias], _responses.Requests);
    }

    [Fact]
    public async Task Accepts_a_confirmed_public_empty_list()
    {
        RecordHtml(ListPath, ListPage(ListPath, 0, ""));
        AddMedia(SourceMediaType.Movie, "Tmdb", "496243");

        await SyncListAsync();

        Assert.Empty(_library.ManagedCollections);
    }

    [Fact]
    public async Task Syncs_distinct_list_entries_that_link_to_the_same_film()
    {
        const string pageTwo = ListPath + "page/2/";
        RecordHtml(ListPath, ListPage(ListPath, 2, Poster(0, MoviePath), pageTwo));
        RecordHtml(pageTwo, ListPage(pageTwo, 2, Poster(1, MoviePath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html"));
        var movie = AddMedia(SourceMediaType.Movie, "Tmdb", "496243");

        await SyncListAsync();

        Assert.Equal([movie], Assert.Single(_library.ManagedCollections).Members);
        Assert.Equal([ListUrl, Origin + pageTwo, Origin + MoviePath], _responses.Requests);
    }

    [Fact]
    public async Task Reuses_film_IDs_after_a_restart_and_across_lists_but_fetches_new_entries()
    {
        const string otherList = "/alex_test/list/new_films/";
        const string seriesPath = "/film/the-example-series/";
        RecordHtml(ListPath, ListPage(ListPath, 1, Poster(0, MoviePath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html"));
        var movie = AddMedia(SourceMediaType.Movie, "Imdb", "tt6751668");
        await SyncListAsync();

        var cachedFilm = Assert.Single(Directory.GetFiles(_dataPath, "*.json", SearchOption.AllDirectories));
        File.SetLastWriteTimeUtc(cachedFilm, DateTime.UnixEpoch);
        RecordHtml(ListPath, ListPage(ListPath, 2, Poster(0, MoviePath) + Poster(1, seriesPath)));
        RecordHtml(otherList, ListPage(otherList, 1, Poster(0, MoviePath)));
        RecordHtml(seriesPath, Fixture("letterboxd-series.html"));
        var series = AddMedia(SourceMediaType.Series, "Tmdb", "87108");

        var restartedSync = new CollectionSync([new LetterboxdHandler(_http, _dataPath)], _library);
        await restartedSync.RunAsync([ListUrl, Origin + otherList], ReportUnexpected, CancellationToken.None);

        Assert.Equal([ListUrl, Origin + MoviePath, ListUrl, Origin + seriesPath, Origin + otherList], _responses.Requests);
        Assert.Equal([movie, series], _library.ManagedCollections.Single(c => c.Tag == $"AutoCollections:{ListUrl}").Members);
        Assert.Equal([movie], _library.ManagedCollections.Single(c => c.Tag == $"AutoCollections:{Origin + otherList}").Members);
    }

    [Fact]
    public void The_cache_clear_task_has_no_default_triggers()
    {
        Assert.Empty(new ClearCacheTask(_handler).GetDefaultTriggers());
    }

    [Fact]
    public async Task The_cache_clear_task_leaves_the_collection_untouched_and_fetches_fresh_film_IDs_on_the_next_sync()
    {
        var task = new ClearCacheTask(_handler);
        var progress = new Progress<double>();
        await task.ExecuteAsync(progress, CancellationToken.None);
        RecordHtml(ListPath, ListPage(ListPath, 1, Poster(0, MoviePath)));
        var moviePage = Fixture("letterboxd-movie.html");
        RecordHtml(MoviePath, moviePage);
        var movie = AddMedia(SourceMediaType.Movie, "Tmdb", "496243");
        var correctedMatch = AddMedia(SourceMediaType.Movie, "Tmdb", "12345");
        await SyncListAsync();

        var cachedFilm = Assert.Single(Directory.GetFiles(_dataPath, "*.json", SearchOption.AllDirectories));
        // A different source's cache must survive the Letterboxd-only clear.
        var otherCache = Path.Combine(_dataPath, "magic-collections", "other-source.json");
        File.WriteAllText(otherCache, "{}");
        RecordHtml(MoviePath, moviePage.Replace("/movie/496243/", "/movie/12345/"));
        await task.ExecuteAsync(progress, CancellationToken.None);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal([movie], collection.Members);
        Assert.False(File.Exists(cachedFilm));
        Assert.True(File.Exists(otherCache));
        Assert.Equal([ListUrl, Origin + MoviePath], _responses.Requests);

        await SyncListAsync();

        Assert.Equal([movie, correctedMatch], collection.Members);
        Assert.Equal([ListUrl, Origin + MoviePath, ListUrl, Origin + MoviePath], _responses.Requests);
    }

    [Fact]
    public async Task Refetches_a_corrupt_cached_film()
    {
        RecordHtml(ListPath, ListPage(ListPath, 1, Poster(0, MoviePath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html"));
        await SyncListAsync();
        var cachedFilm = Assert.Single(Directory.GetFiles(_dataPath, "*.json", SearchOption.AllDirectories));
        File.WriteAllText(cachedFilm, "{");

        var movie = AddMedia(SourceMediaType.Movie, "Tmdb", "496243");
        await SyncListAsync();

        Assert.Equal([movie], Assert.Single(_library.ManagedCollections).Members);
        Assert.Equal([ListUrl, Origin + MoviePath, ListUrl, Origin + MoviePath], _responses.Requests);
    }

    [Fact]
    public async Task Retries_a_film_without_verified_IDs_on_the_next_sync()
    {
        RecordHtml(ListPath, ListPage(ListPath, 1, Poster(0, MoviePath)));
        var moviePage = Fixture("letterboxd-movie.html");
        RecordHtml(MoviePath, moviePage.Replace(MovieProviderLink, ""));
        var movie = AddMedia(SourceMediaType.Movie, "Tmdb", "496243");
        await SyncListAsync();
        Assert.Empty(_library.ManagedCollections);

        RecordHtml(MoviePath, moviePage);
        await SyncListAsync();

        Assert.Equal([movie], Assert.Single(_library.ManagedCollections).Members);
        Assert.Equal([ListUrl, Origin + MoviePath, ListUrl, Origin + MoviePath], _responses.Requests);
    }

    [Theory]
    [InlineData("private list")]
    [InlineData("challenge")]
    [InlineData("wrong page")]
    [InlineData("missing next")]
    [InlineData("changed count")]
    [InlineData("missing poster")]
    [InlineData("shifted index")]
    [InlineData("repeated entry")]
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
            "repeated entry" => (firstPage, ListPage(pageTwo, 2, Poster(1, MoviePath, entryId: 900))),
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
    public async Task Ignores_the_Imdb_link_when_only_the_body_claims_a_media_type()
    {
        RecordHtml(ListPath, ListPage(ListPath, 1, Poster(0, MoviePath)));
        RecordHtml(MoviePath, Fixture("letterboxd-movie.html")
            .Replace(MovieProviderLink, ""));
        AddMedia(SourceMediaType.Movie, "Imdb", "tt6751668");
        AddMedia(SourceMediaType.Movie, "Tmdb", "496243");

        await SyncListAsync();

        Assert.Empty(_library.ManagedCollections);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("redirect")]
    [InlineData("partial HTTP")]
    [InlineData("challenge")]
    [InlineData("wrong film")]
    [InlineData("truncated HTML")]
    [InlineData("connection failed")]
    public async Task Leaves_the_managed_collection_untouched_when_a_detail_page_fails_after_a_valid_source_list_entry(string problem)
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
        var movie = AddMedia(SourceMediaType.Movie, "Tmdb", "496243");

        await AssertFailedWithoutChangesAsync();
        RecordHtml(failingFilmPath, failingPage);
        await SyncListAsync();

        Assert.Contains(movie, Assert.Single(_library.ManagedCollections).Members);
        Assert.Equal([ListUrl, Origin + MoviePath, Origin + failingFilmPath, ListUrl, Origin + failingFilmPath], _responses.Requests);
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
            ListUrl + "/",
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

    [Fact]
    public async Task Matches_by_provider_ID_when_the_film_page_provider_links_move_out_of_the_footer()
    {
        const string seriesPath = "/film/the-example-series/";
        RecordHtml(ListPath, ListPage(ListPath, 1, Poster(0, seriesPath)));
        RecordHtml(seriesPath, Fixture("letterboxd-series.html").Replace("text-link text-footer", "production-provider-links"));
        var series = AddMedia(SourceMediaType.Series, "Imdb", "tt7366338");

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
    [InlineData(false, "/film/list:42611/")]
    [InlineData(false, "/film/film:42611/extra/")]
    public async Task Rejects_links_outside_the_expected_list_or_film_route_without_fetching_them(bool pagination, string link)
    {
        RecordHtml(ListPath, pagination
            ? ListPage(ListPath, 2, Poster(0, MoviePath), link)
            : ListPage(ListPath, 1, Poster(0, link)));

        await AssertFailedWithoutChangesAsync();
    }

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private Task SyncListAsync() =>
        _sync.RunAsync([ListUrl], ReportUnexpected, CancellationToken.None);

    private static void ReportUnexpected(string url) => Assert.Fail($"Unexpected unsupported URL: {url}");

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

    private static string ListPage(string path, int count, string posters, string? next = null, string? notes = null) =>
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
              {{(notes is null ? "" : $"""<div class="list-title-intro">{notes}</div>""")}}
              {{(count == 0 ? "" : $"""<ul class="poster-list -p125 -grid">{posters}</ul>""")}}
              {{(next is null ? "" : $"""<div class="pagination"><div class="paginate-nextprev"><a class="next" href="{next}">Next</a></div></div>""")}}
            </section>
          </main>
          <footer id="page-footer"></footer>
        </body></html>
        """;

    private static string Poster(int index, string link, int? entryId = null) =>
        $$"""
        <li class="posteritem" data-object-id="filmListEntry:{{entryId ?? (900 + index)}}">
          <div class="react-component" data-component-class="LazyPoster"
            data-item-name="The Example" data-item-link="{{link}}" data-list-index="{{index}}"></div>
        </li>
        """;
}
