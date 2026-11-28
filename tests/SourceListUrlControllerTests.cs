using Jellyfin.Plugin.MagicCollections.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MagicCollections.Tests;

public sealed class SourceListUrlControllerTests
{
    [Theory]
    [InlineData("https://mdblist.com/lists/snoak/latest-movies-digital-release", true)]
    [InlineData("https://letterboxd.com/example/list/movies/", true)]
    [InlineData("https://www.imdb.com/list/ls055592025/", false)]
    [InlineData("https://www.imdb.com/chart/top/", false)]
    [InlineData("https://www.imdb.com/chart/toptv/", false)]
    [InlineData("https://www.bfi.org.uk/lists/10-great-documentaries-that-play-like-thrillers", true)]
    [InlineData("https://popular-movies-data.stevenlu.com/movies.json", true)]
    [InlineData("https://trakt.tv/users/Justin/lists/OPEN-YOUR-EYES/?access_token=fixture-token#items", true)]
    [InlineData("https://app.trakt.tv/discover/trending?mode=show", true)]
    [InlineData("https://app.trakt.tv/discover/trending?mode=show&genres=comedy", false)]
    [InlineData("https://app.trakt.tv/discover/recommended?mode=movie&ratings=78-100&runtimes=80-500", true)]
    [InlineData("https://app.trakt.tv/discover/recommended?mode=media&ratings=78-100&runtimes=80-500&genres=action", true)]
    [InlineData("https://app.trakt.tv/lists/smart/view/hidden-gems-6ae39104c63e248a", true)]
    [InlineData("https://app.trakt.tv/users/me/lists/saved-list", false)]
    [InlineData("https://app.trakt.tv/users/me/watchlist", true)]
    [InlineData("https://trakt.tv/users/me/watchlist/", true)]
    [InlineData("https://app.trakt.tv/users/someone-else/watchlist", false)]
    [InlineData("https://app.trakt.tv/users/me/watchlist/movies", false)]
    [InlineData("https://letterboxd.com/film/the-matrix/", false)]
    [InlineData("https://example.com/list", false)]
    [InlineData("not a URL", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Validate_reports_whether_a_registered_handler_supports_the_URL(string? url, bool supported)
    {
        using var provider = PluginTestServices.BuildProvider();
        var controller = ActivatorUtilities.CreateInstance<SourceListUrlController>(provider);

        Assert.Equal(supported, controller.Validate(new(url)));
    }

    [Theory]
    [InlineData("https://media.example.com/Radarr/tag_name", true)]
    [InlineData("https://MEDIA.EXAMPLE.COM:443/Radarr/family%20night", true)]
    [InlineData("https://media.example.com/radarr/tag_name", false)]
    [InlineData("http://media.example.com/Radarr/tag_name", false)]
    [InlineData("https://media.example.com:9443/Radarr/tag_name", false)]
    [InlineData("https://media.example.com.evil.invalid/Radarr/tag_name", false)]
    [InlineData("https://media.example.com/Sonarr/tag_name", false)]
    [InlineData("https://media.example.com/Radarr", false)]
    [InlineData("https://media.example.com/Radarr/tag_name/", false)]
    [InlineData("https://media.example.com/Radarr/extra/tag_name", false)]
    [InlineData("https://media.example.com/Radarr/extra/../tag_name", false)]
    [InlineData("https://media.example.com/Radarr/extra/%2e%2e/tag_name", false)]
    [InlineData("https://media.example.com/Radarr//tag_name", false)]
    [InlineData("https://media.example.com/Radarr/%20", false)]
    [InlineData("https://media.example.com/Radarr/%0d", false)]
    [InlineData("https://user:key@media.example.com/Radarr/tag_name", false)]
    [InlineData("https://@media.example.com/Radarr/tag_name", false)]
    [InlineData("https://media.example.com/Radarr/tag_name?apikey=key", false)]
    [InlineData("https://media.example.com/Radarr/tag_name#", false)]
    public void Radarr_source_must_identify_one_tag_at_a_configured_scheme_host_port_and_base_path(string url, bool supported)
    {
        using var provider = PluginTestServices.BuildProvider(new()
        {
            RadarrServers = [new() { BaseUrl = "https://media.example.com/Radarr/", ApiKey = "example-key" }],
            SonarrServers = [new() { BaseUrl = "https://media.example.com/Sonarr/", ApiKey = "other-key" }]
        });
        var controller = ActivatorUtilities.CreateInstance<SourceListUrlController>(provider);

        Assert.Equal(supported, controller.Validate(new(url)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Radarr_source_rejects_duplicate_normalized_bases_even_across_services(bool acrossServices)
    {
        var first = new ArrServerConfiguration { BaseUrl = "https://media.example.com/Radarr", ApiKey = "first-key" };
        var duplicate = new ArrServerConfiguration { BaseUrl = "https://MEDIA.example.com:443/Radarr/", ApiKey = "second-key" };
        using var provider = PluginTestServices.BuildProvider(new()
        {
            RadarrServers = acrossServices ? [first] : [first, duplicate],
            SonarrServers = acrossServices ? [duplicate] : []
        });
        var controller = ActivatorUtilities.CreateInstance<SourceListUrlController>(provider);

        Assert.False(controller.Validate(new("https://media.example.com/Radarr/tag_name")));
    }

    [Theory]
    [InlineData("https://admin:password@media.example.com/Radarr", "example-key")]
    [InlineData("https://media.example.com/Radarr?apikey=example-key", "example-key")]
    [InlineData("https://media.example.com/Radarr#example-key", "example-key")]
    [InlineData("https://media.example.com/Radarr", "")]
    [InlineData("https://media.example.com/Radarr", "invalid\r\nkey")]
    public void A_base_or_key_that_cannot_be_used_safely_does_not_enable_a_saved_source(string baseUrl, string key)
    {
        using var provider = PluginTestServices.BuildProvider(new()
        {
            RadarrServers = [new() { BaseUrl = baseUrl, ApiKey = key }]
        });
        var controller = ActivatorUtilities.CreateInstance<SourceListUrlController>(provider);

        Assert.False(controller.Validate(new("https://media.example.com/Radarr/tag_name")));
    }

    [Fact]
    public void Draft_URL_checks_only_need_bases_and_do_not_fall_back_to_removed_saved_pairs()
    {
        const string old = "https://old-radarr.example.com";
        const string draft = "https://new-radarr.example.com/Radarr";
        using var provider = PluginTestServices.BuildProvider(new()
        {
            RadarrServers = [new() { BaseUrl = old, ApiKey = "saved-key" }]
        });
        var controller = ActivatorUtilities.CreateInstance<SourceListUrlController>(provider);

        Assert.True(controller.Validate(new(old + "/tag_name")));
        Assert.False(controller.Validate(new(old + "/tag_name", [draft], [])));
        Assert.True(controller.Validate(new(draft + "/tag_name", [draft], [])));
        Assert.False(controller.Validate(new(draft + "/extra/tag_name", [draft], [])));
        Assert.False(controller.Validate(new(draft + "/tag_name", [draft], [draft + "/"])));
        Assert.True(controller.Validate(new("https://letterboxd.com/example/list/movies/", [draft], [])));
    }
}
