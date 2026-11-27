namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed record LibraryMedia(Guid Id, MediaIdentity Identity, MovieTitleYear? MovieTitleYear = null);

public sealed record TaggedCollection(Guid Id, IReadOnlyCollection<Guid> MemberIds);

public interface ICollectionLibrary
{
    IEnumerable<LibraryMedia> GetMedia();
    TaggedCollection? FindByTag(string tag);
    Task<Guid> CreateAsync(string name, string tag);
    Task AddMembersAsync(Guid id, IReadOnlyCollection<Guid> memberIds);
    Task SetOverviewAsync(Guid id, string overview, CancellationToken cancellationToken);
    Task EnsurePosterAsync(Guid id, CancellationToken cancellationToken);
}
