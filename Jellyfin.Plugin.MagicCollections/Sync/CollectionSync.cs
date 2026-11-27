using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class CollectionSync(IEnumerable<ISourceListHandler> handlers, ICollectionLibrary library)
{
    internal const string ManagedCollectionTag = "Magic Collection";

    public async Task RunAsync(IEnumerable<string> urls, Action<string> reportUnsupported,
        CancellationToken cancellationToken, ILogger? verboseLogger = null,
        IEnumerable<string>? privateTraktUrls = null, Action<string>? reportPrivateFailure = null)
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

        var privateSources = (privateTraktUrls ?? [])
            .Select(TraktHandler.CanonicalizePersonalList)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (url, isPrivate) in privateSources.Select(url => (url, true)).Concat(urls.Select(url => (url, false))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidatesForSource = isPrivate ? handlers.OfType<TraktHandler>() : handlers;
            if (SourceListHandlerLookup.Find(candidatesForSource, url) is not { } match)
            {
                if (isPrivate)
                {
                    reportPrivateFailure?.Invoke("No Trakt list handler is available.");
                }
                else
                {
                    reportUnsupported(url);
                }
                continue;
            }

            var (source, handler) = match;
            var canonicalSource = handler.GetCanonicalUrl(source);
            var canonicalUrl = canonicalSource.AbsoluteUri;
            if (!isPrivate && privateSources.Contains(canonicalUrl))
            {
                continue;
            }

            var sourceTag = isPrivate ? PrivateTraktTag(canonicalUrl) : canonicalUrl;
            // Leave credentials, query strings and fragments out of the verbose logs.
            var logUrl = isPrivate ? sourceTag : verboseLogger is null ? null :
                canonicalSource.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
            if (!syncedSources.Add(sourceTag))
            {
                verboseLogger?.LogInformation("Skipping duplicate source list {SourceUrl}", logUrl);
                continue;
            }

            verboseLogger?.LogInformation("Reading source list {SourceUrl}", logUrl);
            SourceList list;
            try
            {
                list = isPrivate
                    ? await ((TraktHandler)handler).ReadPrivateAsync(source, cancellationToken).ConfigureAwait(false)
                    : await handler.ReadAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (SourceListReadException ex) when (isPrivate)
            {
                reportPrivateFailure?.Invoke($"{sourceTag}: {ex.Reason}");
                continue;
            }
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
            var matchableEntryCount = list.ProviderItems.Count + list.MoviesByTitleYear.Count;
            var libraryMatchCount = matches.Count;
            var existing = library.FindByTags(sourceTag, ManagedCollectionTag);
            if (existing is null && (isPrivate || handler is TraktHandler && TraktHandler.CanonicalizePersonalList(canonicalUrl) is not null))
            {
                // Changing the source's access mode must not create a second collection.
                // Only migrate after successfully reading the entire list.
                var previousTag = isPrivate ? canonicalUrl : PrivateTraktTag(canonicalUrl);
                if (library.FindByTags(previousTag, ManagedCollectionTag) is { } previous)
                {
                    await library.ReplaceTagAsync(previous.Id, previousTag, sourceTag, cancellationToken).ConfigureAwait(false);
                    existing = previous;
                }
            }

            if (existing is null && libraryMatchCount == 0)
            {
                verboseLogger?.LogInformation(
                    "No library matches for {SourceUrl} ({MatchableEntryCount} entries available for matching); collection not created",
                    logUrl, matchableEntryCount);
                continue;
            }

            var id = existing?.Id ?? await library.CreateAsync(
                CollectionName(list, source), ManagedCollectionTag, sourceTag, handler.SourceTag)
                .ConfigureAwait(false);
            matches.ExceptWith(existing?.MemberIds ?? []);
            if (matches.Count != 0)
            {
                await library.AddMembersAsync(id, matches).ConfigureAwait(false);
            }
            verboseLogger?.LogInformation(
                "Collection membership for {SourceUrl}: {LibraryMatchCount} library matches from {MatchableEntryCount} entries available for matching; added {AddedCount} items (collection created: {Created})",
                logUrl, libraryMatchCount, matchableEntryCount, matches.Count, existing is null);

            if (!string.IsNullOrWhiteSpace(list.Description))
            {
                await library.SetOverviewAsync(id, list.Description, cancellationToken).ConfigureAwait(false);
            }
            await library.EnsurePosterAsync(id, list.FaviconUrls, list.ImageUrl, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string PrivateTraktTag(string canonicalUrl) =>
        "trakt-private:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalUrl)));

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
