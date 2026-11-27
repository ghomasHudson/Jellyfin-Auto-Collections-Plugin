using Jellyfin.Plugin.MagicCollections;
using Jellyfin.Plugin.MagicCollections.Configuration;
using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MagicCollections.Tests;

public sealed class ServiceRegistratorTests
{
    [Fact]
    public void Registers_a_Popular_Movies_handler_without_replacing_the_MDBList_handler()
    {
        var popularFeed = new Uri("https://popular-movies-data.stevenlu.com/movies.json");
        using var provider = BuildProvider();

        var handlers = provider.GetServices<ISourceListHandler>().ToArray();

        var popular = Assert.Single(handlers, h => h.Supports(popularFeed));
        Assert.Same(popular, provider.GetServices<ISourceListHandler>().Single(h => h.Supports(popularFeed)));
        Assert.Single(handlers, h => h.Supports(new("https://mdblist.com/lists/garycrawfordgc/top-movies")));
    }

    [Fact]
    public void Registers_a_Bfi_handler()
    {
        var bfiList = new Uri("https://www.bfi.org.uk/lists/10-great-documentaries-that-play-like-thrillers");
        using var provider = BuildProvider();

        var handler = Assert.Single(provider.GetServices<ISourceListHandler>(), h => h.Supports(bfiList));

        Assert.IsType<BfiHandler>(handler);
    }

    [Theory]
    [InlineData("https://mdblist.com/lists/snoak/latest-movies-digital-release", true)]
    [InlineData("https://letterboxd.com/example/list/movies/", true)]
    [InlineData("https://www.bfi.org.uk/lists/10-great-documentaries-that-play-like-thrillers", true)]
    [InlineData("https://popular-movies-data.stevenlu.com/movies.json", true)]
    [InlineData("https://letterboxd.com/film/the-matrix/", false)]
    [InlineData("https://example.com/list", false)]
    [InlineData("not a URL", false)]
    public void Source_list_validation_reports_whether_a_registered_handler_supports_the_URL(string url, bool supported)
    {
        using var provider = BuildProvider();
        var validation = ActivatorUtilities.CreateInstance<SourceListUrlController>(provider);

        Assert.Equal(supported, validation.Validate(new(url)));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var dataPath = Path.Combine(Path.GetTempPath(), $"magic-collections-tests-{Guid.NewGuid():N}");
        services.AddSingleton<IApplicationPaths>(new TestApplicationPaths(dataPath));
        new ServiceRegistrator().RegisterServices(services, null!);
        return services.BuildServiceProvider();
    }

    private sealed class TestApplicationPaths(string dataPath) : IApplicationPaths
    {
        public string DataPath => dataPath;
        public string ProgramDataPath => throw new NotSupportedException();
        public string WebPath => throw new NotSupportedException();
        public string ProgramSystemPath => throw new NotSupportedException();
        public string ImageCachePath => throw new NotSupportedException();
        public string PluginsPath => throw new NotSupportedException();
        public string PluginConfigurationsPath => throw new NotSupportedException();
        public string LogDirectoryPath => throw new NotSupportedException();
        public string ConfigurationDirectoryPath => throw new NotSupportedException();
        public string SystemConfigurationFilePath => throw new NotSupportedException();
        public string CachePath => throw new NotSupportedException();
        public string TempDirectory => throw new NotSupportedException();
        public string VirtualDataPath => throw new NotSupportedException();
        public string TrickplayPath => throw new NotSupportedException();
        public string BackupPath => throw new NotSupportedException();
        public void MakeSanityCheckOrThrow() => throw new NotSupportedException();
        public void CreateAndCheckMarker(string path, string markerName, bool recursive) => throw new NotSupportedException();
    }
}
