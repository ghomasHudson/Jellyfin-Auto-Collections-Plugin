using Jellyfin.Plugin.MagicCollections;
using Jellyfin.Plugin.MagicCollections.Configuration;
using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MagicCollections.Tests;

public sealed class RegeneratePostersTaskTests
{
    private const string SourceUrl = "https://lists.example/festival-picks";
    private static readonly Uri Icon = new("https://lists.example/icon.png");
    private static readonly Uri Artwork = new("https://lists.example/festival.jpg");

    [Fact]
    public async Task Manually_replaces_the_managed_poster_without_syncing_the_collection()
    {
        var entry = new MediaIdentity(SourceMediaType.Movie, new Dictionary<string, string> { ["Tmdb"] = "104" });
        var library = new ControlledLibrary();
        library.Media.Add(new(Guid.NewGuid(), entry));
        var managed = await AddCollection(library, "My festival", "Magic Collection", SourceUrl);
        var unmanaged = await AddCollection(library, "Another collection", SourceUrl);
        var markerOnly = await AddCollection(library, "No source list", "Magic Collection");
        var member = Guid.NewGuid();
        managed.Members.Add(member);
        managed.Overview = "My festival description";
        var source = new SourceList("Upstream name", [entry], "Upstream description")
        {
            FaviconUrls = [Icon],
            ImageUrl = Artwork
        };
        IScheduledTask task = CreateTask(library, new PluginConfiguration(), source);

        Assert.Empty(task.GetDefaultTriggers());
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(3, library.ManagedCollections.Count);
        Assert.Equal("My festival", managed.PosterTitle);
        Assert.Equal([Icon], managed.PosterFaviconUrls);
        Assert.Equal(Artwork, managed.PosterListImageUrl);
        Assert.Equal([member], managed.Members);
        Assert.Equal("My festival description", managed.Overview);
        Assert.Equal("Original poster", unmanaged.PosterTitle);
        Assert.Equal("Original poster", markerOnly.PosterTitle);
    }

    [Fact]
    public async Task With_only_list_name_enabled_refreshes_the_poster_without_reading_the_source()
    {
        var library = new ControlledLibrary();
        var managed = await AddCollection(library, "My festival", "Magic Collection", SourceUrl);
        var config = new PluginConfiguration { PosterBackgroundImage = false, PosterIcon = false };
        var handler = new TestHandler(_ => throw new InvalidOperationException("Source must not be read."));
        var task = new RegeneratePostersTask([handler], library, () => config, NullLogger<RegeneratePostersTask>.Instance);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal("My festival", managed.PosterTitle);
        Assert.Empty(managed.PosterFaviconUrls);
        Assert.Null(managed.PosterListImageUrl);
    }

    [Fact]
    public async Task Leaves_posters_untouched_when_all_poster_elements_are_disabled()
    {
        var library = new ControlledLibrary();
        var managed = await AddCollection(library, "My festival", "Magic Collection", SourceUrl);
        var config = new PluginConfiguration { PosterBackgroundImage = false, PosterIcon = false, PosterListName = false };
        var handler = new TestHandler(_ => throw new InvalidOperationException("Source must not be read."));
        var task = new RegeneratePostersTask([handler], library, () => config, NullLogger<RegeneratePostersTask>.Instance);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal("Original poster", managed.PosterTitle);
    }

    [Fact]
    public async Task Keeps_the_poster_when_its_source_list_fails_but_continues_with_other_collections()
    {
        const string unavailableUrl = "https://lists.example/unavailable";
        var library = new ControlledLibrary();
        var unavailable = await AddCollection(library, "Unavailable list", "Magic Collection", unavailableUrl);
        var managed = await AddCollection(library, "My festival", "Magic Collection", SourceUrl);
        var handler = new TestHandler(url => url.AbsoluteUri == unavailableUrl
            ? throw new SourceListReadException(url, "Unavailable list")
            : new("Festival picks", []));
        var task = new RegeneratePostersTask([handler], library, () => new PluginConfiguration(),
            NullLogger<RegeneratePostersTask>.Instance);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal("Original poster", unavailable.PosterTitle);
        Assert.Equal("My festival", managed.PosterTitle);
    }

    private static RegeneratePostersTask CreateTask(ControlledLibrary library, PluginConfiguration config, SourceList source) =>
        new([new TestHandler(_ => source)], library, () => config, NullLogger<RegeneratePostersTask>.Instance);

    private static async Task<ControlledLibrary.ManagedCollection> AddCollection(ControlledLibrary library, string name, params string[] tags)
    {
        var id = await library.CreateAsync(name, tags);
        var collection = library.ManagedCollections.Single(c => c.Id == id);
        collection.PosterTitle = "Original poster";
        return collection;
    }

    private sealed class TestHandler(Func<Uri, SourceList> read) : ISourceListHandler
    {
        public string SourceTag => "Festival Lists";
        public bool Supports(Uri url) => url.Host == "lists.example";
        public Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult(read(url));
    }
}
