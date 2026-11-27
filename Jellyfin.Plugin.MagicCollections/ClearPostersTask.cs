using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MagicCollections;

public sealed class ClearPostersTask(ILibraryManager library) : IScheduledTask
{
    public string Name => "Clear Magic Collections Posters";
    public string Description => "Delete primary posters, including custom artwork, from Magic Collections. Run Sync Magic Collections to generate new posters.";
    public string Category => "Magic Collections";
    public string Key => "MagicCollectionsClearPosters";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collectionIds = library.GetItemIds(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            GroupByPresentationUniqueKey = false,
            Tags = [CollectionSync.ManagedCollectionTag]
        });
        foreach (var id in collectionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (library.GetItemById(id) is BoxSet collection &&
                collection.Tags.Contains(CollectionSync.ManagedCollectionTag, StringComparer.Ordinal))
            {
                await collection.DeleteImageAsync(ImageType.Primary, 0).ConfigureAwait(false);
            }
        }

        progress.Report(100);
    }
}
