using System.Net;
using Jellyfin.Plugin.MagicCollections.Configuration;
using Jellyfin.Plugin.MagicCollections.Sync;
using Xunit;

namespace MagicCollections.Tests;

public sealed class RadarrCollectionSyncTests
{
    private const string BaseUrl = "https://radarr.example.com:9443/Media/Radarr";
    private const string SourceUrl = BaseUrl + "/movie-night";
    private const string Key = "example-radarr-api-secret";
    private const string Tags = """[{"id":12,"label":"movie-night"},{"id":3,"label":"other"}]""";
    private const string Movies = """
        [
          {"id":901,"tmdbId":101,"imdbId":"tt1020304","tags":[3,12]},
          {"id":902,"tmdbId":0,"imdbId":"tt5060708","tags":[12]},
          {"id":903,"tmdbId":103,"tags":[3]},
          {"id":904,"tmdbId":0,"imdbId":null,"tags":[12]}
        ]
        """;

    [Fact]
    public async Task One_tag_on_a_configured_base_path_syncs_movies_by_provider_ID_in_two_reads()
    {
        using var fixture = new RadarrFixture();
        fixture.Configuration.RadarrServers =
        [
            new() { BaseUrl = "https://radarr.example.com:9443", ApiKey = "other-radarr-key" },
            .. fixture.Configuration.RadarrServers
        ];
        fixture.Configuration.SonarrServers =
            [new() { BaseUrl = "https://radarr.example.com:9443/Media/Sonarr", ApiKey = "sonarr-key" }];
        var tmdbMatch = fixture.AddMedia(SourceMediaType.Movie, "Tmdb", "101");
        var imdbMatch = fixture.AddMedia(SourceMediaType.Movie, "Imdb", "tt5060708");
        fixture.AddMedia(SourceMediaType.Movie, "Tmdb", "103");
        fixture.AddMedia(SourceMediaType.Movie, "Tmdb", "901");
        fixture.AddMedia(SourceMediaType.Series, "Imdb", "tt1020304");

        await fixture.Sync("https://RADARR.EXAMPLE.COM:9443/Media/Radarr/%6dovie-night", SourceUrl);

        var collection = Assert.Single(fixture.Library.ManagedCollections);
        Assert.Equal("movie-night", collection.Name);
        Assert.Equal(["Magic Collection", SourceUrl, "Radarr"], collection.Tags);
        Assert.Equal([tmdbMatch, imdbMatch], collection.Members);
        Assert.Equal([(BaseUrl + "/api/v3/tag", Key), (BaseUrl + "/api/v3/movie", Key)],
            fixture.Responses.Requests);
    }

    [Fact]
    public async Task A_missing_tag_fails_but_an_existing_tag_with_no_movies_can_clear_the_collection()
    {
        using var fixture = new RadarrFixture();
        var original = fixture.AddMedia(SourceMediaType.Movie, "Tmdb", "101");
        await fixture.Sync(SourceUrl);
        var collection = Assert.Single(fixture.Library.ManagedCollections);
        fixture.Responses.Requests.Clear();

        fixture.Responses.TagResponse = () => RadarrResponses.Json("[]");
        var error = await Assert.ThrowsAsync<SourceListReadException>(() => fixture.Sync(SourceUrl));
        Assert.Contains("Tag does not exist", error.Message);
        Assert.Equal([original], collection.Members);
        Assert.Equal([(BaseUrl + "/api/v3/tag", Key)], fixture.Responses.Requests);

        fixture.Responses.TagResponse = () => RadarrResponses.Json(Tags);
        fixture.Responses.MovieResponse = () => RadarrResponses.Json("[]");
        await fixture.Sync(SourceUrl);

        Assert.Equal(collection.Id, Assert.Single(fixture.Library.ManagedCollections).Id);
        Assert.Empty(collection.Members);
    }

    [Theory]
    [InlineData("""[{"id":12,"label":"movie-night"},{"id":"12","label":"other"}]""")]
    [InlineData("""[{"id":12,"label":"movie-night"},{"id":13,"label":"movie-night"}]""")]
    [InlineData("""[{"id":12,"label":"movie-night"},{"id":13,"label":"\uD800"}]""")]
    public async Task A_malformed_tag_list_cannot_establish_membership(string response)
    {
        using var fixture = new RadarrFixture();
        fixture.Responses.TagResponse = () => RadarrResponses.Json(response);

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => fixture.Sync(SourceUrl));

