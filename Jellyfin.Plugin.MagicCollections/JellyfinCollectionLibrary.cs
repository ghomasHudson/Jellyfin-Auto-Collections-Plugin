using System.Net.Mime;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MagicCollections;

internal sealed class JellyfinCollectionLibrary(
    ILibraryManager library, ICollectionManager collections, IProviderManager providers,
    CollectionFaviconClient favicons) : ICollectionLibrary
{
    public IEnumerable<LibraryMedia> GetMedia()
    {
        var items = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            GroupByPresentationUniqueKey = false
        }, allowExternalContent: false);
        return items.Select(item =>
        {
            var type = item is Movie ? SourceMediaType.Movie : SourceMediaType.Series;
            MovieTitleYear? titleYear = type == SourceMediaType.Movie &&
                item.ProductionYear is int year && year > 0 && !string.IsNullOrWhiteSpace(item.Name)
                ? new(item.Name, year)
                : null;
            return new LibraryMedia(item.Id, new(type, item.ProviderIds), titleYear);
        });
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
            Name = $"MagicCollections-{Guid.NewGuid():N}"
        }).ConfigureAwait(false);
        collection.Name = name;
        collection.Tags = [.. collection.Tags, tag];
        collection.LockedFields = [.. collection.LockedFields, MetadataField.Name, MetadataField.Tags];
        await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        return collection.Id;
    }

    public Task AddMembersAsync(Guid id, IReadOnlyCollection<Guid> memberIds) =>
        collections.AddToCollectionAsync(id, memberIds);

    public Task SetOverviewAsync(Guid id, string overview, CancellationToken cancellationToken)
    {
        if (library.GetItemById(id) is not BoxSet collection || collection.Overview == overview)
        {
            return Task.CompletedTask;
        }

        collection.Overview = overview;
        return collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
    }

    public async Task EnsurePosterAsync(Guid id, IReadOnlyList<Uri> faviconUrls, CancellationToken cancellationToken)
    {
        if (library.GetItemById(id) is not BoxSet collection || HasCurrentPoster(collection))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var logo = await favicons.TryLoadAsync(faviconUrls, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var poster = CollectionPoster.Render(collection.Name, logo);

        // Jellyfin's SaveImage isn't conditional. Recheck after rendering to narrow the
        // race with an upload, but another writer can still enter after this check.
        cancellationToken.ThrowIfCancellationRequested();
        if (library.GetItemById(id) is not BoxSet current || HasCurrentPoster(current))
        {
            return;
        }

        await providers.SaveImage(current, poster, MediaTypeNames.Image.Png, ImageType.Primary, null, cancellationToken)
            .ConfigureAwait(false);
        await current.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool HasCurrentPoster(BoxSet collection)
    {
        var image = collection.GetImageInfo(ImageType.Primary, 0);
        if (image is null)
        {
            return false;
        }

        var path = image.Path;
        if (!image.IsLocalFile || !Path.IsPathFullyQualified(path))
        {
            return true;
        }

        try
        {
            // File.Exists also returns false for access and I/O errors.
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }
}
