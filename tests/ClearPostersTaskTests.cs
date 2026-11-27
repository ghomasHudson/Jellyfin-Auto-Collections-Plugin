using System.Reflection;
using Jellyfin.Plugin.MagicCollections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace MagicCollections.Tests;

public sealed class ClearPostersTaskTests
{
    [Fact]
    public async Task Clears_only_primary_posters_on_collections_tagged_Magic_Collection()
    {
        var poster = new ItemImageInfo { Type = ImageType.Primary, Path = "https://example.com/custom.png" };
        var backdrop = new ItemImageInfo { Type = ImageType.Backdrop, Path = "https://example.com/backdrop.png" };
        var managed = new TestCollection { Tags = ["Magic Collection"], ImageInfos = [poster, backdrop] };
        var unmanaged = new TestCollection { Tags = ["Another tag"], ImageInfos = [poster] };
        var noPoster = new TestCollection { Tags = ["Magic Collection"], ImageInfos = [backdrop] };
        var library = DispatchProxy.Create<ILibraryManager, TestLibrary>();
        ((TestLibrary)(object)library).Items = [managed, unmanaged, noPoster];
        var task = new ClearPostersTask(library);

        Assert.Empty(task.GetDefaultTriggers());
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Same(backdrop, Assert.Single(managed.ImageInfos));
        Assert.Equal(ItemUpdateType.ImageUpdate, managed.LastUpdate);
        Assert.Same(poster, Assert.Single(unmanaged.ImageInfos));
        Assert.Null(unmanaged.LastUpdate);
        Assert.Same(backdrop, Assert.Single(noPoster.ImageInfos));
        Assert.Null(noPoster.LastUpdate);
    }

    private class TestLibrary : DispatchProxy
    {
        public List<BaseItem> Items { get; set; } = [];

        protected override object Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(ILibraryManager.GetItemList) => Items,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }

    private sealed class TestCollection : BoxSet
    {
        public ItemUpdateType? LastUpdate { get; private set; }

        public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
        {
            LastUpdate = updateReason;
            return Task.CompletedTask;
        }
    }
}
