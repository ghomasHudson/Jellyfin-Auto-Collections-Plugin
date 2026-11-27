using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal sealed class ImdbHandler(IImdbPageRenderer renderer) : ISourceListHandler
{
    private const string Origin = "https://www.imdb.com";
    private const int ChartSize = 250;
    // IMDb's documented 12,000-item limit takes 48 numbered pages at its current 250 rows per page.
    private const int MaxListPages = 60;
    // Match the original URL, not a path normalized by Uri (which can resolve dot segments and escapes).
    private static readonly Regex SourceUrl = new(
        @"\A(?i:https://(?:www\.)?imdb\.com)(?::443)?(?<path>/list/ls[0-9]+|/chart/top(?:tv)?)/?(?:\?ref_=(?:[a-zA-Z0-9._~-]|%[0-9a-fA-F]{2})*)?(?:#[^\x00-\x20\x7f]*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string SourceTag => "IMDb";

    public bool Supports(Uri url) => SupportedPath(url) is not null;

    public Uri GetCanonicalUrl(Uri url) =>
        SupportedPath(url) is { } path ? CanonicalUrl(path) : url;

    public async Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken)
    {
        var path = SupportedPath(url) ?? throw new ArgumentException("Not a supported IMDb source URL.", nameof(url));
        var source = CanonicalUrl(path);
        return path switch
        {
            "/chart/top" => await ReadChartAsync(source, television: false, cancellationToken).ConfigureAwait(false),
            "/chart/toptv" => await ReadChartAsync(source, television: true, cancellationToken).ConfigureAwait(false),
            _ => await ReadListAsync(source, path["/list/".Length..], cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<SourceList> ReadListAsync(Uri source, string listId, CancellationToken ct)
    {
        var kindsById = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<MediaIdentity>();
        string? name = null;
        string? description = null;
        int? total = null;
        var rowsRead = 0;
        for (var page = 1; page <= MaxListPages; page++)
        {
            // Only navigate numbered pages of the same public list. Don't follow URLs from IMDb data.
            var url = page == 1 ? source : new Uri(source, "?page=" + page.ToString(CultureInfo.InvariantCulture));
            var json = await RenderAsync(url, ct).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(json);
                var props = PageProps(document);
                var list = Get(Get(props, "mainColumnData"), "list");
                if (Text(Get(list, "id")) != listId ||
                    Text(Get(Get(list, "listType"), "id")) != "TITLES" ||
                    Text(Get(Get(list, "visibility"), "id")) != "PUBLIC" ||
                    Get(props, "showPrivateListMessaging").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.False))
                {
                    throw new JsonException("Expected the requested public IMDb title list.");
                }

                if (Get(props, "initialPageNumber") is { ValueKind: not JsonValueKind.Undefined } pageNumber &&
                    Count(pageNumber) != page)
                {
                    throw new JsonException("IMDb returned the wrong list page.");
                }

                var search = Get(list, "titleListItemSearch");
                var pageTotal = Count(Get(search, "total"));
                if (page == 1)
                {
                    total = pageTotal;
                    var listName = Get(list, "name");
                    name = CleanText(listName) ?? CleanText(Get(listName, "originalText"));
                    description = CleanText(Get(Get(Get(list, "description"), "originalText"), "plainText"));
                }

                var edges = Edges(search);
                var hasNext = Get(Get(search, "pageInfo"), "hasNextPage").ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new JsonException("Missing or invalid IMDb title list pagination flag.")
                };
                // Check coverage before filtering out episodes or other unsupported types.
                var rows = edges.GetArrayLength();
                var remaining = pageTotal - rowsRead;
                if (pageTotal != total || rows > remaining || hasNext != (rows < remaining) ||
                    (rows == 0 && (page != 1 || pageTotal != 0)))
                {
                    throw new JsonException("Incomplete or inconsistent IMDb title list pagination.");
                }

                foreach (var edge in edges.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    // The same title can occur at different list positions. Positions, not IDs, prove page coverage.
                    if (Count(Get(Get(edge, "node"), "absolutePosition")) != ++rowsRead)
                    {
                        throw new JsonException("IMDb title list positions are not consecutive.");
                    }
                    var (id, kind) = ReadTitle(Get(edge, "listItem"));
                    if (kindsById.TryGetValue(id, out var previousKind))
                    {
                        if (kind != previousKind)
                        {
                            throw new JsonException("IMDb list rows disagree on a title's type.");
                        }
                        continue;
                    }
                    kindsById.Add(id, kind);
                    if (ListMediaType(kind) is { } type)
                    {
                        items.Add(Identity(type, id));
                    }
                }
                if (!hasNext)
                {
                    ct.ThrowIfCancellationRequested();
                    return new(name, items, description);
                }
            }
            catch (JsonException exception)
            {
                ct.ThrowIfCancellationRequested();
                throw new SourceListReadException(source, $"Invalid IMDb title list data on page {page}: {exception.Message}", exception);
            }
        }
        throw new SourceListReadException(source, $"IMDb title list exceeds the limit of {MaxListPages} pages.");
    }

    private async Task<SourceList> ReadChartAsync(Uri source, bool television, CancellationToken ct)
    {
        var json = await RenderAsync(source, ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(json);
            var chart = Get(Get(PageProps(document), "pageData"), "chartTitles");
            var edges = Edges(chart);
            var total = Get(chart, "total");
            var hasNext = Get(Get(chart, "pageInfo"), "hasNextPage");
            if (edges.GetArrayLength() != ChartSize ||
                (total.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && Count(total) != ChartSize) ||
                hasNext.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False))
            {
                throw new JsonException($"Expected a complete IMDb Top {ChartSize} chart.");
            }

            var mediaType = television ? SourceMediaType.Series : SourceMediaType.Movie;
            var items = new List<MediaIdentity>(ChartSize);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in edges.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var (id, kind) = ReadTitle(Get(edge, "node"));
                var expectedType = television ? kind is "tvSeries" or "tvMiniSeries" : kind == "movie";
                if (!expectedType || !ids.Add(id))
                {
                    throw new JsonException("Unexpected title type or duplicate title on the IMDb chart.");
                }
                items.Add(Identity(mediaType, id));
            }
            ct.ThrowIfCancellationRequested();
            return new(television ? "IMDb Top 250 TV Shows" : "IMDb Top 250 Movies", items);
        }
        catch (JsonException exception)
        {
            ct.ThrowIfCancellationRequested();
            throw new SourceListReadException(source, $"Invalid IMDb chart data: {exception.Message}", exception);
        }
    }

    private async Task<string> RenderAsync(Uri url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var json = await renderer.RenderAsync(url, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return json;
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new SourceListReadException(url, "IMDb rendering timed out.", exception);
        }
    }

    private static (string Id, string Kind) ReadTitle(JsonElement title) =>
        (ImdbId.ReadOptional(Get(title, "id"), minimumDigits: 7) ?? throw new JsonException("Missing IMDb title ID."),
         Text(Get(Get(title, "titleType"), "id")) ?? throw new JsonException("Missing IMDb title type."));

    private static SourceMediaType? ListMediaType(string kind) => kind switch
    {
        "movie" or "tvMovie" or "short" => SourceMediaType.Movie,
        "tvSeries" or "tvMiniSeries" => SourceMediaType.Series,
        "tvEpisode" or "tvSpecial" or "tvShort" or "video" or "videoGame" or "musicVideo" or
            "podcastSeries" or "podcastEpisode" => null,
        _ => throw new JsonException("Unrecognized IMDb title type.")
    };

    private static MediaIdentity Identity(SourceMediaType type, string imdbId) =>
        new(type, new Dictionary<string, string> { ["Imdb"] = imdbId });

    private static JsonElement PageProps(JsonDocument document) => Get(Get(document.RootElement, "props"), "pageProps");

    private static JsonElement Edges(JsonElement search) =>
        Get(search, "edges") is { ValueKind: JsonValueKind.Array } edges
            ? edges
            : throw new JsonException("Missing IMDb title rows.");

    private static JsonElement Get(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) ? result : default;

    private static int Count(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0
            ? count
            : throw new JsonException("Missing or invalid IMDb count or position.");

    private static string? Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? CleanText(JsonElement value) =>
        Text(value) is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static string? SupportedPath(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Host is not ("www.imdb.com" or "imdb.com"))
        {
            return null;
        }
        var match = SourceUrl.Match(url.OriginalString);
        return match.Success ? match.Groups["path"].Value : null;
    }

    private static Uri CanonicalUrl(string path) => new(Origin + path + "/");
}
