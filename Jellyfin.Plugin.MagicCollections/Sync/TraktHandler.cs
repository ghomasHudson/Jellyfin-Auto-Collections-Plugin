using System.Globalization;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.MagicCollections.Configuration;

namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class TraktHandler(HttpClient http, Func<PluginConfiguration> configuration,
    TraktConnection? connection = null) : ISourceListHandler
{
    private static readonly Uri ApiOrigin = new("https://api.trakt.tv");
    private static readonly Uri TraktFavicon = new("https://app.trakt.tv/pwa/android/icon_192.png");
    private const int PageSize = 50;
    private const int ChartEntryCap = 100;

    public string SourceTag => "Trakt";

    public bool Supports(Uri url) => ParseSource(url) is not null;

    public Uri GetCanonicalUrl(Uri url) => RequireSource(url).CanonicalUrl;

    internal static string CanonicalizeConfiguredUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && ParseSource(uri) is { } source
            ? source.CanonicalUrl.AbsoluteUri
            : url;

    internal bool IsPersonalList(Uri url) => ParseSource(url)?.Owner is not null;

    public async Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken)
    {
        var source = RequireSource(url);
        try
        {
            return await ReadSourceAsync(source, authenticated: false, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceListReadException ex) when (source.Owner is not null &&
            ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            if (connection is null)
            {
                throw SourceError(source, $"{ex.Reason} Connect Trakt in the Magic Collections settings.", authenticated: true);
            }

            // Retry the whole read so the summary and every page use the same access mode.
            return await ReadSourceAsync(source, authenticated: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SourceList> ReadSourceAsync(Source source, bool authenticated, CancellationToken cancellationToken)
    {
        var clientId = TraktConnection.ResolveClientId(configuration().TraktClientId);
        if (clientId.Any(c => c is < '!' or > '~'))
        {
            throw ReadError("Invalid Trakt Client ID in plugin settings.");
        }

        try
        {
            var (name, description) = source.Chart is { } chart
                ? (chart.Name, null)
                : await ReadSummaryAsync(source, clientId, authenticated, cancellationToken).ConfigureAwait(false);
            var items = await ReadPagesAsync(source, clientId, authenticated, cancellationToken).ConfigureAwait(false);
            return new(name, items, description)
            {
                FaviconUrls = [TraktFavicon],
                ReadWithTraktAccount = authenticated
            };
        }
        catch (JsonException)
        {
            throw ReadError("Invalid Trakt source list data.");
        }
        catch (TraktConnectionException exception)
        {
            throw ReadError(exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw ReadError("Trakt API request or connection store failed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ReadError("Trakt API request timed out.");
        }

        SourceListReadException ReadError(string reason) => SourceError(source, reason, authenticated);
    }

    private static SourceListReadException SourceError(Source source, string reason, bool authenticated,
        HttpStatusCode? status = null) => new(source.Owner is null ? source.CanonicalUrl : ApiOrigin, reason)
        {
            StatusCode = status,
            IsPrivateTraktFailure = authenticated
        };

    private async Task<(string? Name, string? Description)> ReadSummaryAsync(Source source, string clientId, bool authenticated,
        CancellationToken cancellationToken)
    {
        using var response = await FetchAsync(source, source.ApiPath + "/", clientId, authenticated, cancellationToken, allowNoContent: !authenticated)
            .ConfigureAwait(false);
        // Some official lists have no user-list summary but do expose items on the public list route.
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return (null, null);
        }

        using var data = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (data.RootElement.ValueKind != JsonValueKind.Object ||
            !data.RootElement.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
        {
            throw new JsonException("Expected a Trakt list name.");
        }

        if (!authenticated && data.RootElement.TryGetProperty("privacy", out var privacy) &&
            privacy.ValueKind == JsonValueKind.String && privacy.GetString() is "private" or "friends")
        {
            throw SourceError(source, "This Trakt list requires sign-in.", authenticated, HttpStatusCode.Forbidden);
        }

        var description = data.RootElement.TryGetProperty("description", out var overview) &&
            overview.ValueKind == JsonValueKind.String ? overview.GetString()?.Trim() : null;
        return (name.GetString(), string.IsNullOrEmpty(description) ? null : description);
    }

    private async Task<List<MediaIdentity>> ReadPagesAsync(Source source, string clientId, bool authenticated, CancellationToken cancellationToken)
    {
        var items = new List<MediaIdentity>();
        var chart = source.Chart;
        var path = chart is null ? source.ApiPath + "/items/movie,show,season,episode" : source.ApiPath;
        Pagination? firstPagePagination = null;
        var chartEntriesRemaining = ChartEntryCap;
        for (var page = 1; ; page++)
        {
            var endpoint = FormattableString.Invariant($"{path}?page={page}&limit={PageSize}");
            using var response = await FetchAsync(source, endpoint, clientId, authenticated, cancellationToken).ConfigureAwait(false);
            using var data = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = data.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Expected an array of Trakt entries.");
            }

            var pagination = ReadPagination(response, page, root.GetArrayLength());
            // Live charts can grow or shrink between requests, but the page size must
            // stay fixed so page offsets (and the rank cap) remain meaningful.
            if (firstPagePagination is { } initial &&
                (pagination.Limit != initial.Limit || (chart is null && pagination != initial)))
            {
                throw new JsonException("Trakt pagination changed between pages.");
            }
            firstPagePagination ??= pagination;

            foreach (var row in root.EnumerateArray())
            {
                // Count ranks, not matchable IDs. A chart page may straddle the cap.
                if (chart is not null)
                {
                    if (chartEntriesRemaining == 0)
                    {
                        return items;
                    }

                    chartEntriesRemaining--;
                }

                var identity = chart is null
                    ? ReadListItem(row)
                    : ReadMedia(chart.Wrapped ? Unwrap(row, chart.MediaKey) : row, chart.Type);
                if (identity is not null && identity.ProviderIds.Count != 0)
                {
                    items.Add(identity);
                }
            }
            if (page >= pagination.PageCount || (chart is not null && chartEntriesRemaining == 0))
            {
                return items;
            }
        }
    }

    private static Pagination ReadPagination(HttpResponseMessage response, int page, int entryCount)
    {
        var reportedPage = ReadPaginationHeader(response, "X-Pagination-Page");
        var limit = ReadPaginationHeader(response, "X-Pagination-Limit");
        var pageCount = ReadPaginationHeader(response, "X-Pagination-Page-Count");
        var itemCount = ReadPaginationHeader(response, "X-Pagination-Item-Count");
        if (reportedPage != page || limit is < 1 or > PageSize ||
            pageCount != (itemCount + (long)limit - 1) / limit || page > Math.Max(1, pageCount) ||
            entryCount != Math.Min(limit, itemCount - (page - 1L) * limit))
        {
            throw new JsonException("Trakt pagination does not match the entries returned.");
        }

        return new(limit, pageCount, itemCount);
    }

    private static int ReadPaginationHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values) && values.ToArray() is [var value] &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }
        throw new JsonException("Missing or invalid Trakt pagination header.");
    }

    private readonly record struct Pagination(int Limit, int PageCount, int ItemCount);

    private async Task<HttpResponseMessage> FetchAsync(Source source, string path, string clientId, bool authenticated,
        CancellationToken cancellationToken, bool allowNoContent = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiOrigin, path));
        HttpResponseMessage response;
        if (authenticated)
        {
            if (connection is null || source.Owner is null)
            {
                throw new TraktConnectionException("Connect Trakt and enter a personal list URL.");
            }
            response = await connection.SendListRequestAsync(request, source.Owner, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            TraktConnection.AddApiHeaders(request, clientId);
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        if (response.StatusCode == HttpStatusCode.OK || (allowNoContent && response.StatusCode == HttpStatusCode.NoContent))
        {
            return response;
        }

        using (response)
        {
            var reason = FormattableString.Invariant($"Trakt API returned HTTP {(int)response.StatusCode}.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Do not retry automatically, or put the unparsed header or response body into the log.
                reason += response.Headers.RetryAfter switch
                {
                    { Delta: { } delay } => FormattableString.Invariant($" Trakt rate limit reached; retry after {delay.TotalSeconds} seconds."),
                    { Date: { } date } => FormattableString.Invariant($" Trakt rate limit reached; retry after {date:R}."),
                    _ => " Trakt rate limit reached; wait before retrying."
                };
            }

            throw SourceError(source, reason, authenticated, response.StatusCode);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static MediaIdentity? ReadListItem(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Expected a Trakt entry with a type.");
        }

        var mediaKey = type.GetString()!;
        return GetMediaType(mediaKey) is { } media ? ReadMedia(Unwrap(row, mediaKey), media) : null;
    }

    private static SourceMediaType? GetMediaType(string mediaKey) => mediaKey switch
    {
        "movie" => SourceMediaType.Movie,
        "show" => SourceMediaType.Series,
        _ => null
    };

    private static JsonElement Unwrap(JsonElement row, string field) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(field, out var item)
            ? item : throw new JsonException("Expected a wrapped Trakt movie or show.");

    private static MediaIdentity ReadMedia(JsonElement item, SourceMediaType type)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("ids", out var ids) ||
            ids.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected a Trakt movie or show with IDs.");
        }

        var providers = new Dictionary<string, string>();
        ids.TryGetProperty("imdb", out var imdb);
        if (ImdbId.ReadOptional(imdb, minimumDigits: 1) is { } imdbId)
        {
            providers["Imdb"] = imdbId;
        }
        AddNumericId(ids, "tmdb", "Tmdb", providers);
        AddNumericId(ids, "tvdb", "Tvdb", providers);
        return new(type, providers);
    }

    private static void AddNumericId(JsonElement ids, string field, string provider, Dictionary<string, string> providers)
    {
        if (!ids.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id < 0)
        {
            throw new JsonException("Expected an integer Trakt provider ID.");
        }
        if (id > 0)
        {
            providers[provider] = id.ToString(CultureInfo.InvariantCulture);
        }
    }

    private sealed record Source(string ApiPath, Uri CanonicalUrl, Chart? Chart = null, string? Owner = null);

    private sealed record Chart(string Name, string MediaKey, SourceMediaType Type, bool Wrapped);

    private static Source RequireSource(Uri url) =>
        ParseSource(url) ?? throw new ArgumentException("Not a Trakt source list URL.", nameof(url));

    private static Source? ParseSource(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("https" or "http") ||
            url.Host is not ("trakt.tv" or "app.trakt.tv") || !url.IsDefaultPort || url.UserInfo.Length != 0)
        {
            return null;
        }

        var segments = url.AbsolutePath.TrimEnd('/').Split('/');
        if (segments is ["", "users", var user, "lists", var list] &&
            Uri.UnescapeDataString(user) is var userSlug && IsSlug(userSlug) &&
            !userSlug.Equals("me", StringComparison.OrdinalIgnoreCase) &&
            Uri.UnescapeDataString(list) is var listSlug && IsSlug(listSlug))
        {
            var path = $"/users/{userSlug.ToLowerInvariant()}/lists/{listSlug.ToLowerInvariant()}";
            return new(path, new("https://app.trakt.tv" + path), Owner: userSlug);
        }

        // Additional query parameters might change chart membership; only accept unfiltered charts.
        var (kind, mode) = (url.Host, segments, url.Query) switch
        {
            ("trakt.tv", ["", "movies", var chart], "") => (chart, "movie"),
            ("trakt.tv", ["", "shows", var chart], "") => (chart, "show"),
            ("app.trakt.tv", ["", "discover", var chart], "?mode=movie") => (chart, "movie"),
            ("app.trakt.tv", ["", "discover", var chart], "?mode=show") => (chart, "show"),
            _ => (null, null)
        };
        if (kind is not ("popular" or "trending") || mode is null || GetMediaType(mode) is not { } media)
        {
            return null;
        }

        var trending = kind == "trending";
        var name = (trending ? "Trending " : "Popular ") + (media == SourceMediaType.Movie ? "Movies" : "Shows");
        return new($"/{mode}s/{kind}", new($"https://app.trakt.tv/discover/{kind}?mode={mode}"),
            new(name, mode, media, trending));
    }

    private static bool IsSlug(string slug) =>
        slug.Length > 0 && slug is not ("." or "..") &&
        slug.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
