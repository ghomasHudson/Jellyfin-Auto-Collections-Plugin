using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AutoCollections.Sync;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AutoCollections;

internal sealed class JellyfinCollectionLibrary(ILibraryManager library, ICollectionManager collections) : ICollectionLibrary
{
    public IEnumerable<LibraryMedia> GetMedia()
    {
        var items = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            GroupByPresentationUniqueKey = false
        }, allowExternalContent: false);
        return items.Select(item => new LibraryMedia(
            item.Id, new(item is Movie ? SourceMediaType.Movie : SourceMediaType.Series, item.ProviderIds)));
    }

    public TaggedCollection? FindByTag(string tag)
    {
        var candidates = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            GroupByPresentationUniqueKey = false,
            Tags = [tag]
        });
        var collection = candidates.OfType<BoxSet>().SingleOrDefault(c => c.Tags.Contains(tag, StringComparer.Ordinal));
        return collection is null ? null : new(collection.Id, collection.GetLinkedChildren().Select(item => item.Id).ToArray());
    }

    public async Task<Guid> CreateAsync(string name, string tag)
    {
        // Jellyfin derives the box set's folder and ID from the creation name.
        // Use a private storage name so a display namesake cannot collide with it.
        var collection = await collections.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = $"AutoCollections-{Guid.NewGuid():N}"
        }).ConfigureAwait(false);
        collection.Name = name;
        collection.Tags = [.. collection.Tags, tag];
        collection.LockedFields = [.. collection.LockedFields, MetadataField.Name, MetadataField.Tags];
        await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        return collection.Id;
    }

    public Task AddMembersAsync(Guid id, IReadOnlyCollection<Guid> memberIds) =>
        collections.AddToCollectionAsync(id, memberIds);
}
