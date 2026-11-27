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
}
