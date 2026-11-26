namespace Jellyfin.Plugin.AutoCollections.Sync;

public enum SourceMediaType { Movie, Series }

public sealed record SourceItem(SourceMediaType Type, IReadOnlyDictionary<string, string> ProviderIds);

public sealed record SourceList(string? Name, IReadOnlyList<SourceItem> Items);

public interface ISourceListHandler
{
    bool Supports(Uri url);
    Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken);
}
