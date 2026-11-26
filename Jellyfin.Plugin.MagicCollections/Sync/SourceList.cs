namespace Jellyfin.Plugin.MagicCollections.Sync;

public enum SourceMediaType { Movie, Series }

public sealed record MediaIdentity(SourceMediaType Type, IReadOnlyDictionary<string, string> ProviderIds);

public sealed record SourceList(string? Name, IReadOnlyList<MediaIdentity> Items, string? Description = null);

public interface ISourceListHandler
{
    bool Supports(Uri url);
    Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken);
}
