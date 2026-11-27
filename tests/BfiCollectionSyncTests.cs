using System.Net;
using Jellyfin.Plugin.MagicCollections.Sync;
using Xunit;

namespace MagicCollections.Tests;

public sealed class BfiCollectionSyncTests : IDisposable
{
    private const string ListUrl = "https://www.bfi.org.uk/lists/films-for-a-rainy-day";
    private const string Title = "The Director's Cut";
    private readonly ControlledLibrary _library = new();
    private readonly RecordedHttpResponses _http = new();
    private readonly HttpClient _client;

    public BfiCollectionSyncTests() => _client = new(_http);

    [Fact]
    public async Task Syncs_only_the_selected_movie_using_its_title_and_year_without_provider_ids()
    {
        RecordArticle();
        var movie = AddMedia("The Director’s Cut", 1997);
        AddMedia(Title, 1956); // Earlier version.
        AddMedia(Title, null);
        AddMedia(Title, 1997, SourceMediaType.Series);
        AddMedia("Storm Warning", 2025);
        AddMedia("Cloud Study", 2010);
        AddMedia("Lobby Photograph", 2016);
        AddMedia("Film Festival", 2024);
        AddMedia("Related Film", 1998);

        await SyncAsync(ListUrl + "/", ListUrl);

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("Films for a Rainy Day", collection.Name);
        Assert.Equal($"AutoCollections:{ListUrl}", collection.Tag);
        Assert.Equal([movie], collection.Members);
        Assert.Equal([ListUrl], _http.Requests);
    }

    [Fact]
    public async Task Accepts_a_cast_heading_with_a_confirming_caption_and_uses_the_editorial_heading_without_json_ld()
    {
        RecordArticle(Article()
            .Replace("The Director’s Cut (1997)</h2>", "Rae Rivera in The Director’s Cut (1997)</h2>", StringComparison.Ordinal)
            .Replace("application/ld+json", "text/plain", StringComparison.Ordinal));
        var movie = AddMedia(Title, 1997);

        await SyncAsync();

        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal("Films for Rainy Days", collection.Name);
        Assert.Equal([movie], collection.Members);
    }

    [Fact]
    public async Task A_readable_list_with_no_unique_title_and_year_match_is_a_success()
    {
        RecordArticle();
        AddMedia(Title, 1997);
        AddMedia("the director’s cut", 1997);
        AddMedia(Title, 1956);

        await SyncAsync();

        Assert.Empty(_library.ManagedCollections);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_changed_layout_or_http_error_reports_a_failed_read_and_leaves_the_collection_untouched(HttpStatusCode status)
    {
        _http.Record(ListUrl, () => new(status)
        {
            Content = new StringContent("<html><body><main><h1>Unexpected page</h1></main></body></html>", null, "text/html")
        });
        var collectionId = await _library.CreateAsync("Existing collection", $"AutoCollections:{ListUrl}");
        var member = AddMedia(Title, 1997);
        await _library.AddMembersAsync(collectionId, [member]);

        var exception = await Assert.ThrowsAsync<SourceListReadException>(() => SyncAsync(ListUrl + "/"));

        Assert.Contains(ListUrl + "/", exception.Message, StringComparison.Ordinal);
        var collection = Assert.Single(_library.ManagedCollections);
        Assert.Equal(collectionId, collection.Id);
        Assert.Equal([member], collection.Members);
        Assert.Null(collection.PosterTitle);
    }

    [Theory]
    [InlineData("https://www.bfi.org.uk/lists")]
    [InlineData("https://www.bfi.org.uk/lists/")]
    [InlineData("https://www.bfi.org.uk/articles/type/lists")]
    [InlineData("https://www.bfi.org.uk/news/not-a-list")]
    [InlineData("http://www.bfi.org.uk/lists/films-for-a-rainy-day")]
    [InlineData("https://www.bfi.org.uk.example/lists/films-for-a-rainy-day")]
    [InlineData("https://www.bfi.org.uk/lists/films-for-a-rainy-day?redirect=1")]
    public async Task Does_not_route_other_pages_to_the_bfi_handler(string url)
    {
        var unsupported = new List<string>();
        await new CollectionSync([new BfiHandler(_client)], _library).RunAsync([url], unsupported.Add, CancellationToken.None);

        Assert.Equal([url], unsupported);
        Assert.Empty(_http.Requests);
    }

    public void Dispose() => _client.Dispose();

    private Task SyncAsync(params string[] urls) =>
        new CollectionSync([new BfiHandler(_client)], _library).RunAsync(
            urls.Length == 0 ? [ListUrl] : urls, url => Assert.Fail($"Unexpected unsupported URL: {url}"), CancellationToken.None);

    private void RecordArticle(string? html = null) =>
        _http.Record(ListUrl, () => new(HttpStatusCode.OK)
        {
            Content = new StringContent(html ?? Article(), null, "text/html")
        });

    private static string Article() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bfi-list.html"));

    private Guid AddMedia(string title, int? year, SourceMediaType type = SourceMediaType.Movie)
    {
        var key = Guid.NewGuid();
        _library.Media.Add(new(key, new(type, new Dictionary<string, string>()), title, year));
        return key;
    }
}
