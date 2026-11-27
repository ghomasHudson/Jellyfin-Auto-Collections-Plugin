namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class CollectionSync(IEnumerable<ISourceListHandler> handlers, ICollectionLibrary library)
{
    public async Task RunAsync(IEnumerable<string> urls, Action<string> reportUnsupported, CancellationToken cancellationToken)
    {
        // Snapshot once per run. Build each index only if a source uses it.
        var media = new Lazy<LibraryMedia[]>(() => library.GetMedia().ToArray());
        var mediaByProvider = new Lazy<ILookup<MatchKey, Guid>>(() =>
            media.Value.SelectMany(item => Keys(item.Identity), (item, key) => (key, item.Id))
                .ToLookup(pair => pair.key, pair => pair.Id));
        var moviesByTitleYear = new Lazy<ILookup<MovieTitleYear, Guid>>(() =>
            media.Value.Where(item => item.Identity.Type == SourceMediaType.Movie &&
                    item.MovieTitleYear is { IsComplete: true })
                .ToLookup(item => item.MovieTitleYear!.Value.MatchKey(), item => item.Id));
        var syncedSources = new HashSet<string>(StringComparer.Ordinal);

        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var source) ||
                handlers.FirstOrDefault(h => h.Supports(source)) is not { } handler)
            {
                reportUnsupported(url);
                continue;
            }

            var canonicalUrl = handler.GetCanonicalUrl(source).AbsoluteUri;
            if (!syncedSources.Add(canonicalUrl))
            {
                continue;
            }

            var list = await handler.ReadAsync(source, cancellationToken).ConfigureAwait(false);
            var matches = list.ProviderItems.SelectMany(Keys)
                .SelectMany(key => mediaByProvider.Value[key]).ToHashSet();
            foreach (var movie in list.MoviesByTitleYear.Where(movie => movie.IsComplete))
            {
                var candidates = moviesByTitleYear.Value[movie.MatchKey()].Distinct().Take(2).ToArray();
                if (candidates.Length == 1)
                {
                    matches.Add(candidates[0]);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var tag = $"AutoCollections:{canonicalUrl}";
            var existing = library.FindByTag(tag);
            if (existing is null && matches.Count == 0)
            {
                continue;
            }

            var id = existing?.Id ?? await library.CreateAsync(CollectionName(list, source), tag).ConfigureAwait(false);
            matches.ExceptWith(existing?.MemberIds ?? []);
            if (matches.Count != 0)
            {
                await library.AddMembersAsync(id, matches).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(list.Description))
            {
                await library.SetOverviewAsync(id, list.Description, cancellationToken).ConfigureAwait(false);
            }
            await library.EnsurePosterAsync(id, list.FaviconUrls, cancellationToken).ConfigureAwait(false);
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

    private readonly record struct MatchKey(SourceMediaType Type, string Provider, string ProviderId);

    private static IEnumerable<MatchKey> Keys(MediaIdentity identity)
    {
        foreach (var (provider, id) in identity.ProviderIds)
        {
            if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(id))
            {
                yield return new(identity.Type, provider.ToUpperInvariant(), id);
            }
        }
    }
}
