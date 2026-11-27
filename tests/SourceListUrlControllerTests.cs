using Jellyfin.Plugin.MagicCollections.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MagicCollections.Tests;

public sealed class SourceListUrlControllerTests
{
    [Theory]
    [InlineData("https://mdblist.com/lists/snoak/latest-movies-digital-release", true)]
    [InlineData("https://letterboxd.com/example/list/movies/", true)]
    [InlineData("https://www.bfi.org.uk/lists/10-great-documentaries-that-play-like-thrillers", true)]
    [InlineData("https://popular-movies-data.stevenlu.com/movies.json", true)]
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
