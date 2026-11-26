namespace Jellyfin.Plugin.AutoCollections.Sync;

public sealed class CollectionSync(IEnumerable<ISourceListHandler> handlers, ICollectionLibrary library)
{
    public async Task RunAsync(IEnumerable<string> urls, Action<string> reportUnsupported, CancellationToken cancellationToken)
    {
        // Load once per run, and only if there's a supported source.
        var media = new Lazy<ILookup<(SourceMediaType, string, string), Guid>>(() =>
            library.GetMedia().SelectMany(item => Keys(item.Type, item.ProviderIds), (item, key) => (key, item.Id))
                .ToLookup(pair => pair.key, pair => pair.Id));

        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var source) ||
                handlers.FirstOrDefault(h => h.Supports(source)) is not { } handler)
            {
                reportUnsupported(url);
                continue;
            }

            var list = await handler.ReadAsync(source, cancellationToken).ConfigureAwait(false);
            var matches = list.Items.SelectMany(item => Keys(item.Type, item.ProviderIds))
                .SelectMany(key => media.Value[key]).ToHashSet();
            if (matches.Count == 0)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var tag = $"AutoCollections:{source.AbsoluteUri}";
            var existing = library.FindByTag(tag);
            var id = existing?.Id ?? await library.CreateAsync(CollectionName(list, source), tag).ConfigureAwait(false);
            matches.ExceptWith(existing?.MemberIds ?? []);
            if (matches.Count != 0)
            {
                await library.AddMembersAsync(id, matches).ConfigureAwait(false);
            }
        }
    }

    private static string CollectionName(SourceList list, Uri source)
    {
        if (!string.IsNullOrWhiteSpace(list.Name))
        {
            return list.Name.Trim();
        }

        var slug = Uri.UnescapeDataString(source.Segments.Last().Trim('/'));
        var words = slug.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? source.Host : string.Join(' ', words);
    }

    private static IEnumerable<(SourceMediaType, string, string)> Keys(SourceMediaType type, IReadOnlyDictionary<string, string> ids)
    {
        foreach (var (provider, id) in ids)
        {
            if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(id))
            {
                yield return (type, provider.ToUpperInvariant(), id);
            }
        }
    }
}
