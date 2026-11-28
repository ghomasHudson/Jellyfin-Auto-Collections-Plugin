using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MagicCollections;

internal sealed class JellyfinQueryHandler(ILibraryManager library)
{
    public Guid[] Read(JellyfinQuery source, CancellationToken cancellationToken)
    {
        var roots = FindScope(source.ParentId, cancellationToken);
        if (roots.Length == 0)
        {
            return [];
        }

        var studioIds = new HashSet<Guid>();
        foreach (var studio in source.Studios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Resolve names from the index; GetStudio(name) could create a new studio.
            studioIds.UnionWith(library.GetItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Studio],
                Name = studio
            }));
        }
        if (source.Studios.Length != 0 && studioIds.Count == 0)
        {
            return [];
        }

        var items = library.GetItemList(new InternalItemsQuery
        {
            // A physical library path can itself be nested under another configured path.
            AncestorIds = roots,
            IncludeItemTypes = source.ItemTypes,
            IsVirtualItem = false,
            IsPlaceHolder = false,
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            Genres = source.Genres,
            Tags = source.Tags,
            Years = source.Years,
            Person = source.Person,
            SearchTerm = source.SearchTerm,
            StudioIds = studioIds.ToArray()
        }, allowExternalContent: false);

        var ids = new HashSet<Guid>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Jellyfin 10.11's library repository doesn't apply MinCriticRating.
            if (item is Movie or Series && item.ExtraType is null && !item.IsShortcut && item.ChannelId == Guid.Empty &&
                item.Path is not null && Path.IsPathFullyQualified(item.Path) &&
                (source.MinCriticRating is not { } minimum || item.CriticRating >= minimum))
            {
                ids.Add(item.Id);
            }
        }
        if (source.Limit is not { } limit)
        {
            return ids.ToArray();
        }

        var selected = ids.ToArray();
        if (source.Random)
        {
            Random.Shared.Shuffle(selected);
        }
        else
        {
            Array.Sort(selected);
        }
        return selected.Length <= limit ? selected : selected[..limit];
    }

    private Guid[] FindScope(Guid? parentId, CancellationToken cancellationToken)
    {
        var roots = new HashSet<Guid>();
        var found = false;
        foreach (var entry in library.GetVirtualFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.LibraryOptions?.Enabled == false)
            {
                continue;
            }
            var resolved = Guid.TryParse(entry.ItemId, out var id) && id != Guid.Empty;
            if (parentId is { } selected ? !resolved || id != selected : entry.Locations.Length == 0)
            {
                continue;
            }
            if (!resolved || library.GetItemById(id) is not CollectionFolder folder ||
                (folder.PhysicalFolderIds.Length == 0 && entry.Locations.Length != 0))
            {
                throw new InvalidOperationException("A configured Jellyfin library isn't indexed yet.");
            }
            found = true;
            foreach (var root in folder.PhysicalFolderIds)
            {
                if (root == Guid.Empty || library.GetItemById(root) is not Folder)
                {
                    throw new InvalidOperationException("A configured Jellyfin library path could not be resolved.");
                }
                roots.Add(root);
            }
        }
        if (parentId is not null && !found)
        {
            throw new InvalidOperationException("parentId does not identify an enabled, configured Jellyfin library.");
        }
        return roots.ToArray();
    }
}
