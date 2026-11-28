using System.Net;
using Jellyfin.Plugin.MagicCollections.Configuration;
using Jellyfin.Plugin.MagicCollections.Sync;
using Xunit;

namespace MagicCollections.Tests;

public sealed class SonarrCollectionSyncTests
{
    private const string BaseUrl = "https://media.example.com:9443/sonarr";
    private const string SourceUrl = BaseUrl + "/family-shows";
    private const string Key = "example-sonarr-api-secret";
    private const string Tags = """[{"id":12,"label":"family-shows"},{"id":3,"label":"other"}]""";

    [Fact]
    public async Task A_tag_syncs_whole_series_using_Tvdb_and_optional_Tmdb_and_Imdb_IDs()
    {
        using var fixture = new SonarrFixture();
        fixture.Responses.RecordBody(BaseUrl, "series", """
            [
              {"id":901,"tvdbId":101,"tags":[3,12],"seasons":[{"seasonNumber":1}]},
              {"id":902,"tvdbId":102,"tmdbId":202,"tags":[12]},
              {"id":903,"tvdbId":103,"tags":[3]},
              {"id":904,"tvdbId":0,"tmdbId":0,"imdbId":null,"tags":[12]},
              {"id":905,"tvdbId":105,"tmdbId":0,"imdbId":"tt0123456","tags":[12]}
            ]
            """);
        var tvdbMatch = fixture.AddMedia("Tvdb", "101");
        var tmdbMatch = fixture.AddMedia("Tmdb", "202");
        var imdbMatch = fixture.AddMedia("Imdb", "tt0123456");
        fixture.AddMedia("Tvdb", "103"); // Different tag
        fixture.AddMedia("Tvdb", "901"); // Sonarr resource ID
        fixture.AddMedia("Tvdb", "0"); // Unavailable provider ID
        fixture.AddMedia("Imdb", "tt0123456", SourceMediaType.Movie);

        await fixture.Sync("https://MEDIA.example.com:9443/sonarr/%66amily-shows", SourceUrl);

        var collection = Assert.Single(fixture.Library.ManagedCollections);
        Assert.Equal("family-shows", collection.Name);
        Assert.Equal(["Magic Collection", SourceUrl, "Sonarr"], collection.Tags);
        Assert.Equal(new[] { tvdbMatch, tmdbMatch, imdbMatch }.Order(), collection.Members.Order());
        Assert.Equal([(BaseUrl + "/api/v3/tag", Key), (BaseUrl + "/api/v3/series", Key)], fixture.Responses.Requests);
    }

    [Fact]
    public async Task A_valid_tag_with_no_tagged_series_clears_existing_membership()
    {
        using var fixture = new SonarrFixture();
        var original = fixture.AddMedia("Tvdb", "101");
        await fixture.Sync(SourceUrl);
        var collection = Assert.Single(fixture.Library.ManagedCollections);
        Assert.Equal([original], collection.Members);
        fixture.Responses.RecordBody(BaseUrl, "series", """[{"id":901,"tvdbId":101,"tags":[3]}]""");

        await fixture.Sync(SourceUrl);

        var updated = Assert.Single(fixture.Library.ManagedCollections);
        Assert.Equal(collection.Id, updated.Id);
        Assert.Empty(updated.Members);
    }

    [Fact]
    public async Task A_missing_tag_leaves_existing_membership_unchanged_without_reading_series()
    {
        using var fixture = new SonarrFixture();
        var original = fixture.AddMedia("Tvdb", "101");
        await fixture.Sync(SourceUrl);
        var collection = Assert.Single(fixture.Library.ManagedCollections);
        fixture.Responses.Requests.Clear();
        fixture.Responses.RecordBody(BaseUrl, "tag", """[{"id":12,"label":"other"}]""");

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => fixture.Sync(SourceUrl));

        Assert.Contains("Tag does not exist", error.Message);
        Assert.Equal([original], collection.Members);
        Assert.Equal([(BaseUrl + "/api/v3/tag", Key)], fixture.Responses.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task A_failed_series_read_leaves_membership_unchanged_without_echoing_request_details(HttpStatusCode status)
    {
        using var fixture = new SonarrFixture();
        var original = fixture.AddMedia("Tvdb", "101");
        await fixture.Sync(SourceUrl);
        var collection = Assert.Single(fixture.Library.ManagedCollections);
        fixture.Responses.Requests.Clear();
        fixture.Responses.Record(BaseUrl, "series", () => new(status)
        {
            Content = new StringContent(Key),
            Headers = { Location = new("https://elsewhere.example/" + Key) }
        });

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => fixture.Sync(SourceUrl));

        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain(Key, error.ToString());
        Assert.Equal([original], collection.Members);
        Assert.Equal([(BaseUrl + "/api/v3/tag", Key), (BaseUrl + "/api/v3/series", Key)], fixture.Responses.Requests);
    }

