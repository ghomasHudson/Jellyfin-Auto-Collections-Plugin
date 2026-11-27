using System.Globalization;
using System.Text.Json;
using AngleSharp.Html.Parser;

namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class MdblistHandler(HttpClient http) : ISourceListHandler
{
    private static readonly Uri Origin = new("https://mdblist.com");

    public bool Supports(Uri url) => PublicListPath(url) is not null;

    public async Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken)
    {
        var path = PublicListPath(url) ?? throw new ArgumentException("Not a public MDBList source list URL.", nameof(url));
        var pageUrl = new Uri(Origin, path);
        var html = await FetchBodyAsync(pageUrl, url, cancellationToken).ConfigureAwait(false);
        var json = await FetchBodyAsync(new Uri(Origin, path + "/json"), url, cancellationToken).ConfigureAwait(false);

        try
        {
            using var page = await new HtmlParser().ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
            // The description sits beside the list heading, separate from the individual cards' synopses.
            var heading = page.QuerySelector("h3.textdark");
            var description = heading?.NextElementSibling is { } sibling && sibling.Matches("span.text.wrap")
                ? sibling
                : null;
            return new(heading?.TextContent.Trim(), ReadItems(json), ListDescription.Read(description))
            {
                FaviconUrls = SourceFavicons.FromPage(page, pageUrl)
            };
        }
        catch (JsonException exception)
        {
            throw new SourceListReadException(url, $"Invalid MDBList source list data: {exception.Message}", exception);
        }
    }

    private async Task<string> FetchBodyAsync(Uri endpoint, Uri sourceListUrl, CancellationToken cancellationToken)
    {
        try
        {
            return await http.GetStringAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new SourceListReadException(sourceListUrl, $"MDBList fetch failed: {exception.Message}", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceListReadException(sourceListUrl, "MDBList fetch timed out.", exception);
        }
    }

    private static List<MediaIdentity> ReadItems(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Expected an array of entries.");
        }

        var items = new List<MediaIdentity>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("mediatype", out var mediaType) || mediaType.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("Expected an entry with a media type.");
            }
            var type = mediaType.GetString() switch
            {
                "movie" => SourceMediaType.Movie,
                "show" => SourceMediaType.Series,
                _ => throw new JsonException("Unrecognized MDBList media type.")
            };

            var ids = new Dictionary<string, string>();
            // MDBList's public export uses "id" for TMDb, not its own list or item identifier.
            AddNumericId(entry, "id", "Tmdb", ids);
            AddNumericId(entry, "tvdbid", "Tvdb", ids);
            entry.TryGetProperty("imdb_id", out var imdb);
            if (ImdbId.ReadOptional(imdb, minimumDigits: 1) is { } imdbId)
            {
                ids["Imdb"] = imdbId;
            }
            items.Add(new(type, ids));
        }
        return items;
    }

    private static string? PublicListPath(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("https" or "http") ||
            url.Host is not ("mdblist.com" or "www.mdblist.com") ||
            !url.IsDefaultPort || url.UserInfo.Length != 0)
        {
            return null;
        }

        var path = url.AbsolutePath.TrimEnd('/');
        return path.Split('/') switch
        {
            // The interactive chart's JSON does not represent the chart shown on the page.
            ["", "lists", "official", "movies" or "shows", var slug]
                when Uri.UnescapeDataString(slug) == "justwatch-streaming-charts" => null,
            ["", "lists", var owner, var slug] when IsValidListPathSegment(owner) && IsValidListPathSegment(slug) => path,
            ["", "lists", "official", "movies" or "shows", var slug] when IsValidListPathSegment(slug) => path,
            _ => null
        };
    }

    private static bool IsValidListPathSegment(string segment)
    {
        var decoded = Uri.UnescapeDataString(segment);
        return decoded.Length > 0 && decoded is not ("." or "..") &&
            !decoded.Any(c => char.IsControl(c) || c is '/' or '\\' or '%' or '?' or '#');
    }

    private static void AddNumericId(JsonElement entry, string field, string provider, Dictionary<string, string> ids)
    {
        if (!entry.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id < 0)
        {
            throw new JsonException($"Expected an integer provider ID in '{field}'.");
        }
        if (id > 0)
        {
            ids[provider] = id.ToString(CultureInfo.InvariantCulture);
        }
    }
}
