using Jellyfin.Plugin.MagicCollections.Sync;

namespace MagicCollections.Tests;

internal sealed class ControlledLibrary : ICollectionLibrary
{
    public sealed record ManagedCollection(Guid Id, string Name, string Tag, List<Guid> Members)
    {
        public string? Overview { get; set; }
        public string? PosterTitle { get; set; }
        public IReadOnlyList<Uri> PosterFaviconUrls { get; set; } = [];
        public Uri? PosterListImageUrl { get; set; }
    }

    public List<LibraryMedia> Media { get; } = [];
    public List<ManagedCollection> ManagedCollections { get; } = [];

    public IEnumerable<LibraryMedia> GetMedia() => Media;
    public TaggedCollection? FindByTag(string tag) => ManagedCollections.SingleOrDefault(c => c.Tag == tag) is { } c
        ? new(c.Id, c.Members.ToArray()) : null;

    public Task<Guid> CreateAsync(string name, string tag)
    {
        var collection = new ManagedCollection(Guid.NewGuid(), name, tag, []);
        ManagedCollections.Add(collection);
        return Task.FromResult(collection.Id);
    }

    public Task AddMembersAsync(Guid id, IReadOnlyCollection<Guid> memberIds)
    {
        ManagedCollections.Single(c => c.Id == id).Members.AddRange(memberIds);
        return Task.CompletedTask;
    }

    public Task SetOverviewAsync(Guid id, string overview, CancellationToken cancellationToken)
    {
        ManagedCollections.Single(c => c.Id == id).Overview = overview;
        return Task.CompletedTask;
    }

    public Task EnsurePosterAsync(Guid id, IReadOnlyList<Uri> faviconUrls, Uri? listImageUrl, CancellationToken cancellationToken)
    {
        var collection = ManagedCollections.Single(c => c.Id == id);
        if (collection.PosterTitle is null)
        {
            collection.PosterTitle = collection.Name;
            collection.PosterFaviconUrls = faviconUrls;
            collection.PosterListImageUrl = listImageUrl;
        }

        return Task.CompletedTask;
    }
}
