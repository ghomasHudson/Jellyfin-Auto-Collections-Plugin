using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class CollectionSync(IEnumerable<ISourceListHandler> handlers, ICollectionLibrary library)
{
    internal const string ManagedCollectionTag = "Magic Collection";
    private readonly JellyfinQueryHandler? _jellyfin;

    internal CollectionSync(IEnumerable<ISourceListHandler> handlers, ICollectionLibrary library, JellyfinQueryHandler jellyfin)
        : this(handlers, library) => _jellyfin = jellyfin;

    public async Task RunAsync(IEnumerable<string> urls, Action<string> reportUnsupported,
        CancellationToken cancellationToken, ILogger? verboseLogger = null,
        Action<string>? reportPrivateFailure = null, Action<string>? reportJellyfinFailure = null)
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
            Uri? externalSource = null;
            string sourceTag, handlerTag;
            string? logUrl, previousTag = null;
            SourceList list;
            HashSet<Guid> matches;
            int matchableEntryCount;
            if (_jellyfin is not null && url?.Trim() is { } localSource && JellyfinQuery.HasPrefix(localSource))
            {
                var (query, error) = JellyfinQuery.Parse(localSource);
                if (query is null)
                {
                    var message = $"Invalid Jellyfin library query: {error}";
                    if (reportJellyfinFailure is null) throw new ArgumentException(message);
                    reportJellyfinFailure(message);
                    continue;
                }
                sourceTag = "jellyfin-query:" + Hash(query.CanonicalSource);
                handlerTag = "Jellyfin";
                logUrl = sourceTag;
                if (!BeginSource(sourceTag, logUrl))
                {
                    continue;
                }
                try
                {
                    matches = _jellyfin.Read(query, cancellationToken).ToHashSet();
                }
                catch (Exception ex) when (ex is not OperationCanceledException && reportJellyfinFailure is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    reportJellyfinFailure($"Jellyfin library query failed ({sourceTag}): {ex.Message}");
                    continue;
                }
                matchableEntryCount = matches.Count;
                list = new SourceList(query.Name, [], query.Description);
            }
            else
            {
                if (SourceListHandlerLookup.Find(handlers, url) is not { } match)
                {
                    reportUnsupported(url!);
                    continue;
                }
                var (source, handler) = match;
                externalSource = source;
                handlerTag = handler.SourceTag;
                var canonicalSource = handler.GetCanonicalUrl(source);
                var canonicalUrl = canonicalSource.AbsoluteUri;
                // A personal list may be private; don't wait for the API to find out before redacting it.
                var opaqueTag = handler is TraktHandler trakt && trakt.IsPersonalList(canonicalSource)
                    ? PrivateTraktTag(canonicalUrl) : null;
                logUrl = opaqueTag ?? (verboseLogger is null ? null :
                    canonicalSource.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped));
                if (!BeginSource(canonicalUrl, logUrl))
                {
                    continue;
                }
                try
                {
                    list = await handler.ReadAsync(source, cancellationToken).ConfigureAwait(false);
                }
                catch (SourceListReadException ex) when (ex.IsPrivateTraktFailure && reportPrivateFailure is not null)
                {
                    reportPrivateFailure($"{opaqueTag}: {ex.Reason}");
                    continue;
                }
                matches = list.ProviderItems.SelectMany(Keys)
                    .SelectMany(key => mediaByProvider.Value[key]).ToHashSet();
                foreach (var movie in list.MoviesByTitleYear.Where(movie => movie.IsComplete))
                {
                    var candidates = moviesByTitleYear.Value[movie.MatchKey()].Distinct().Take(2).ToArray();
                    if (candidates.Length == 1)
                    {
                        matches.Add(candidates[0]);
                    }
                }
                matchableEntryCount = list.ProviderItems.Count + list.MoviesByTitleYear.Count;
                // Watchlist and recommendation URLs refer to the connected account's own results.
                sourceTag = list.TraktAccountScopedOwner is { } owner
                    ? PrivateTraktTag($"{canonicalUrl}|{owner.ToLowerInvariant()}")
                    : list.ReadWithTraktAccount ? opaqueTag! : canonicalUrl;
                if (opaqueTag is not null && list.TraktAccountScopedOwner is null)
                {
                    previousTag = list.ReadWithTraktAccount ? canonicalUrl : opaqueTag;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var libraryMatchCount = matches.Count;
            var existing = library.FindByTags(sourceTag, ManagedCollectionTag);
            if (existing is null && previousTag is not null &&
                library.FindByTags(previousTag, ManagedCollectionTag) is { } previous)
            {
                // Changing the source's access mode must not create a second collection.
                // Only migrate after successfully reading the entire list.
                await library.ReplaceTagAsync(previous.Id, previousTag, sourceTag, cancellationToken).ConfigureAwait(false);
                existing = previous;
            }

            if (existing is null && libraryMatchCount == 0)
            {
                verboseLogger?.LogInformation(
                    "No library matches for {SourceUrl} ({MatchableEntryCount} entries available for matching); collection not created",
                    logUrl, matchableEntryCount);
                continue;
            }

            var id = existing?.Id ?? await library.CreateAsync(
                CollectionName(list, externalSource), ManagedCollectionTag, sourceTag, handlerTag)
                .ConfigureAwait(false);
            var currentMembers = existing?.MemberIds ?? [];
            var additions = matches.Except(currentMembers).ToArray();
            var removals = currentMembers.Except(matches).ToArray();
            if (additions.Length != 0)
            {
                await library.AddMembersAsync(id, additions).ConfigureAwait(false);
            }
            if (removals.Length != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await library.RemoveMembersAsync(id, removals).ConfigureAwait(false);
            }
            verboseLogger?.LogInformation(
                "Collection membership for {SourceUrl}: {LibraryMatchCount} library matches from {MatchableEntryCount} entries available for matching; added {AddedCount} items, removed {RemovedCount} items (collection created: {Created})",
                logUrl, libraryMatchCount, matchableEntryCount, additions.Length, removals.Length, existing is null);

            if (!string.IsNullOrWhiteSpace(list.Description))
            {
                await library.EnsureOverviewAsync(id, list.Description, cancellationToken).ConfigureAwait(false);
            }
            await library.EnsurePosterAsync(id, list.FaviconUrls, list.ImageUrl, cancellationToken).ConfigureAwait(false);
        }

        bool BeginSource(string canonicalUrl, string? logUrl)
        {
            if (!syncedSources.Add(canonicalUrl))
            {
                verboseLogger?.LogInformation("Skipping duplicate source list {SourceUrl}", logUrl);
                return false;
            }
            verboseLogger?.LogInformation("Reading source list {SourceUrl}", logUrl);
            return true;
        }
    }

    private static string PrivateTraktTag(string canonicalUrl) =>
        "trakt-private:" + Hash(canonicalUrl);

    private static string Hash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    private static string CollectionName(SourceList list, Uri? source)
    {
        if (!string.IsNullOrWhiteSpace(list.Name))
        {
            return list.Name.Trim();
        }
        if (source is null)
        {
            return "Jellyfin library query";
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
