using Jellyfin.Plugin.MagicCollections.Sync;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MagicCollections.Tests;

public sealed class ServiceRegistratorTests
{
    [Fact]
    public void Registers_a_Popular_Movies_handler_without_replacing_the_MDBList_handler()
    {
        var popularFeed = new Uri("https://popular-movies-data.stevenlu.com/movies.json");
        using var provider = PluginTestServices.BuildProvider();

        var handlers = provider.GetServices<ISourceListHandler>().ToArray();

        var popular = Assert.Single(handlers, h => h.Supports(popularFeed));
        Assert.Same(popular, provider.GetServices<ISourceListHandler>().Single(h => h.Supports(popularFeed)));
        Assert.Single(handlers, h => h.Supports(new("https://mdblist.com/lists/garycrawfordgc/top-movies")));
    }

    [Fact]
    public void Registers_a_Bfi_handler()
    {
        var bfiList = new Uri("https://www.bfi.org.uk/lists/10-great-documentaries-that-play-like-thrillers");
        using var provider = PluginTestServices.BuildProvider();

        var handler = Assert.Single(provider.GetServices<ISourceListHandler>(), h => h.Supports(bfiList));

        Assert.IsType<BfiHandler>(handler);
    }
}
