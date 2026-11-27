namespace Jellyfin.Plugin.MagicCollections.Sync;

public enum SourceMediaType { Movie, Series }

public sealed record MediaIdentity(SourceMediaType Type, IReadOnlyDictionary<string, string> ProviderIds);

public sealed record SourceList(string? Name, IReadOnlyList<MediaIdentity> ProviderItems, string? Description = null)
{
    // Reserved for BFI editorial lists, which don't publish provider IDs.
    public IReadOnlyList<MovieTitleYear> MoviesByTitleYear { get; init; } = [];
    public IReadOnlyList<Uri> FaviconUrls { get; init; } = [];
    public Uri? ImageUrl { get; init; }
}

public interface ISourceListHandler
{
    // The source's display name, used as a collection tag.
    string SourceTag { get; }

    bool Supports(Uri url);

    // Equivalent source URLs must share the same URL tag.
    Uri GetCanonicalUrl(Uri url) => url;

    Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken);
}
