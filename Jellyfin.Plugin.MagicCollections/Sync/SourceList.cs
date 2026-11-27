namespace Jellyfin.Plugin.MagicCollections.Sync;

public enum SourceMediaType { Movie, Series }

public sealed record MediaIdentity(SourceMediaType Type, IReadOnlyDictionary<string, string> ProviderIds);

public sealed record SourceList(string? Name, IReadOnlyList<MediaIdentity> Items, string? Description = null)
{
    // Opt-in for BFI editorial lists, which don't publish provider IDs.
    internal IReadOnlyList<MovieTitleYear> MoviesByTitleYear { get; init; } = [];
}

public interface ISourceListHandler
{
    bool Supports(Uri url);

    // Equivalent source URLs must share the same managed collection tag.
    Uri GetCanonicalUrl(Uri url) => url;

    Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken);
}
