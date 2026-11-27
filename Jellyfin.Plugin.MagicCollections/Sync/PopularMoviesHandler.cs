using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class PopularMoviesHandler(HttpClient http, TimeProvider timeProvider, string dataPath) : ISourceListHandler
{
    private const string Host = "popular-movies-data.stevenlu.com";
    private const string Origin = "https://" + Host;
    // The JSON-only feeds have no favicon; use the publisher's site icon.
    private static readonly Uri PublisherFavicon = new("https://stevenlu.com/images/avatar.webp");
    private static readonly IReadOnlyDictionary<string, string> FeedNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/movies.json"] = "Steven Lu Popular Movies",
        ["/all-movies.json"] = "Steven Lu All Movies",
        ["/movies-metacritic-min50.json"] = "Steven Lu Movies (Metacritic 50+)",
        ["/movies-metacritic-min60.json"] = "Steven Lu Movies (Metacritic 60+)",
        ["/movies-metacritic-min70.json"] = "Steven Lu Movies (Metacritic 70+)",
        ["/movies-metacritic-min80.json"] = "Steven Lu Movies (Metacritic 80+)",
        ["/movies-imdb-min5.json"] = "Steven Lu Movies (IMDb 5+)",
        ["/movies-imdb-min6.json"] = "Steven Lu Movies (IMDb 6+)",
        ["/movies-imdb-min7.json"] = "Steven Lu Movies (IMDb 7+)",
        ["/movies-imdb-min8.json"] = "Steven Lu Movies (IMDb 8+)",
        ["/movies-rottentomatoes-min50.json"] = "Steven Lu Movies (Rotten Tomatoes 50+)",
        ["/movies-rottentomatoes-min60.json"] = "Steven Lu Movies (Rotten Tomatoes 60+)",
        ["/movies-rottentomatoes-min70.json"] = "Steven Lu Movies (Rotten Tomatoes 70+)",
        ["/movies-rottentomatoes-min80.json"] = "Steven Lu Movies (Rotten Tomatoes 80+)"
    };

    private readonly PopularMoviesFeedCache _cache = new(dataPath, timeProvider);

    public bool Supports(Uri url) => FeedName(url) is not null;

    public async Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken)
    {
        var name = FeedName(url) ?? throw new ArgumentException("Not a Steven Lu Popular Movies feed URL.", nameof(url));
        var canonicalUrl = new Uri(Origin + url.AbsolutePath);
        var items = await _cache.ReadAsync(canonicalUrl, FetchAsync, cancellationToken).ConfigureAwait(false);
        return new(name, items) { FaviconUrls = [PublisherFavicon] };
    }

    private async Task<IReadOnlyList<MediaIdentity>> FetchAsync(Uri url)
    {
        try
        {
            var json = await http.GetStringAsync(url).ConfigureAwait(false);
            return ReadItems(json);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new SourceListReadException(url, $"Steven Lu Popular Movies fetch failed: {exception.Message}", exception);
        }
        catch (OperationCanceledException exception)
        {
            throw new SourceListReadException(url, "Steven Lu Popular Movies fetch timed out.", exception);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new SourceListReadException(url, $"Invalid Steven Lu Popular Movies data: {exception.Message}", exception);
        }
    }

    private static List<MediaIdentity> ReadItems(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Expected an array of movies.");
        }

        var items = new List<MediaIdentity>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Expected a movie object.");
            }

            JsonElement imdb = default, tmdb = default;
            foreach (var property in entry.EnumerateObject())
            {
                if (property.NameEquals("imdb_id"))
                {
                    ReadProviderField(property, ref imdb);
                }
                else if (property.NameEquals("tmdb_id"))
                {
                    ReadProviderField(property, ref tmdb);
                }
            }

            var ids = new Dictionary<string, string>();
            if (ImdbId.ReadOptional(imdb, minimumDigits: 7) is { } imdbId)
            {
                ids["Imdb"] = imdbId;
            }
            if (tmdb.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                if (tmdb.ValueKind != JsonValueKind.Number || !tmdb.TryGetInt32(out var id) || id <= 0)
                {
                    throw new JsonException("Expected a positive 32-bit integer TMDb ID.");
                }
                ids["Tmdb"] = id.ToString(CultureInfo.InvariantCulture);
            }
            if (ids.Count == 0)
            {
                throw new JsonException("Expected at least one provider ID for each movie.");
            }
            items.Add(new(SourceMediaType.Movie, ids));
        }
        return items;
    }

    private static void ReadProviderField(JsonProperty property, ref JsonElement field)
    {
        if (field.ValueKind != JsonValueKind.Undefined)
        {
            throw new JsonException($"Duplicate provider ID field '{property.Name}'.");
        }
        field = property.Value;
    }

    private static string? FeedName(Uri url)
    {
        // Check the original spelling of the path too: Uri normalizes dot segments, escapes and backslashes.
        return url.IsAbsoluteUri && url.Host == Host &&
            url.OriginalString.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) &&
            url.OriginalString.AsSpan(Origin.Length).SequenceEqual(url.AbsolutePath) &&
            FeedNames.TryGetValue(url.AbsolutePath, out var name) ? name : null;
    }
}