    [Fact]
    public async Task An_incomplete_series_response_does_not_apply_partial_membership()
    {
        using var fixture = new SonarrFixture();
        var original = fixture.AddMedia("Tvdb", "101");
        fixture.AddMedia("Tvdb", "102");
        await fixture.Sync(SourceUrl);
        var collection = Assert.Single(fixture.Library.ManagedCollections);
        fixture.Responses.RecordBody(BaseUrl, "series", """
            [{"id":902,"tvdbId":102,"tags":[12]},{"id":901,"tvdbId":101}]
            """);

        var error = await Assert.ThrowsAsync<SourceListReadException>(() => fixture.Sync(SourceUrl));

        Assert.Contains("Invalid or incomplete", error.Message);
        Assert.Equal([original], collection.Members);
    }

    [Fact]
    public async Task The_same_tag_on_two_configured_servers_has_separate_membership_and_keys()
    {
        const string otherBase = "https://media.example.com:9443/sonarr-secondary";
        const string otherSource = otherBase + "/family-shows";
        const string otherKey = "secondary-sonarr-key";
        using var fixture = new SonarrFixture();
        fixture.Configuration.SonarrServers = [.. fixture.Configuration.SonarrServers,
            new() { BaseUrl = otherBase, ApiKey = otherKey }];
        fixture.Responses.RecordBody(otherBase, "tag", Tags);
        fixture.Responses.RecordBody(otherBase, "series", """[{"id":901,"tvdbId":102,"tags":[12]}]""");
        var firstMember = fixture.AddMedia("Tvdb", "101");
        var secondMember = fixture.AddMedia("Tvdb", "102");

        await fixture.Sync(SourceUrl, otherSource);

        Assert.Collection(fixture.Library.ManagedCollections, first =>
        {
            Assert.Contains(SourceUrl, first.Tags);
            Assert.Equal([firstMember], first.Members);
        }, second =>
        {
            Assert.Contains(otherSource, second.Tags);
            Assert.Equal([secondMember], second.Members);
        });
        Assert.Equal([
            (BaseUrl + "/api/v3/tag", Key), (BaseUrl + "/api/v3/series", Key),
            (otherBase + "/api/v3/tag", otherKey), (otherBase + "/api/v3/series", otherKey)
        ], fixture.Responses.Requests);
    }

    [Fact]
    public async Task An_unconfigured_Sonarr_base_is_reported_without_sending_a_request()
    {
        using var fixture = new SonarrFixture();
        const string unknown = "https://media.example.com:9443/unconfigured/family-shows";
        var unsupported = new List<string>();

        await fixture.Engine.RunAsync([unknown], unsupported.Add, default);

        Assert.Equal([unknown], unsupported);
        Assert.Empty(fixture.Responses.Requests);
        Assert.Empty(fixture.Library.ManagedCollections);
    }

    private sealed class SonarrFixture : IDisposable
    {
        public PluginConfiguration Configuration { get; } = new()
        {
            SonarrServers = [new() { BaseUrl = BaseUrl + "/", ApiKey = Key }]
        };
        public ControlledLibrary Library { get; } = new();
        public SonarrResponses Responses { get; } = new();
        private readonly HttpClient _http;

        public SonarrFixture()
        {
            _http = new(Responses);
            Responses.RecordBody(BaseUrl, "tag", Tags);
            Responses.RecordBody(BaseUrl, "series", """[{"id":901,"tvdbId":101,"tags":[12]}]""");
        }

        public CollectionSync Engine => new([new SonarrHandler(_http, () => Configuration)], Library);

        public Task Sync(params string[] urls) =>
            Engine.RunAsync(urls, url => Assert.Fail($"Unexpected unsupported URL: {url}"), default);

        public Guid AddMedia(string provider, string id, SourceMediaType type = SourceMediaType.Series)
        {
            var item = Guid.NewGuid();
            Library.Media.Add(new(item, new(type, new Dictionary<string, string> { [provider] = id })));
            return item;
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class SonarrResponses : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = [];
        public List<(string Url, string Key)> Requests { get; } = [];

        public void RecordBody(string baseUrl, string route, string body) =>
            Record(baseUrl, route, () => new(HttpStatusCode.OK) { Content = new StringContent(body) });

        public void Record(string baseUrl, string route, Func<HttpResponseMessage> respond) =>
            _responses[$"{baseUrl}/api/v3/{route}"] = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var endpoint = request.RequestUri!.AbsoluteUri;
            Requests.Add((endpoint, Assert.Single(request.Headers.GetValues("X-Api-Key"))));
            return Task.FromResult(_responses[endpoint]());
        }
    }
}