        Assert.Contains("Invalid or incomplete", error.Message);
        Assert.Equal([(BaseUrl + "/api/v3/tag", Key)], fixture.Responses.Requests);
        Assert.Empty(fixture.Library.ManagedCollections);
    }

    [Theory]
    [InlineData("""{"response":"example-radarr-api-secret"}""")]
    [InlineData("""[{"id":901,"tmdbId":101}]""")]
    [InlineData("""[{"id":901,"tmdbId":101,"tags":[12,"example-radarr-api-secret"]}]""")]
    [InlineData("""[{"id":901,"tags":[12]}]""")]
    [InlineData("""[{"id":901,"tmdbId":"101","tags":[12]}]""")]
    [InlineData("""[{"id":901,"tmdbId":101,"imdbId":"example-radarr-api-secret","tags":[12]}]""")]
    [InlineData("""[{"id":901,"tmdbId":101,"tags":[12]},""")]
    public async Task Malformed_or_incomplete_movies_do_not_change_the_collection_or_echo_the_body(string response)
    {
        using var fixture = new RadarrFixture();
        var original = fixture.AddMedia(SourceMediaType.Movie, "Tmdb", "101");
        await fixture.Sync(SourceUrl);
        var collection = Assert.Single(fixture.Library.ManagedCollections);
        fixture.Responses.MovieResponse = () => RadarrResponses.Json(response);

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => fixture.Sync(SourceUrl));

        Assert.Contains("Invalid or incomplete", error.Message);
        Assert.DoesNotContain(Key, error.ToString());
        Assert.Equal([original], collection.Members);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.PartialContent)]
    public async Task A_non_successful_read_does_not_echo_the_body_or_redirect_location(HttpStatusCode status)
    {
        using var fixture = new RadarrFixture();
        fixture.Responses.TagResponse = () => new(status)
        {
            Content = new StringContent(Key),
            Headers = { Location = new("https://elsewhere.example/" + Key) }
        };

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => fixture.Sync(SourceUrl));

        Assert.Contains($"HTTP {(int)status}", error.Message);
        Assert.DoesNotContain(Key, error.ToString());
        Assert.Equal([(BaseUrl + "/api/v3/tag", Key)], fixture.Responses.Requests);
        Assert.Empty(fixture.Library.ManagedCollections);
    }

    [Fact]
    public async Task Transport_failures_do_not_expose_request_details()
    {
        using var fixture = new RadarrFixture();
        fixture.Responses.TagResponse = () => throw new HttpRequestException(Key);

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => fixture.Sync(SourceUrl));

        Assert.Contains("API request failed", error.Message);
        Assert.DoesNotContain(Key, error.ToString());
    }

    [Fact]
    public async Task An_unconfigured_instance_is_reported_without_contacting_it_or_a_configured_server()
    {
        using var fixture = new RadarrFixture();
        var unknown = "https://unconfigured.example.com/movie-night";
        var unsupported = new List<string>();

        await fixture.Engine.RunAsync([unknown], unsupported.Add, default);

        Assert.Equal([unknown], unsupported);
        Assert.Empty(fixture.Responses.Requests);
        Assert.Empty(fixture.Library.ManagedCollections);
    }

    private sealed class RadarrFixture : IDisposable
    {
        public readonly PluginConfiguration Configuration = new()
        {
            RadarrServers = [new() { BaseUrl = BaseUrl + "/", ApiKey = Key }]
        };
        public readonly ControlledLibrary Library = new();
        public readonly RadarrResponses Responses = new();
        private readonly HttpClient _http;

        public RadarrFixture() => _http = new(Responses);

        public CollectionSync Engine => new([new RadarrHandler(_http, () => Configuration)], Library);

        public Task Sync(params string[] urls) =>
            Engine.RunAsync(urls, url => Assert.Fail($"Unexpected unsupported URL: {url}"), default);

        public Guid AddMedia(SourceMediaType type, string provider, string id)
        {
            var item = Guid.NewGuid();
            Library.Media.Add(new(item, new(type, new Dictionary<string, string> { [provider] = id })));
            return item;
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class RadarrResponses : HttpMessageHandler
    {
        public readonly List<(string Url, string Key)> Requests = [];
        public Func<HttpResponseMessage> TagResponse { get; set; } = () => Json(Tags);
        public Func<HttpResponseMessage> MovieResponse { get; set; } = () => Json(Movies);

        public static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var endpoint = request.RequestUri!.AbsoluteUri;
            Requests.Add((endpoint, Assert.Single(request.Headers.GetValues("X-Api-Key"))));
            return Task.FromResult(endpoint switch
            {
                BaseUrl + "/api/v3/tag" => TagResponse(),
                BaseUrl + "/api/v3/movie" => MovieResponse(),
                _ => throw new InvalidOperationException("Unexpected Radarr request.")
            });
        }
    }
}
