namespace Jellyfin.Plugin.AutoCollections.Sync;

public sealed record LibraryMedia(Guid Id, SourceMediaType Type, IReadOnlyDictionary<string, string> ProviderIds);

public sealed record TaggedCollection(Guid Id, IReadOnlyCollection<Guid> MemberIds);

public interface ICollectionLibrary
{
    IEnumerable<LibraryMedia> GetMedia();
    TaggedCollection? FindByTag(string tag);
    Task<Guid> CreateAsync(string name, string tag);
    Task AddMembersAsync(Guid id, IReadOnlyCollection<Guid> memberIds);
}
