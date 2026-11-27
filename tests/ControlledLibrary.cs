using Jellyfin.Plugin.MagicCollections.Sync;

namespace MagicCollections.Tests;

internal sealed class ControlledLibrary : ICollectionLibrary
{
    public sealed record ManagedCollection(Guid Id, string Name, IReadOnlyList<string> Tags, List<Guid> Members)
    {
        public string? Overview { get; set; }
        public string? PosterTitle { get; set; }
        public IReadOnlyList<Uri> PosterFaviconUrls { get; set; } = [];
        public Uri? PosterListImageUrl { get; set; }
    }

    public List<LibraryMedia> Media { get; } = [];
    public List<ManagedCollection> ManagedCollections { get; } = [];

    public IEnumerable<LibraryMedia> GetMedia() => Media;
    public TaggedCollection? FindByTags(string urlTag, string markerTag) =>
        ManagedCollections.SingleOrDefault(c => c.Tags.Contains(urlTag) && c.Tags.Contains(markerTag)) is { } c
            ? new(c.Id, c.Members.ToArray()) : null;

    public IEnumerable<(Guid Id, IReadOnlyList<string> Tags)> GetCollectionsWithTag(string tag) =>
        ManagedCollections.Where(c => c.Tags.Contains(tag)).Select(c => (c.Id, c.Tags));

    public Task<Guid> CreateAsync(string name, params string[] tags)
    {
        var collection = new ManagedCollection(Guid.NewGuid(), name, [.. tags], []);
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
        return collection.PosterTitle is null
            ? RegeneratePosterAsync(id, faviconUrls, listImageUrl, cancellationToken)
            : Task.CompletedTask;
    }

    public Task RegeneratePosterAsync(Guid id, IReadOnlyList<Uri> faviconUrls, Uri? listImageUrl, CancellationToken cancellationToken)
    {
        var collection = ManagedCollections.Single(c => c.Id == id);
        collection.PosterTitle = collection.Name;
        collection.PosterFaviconUrls = faviconUrls;
        collection.PosterListImageUrl = listImageUrl;

        return Task.CompletedTask;
    }
}
