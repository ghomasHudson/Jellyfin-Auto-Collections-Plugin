using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class MdblistHandler(HttpClient http) : ISourceListHandler
{
    private const RegexOptions HtmlOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Uri Origin = new("https://mdblist.com");

    // MDBList renders the name in this heading, separate from the list owner's heading.
    private static readonly Regex ListHeading = new("""<h3\s+class="textdark"\s*>(.*?)</h3\s*>""", HtmlOptions | RegexOptions.Singleline);
    private static readonly Regex HtmlTag = new("<[^>]*>", HtmlOptions);

    public bool Supports(Uri url) => PublicListPath(url) is not null;

    public async Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken)
    {
        var path = PublicListPath(url) ?? throw new ArgumentException("Not a public MDBList source list URL.", nameof(url));
        var html = await FetchBodyAsync(new Uri(Origin, path), url, cancellationToken).ConfigureAwait(false);
        var json = await FetchBodyAsync(new Uri(Origin, path + "/json"), url, cancellationToken).ConfigureAwait(false);

        try
        {
            return new(ReadName(html), ReadItems(json));
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

    private static string? ReadName(string html)
    {
        var heading = ListHeading.Match(html);
        return heading.Success ? WebUtility.HtmlDecode(HtmlTag.Replace(heading.Groups[1].Value, "")).Trim() : null;
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
            if (entry.TryGetProperty("imdb_id", out var imdb) && imdb.ValueKind != JsonValueKind.Null)
            {
                if (imdb.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("Expected an IMDb ID string.");
                }
                if (imdb.GetString() is { Length: > 0 } imdbId)
                {
                    if (imdbId.Length < 3 || !imdbId.StartsWith("tt", StringComparison.Ordinal) ||
                        imdbId.AsSpan(2).ContainsAnyExceptInRange('0', '9'))
                    {
                        throw new JsonException("Expected an IMDb ID starting with 'tt' followed by digits.");
                    }
                    ids["Imdb"] = imdbId;
                }
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
