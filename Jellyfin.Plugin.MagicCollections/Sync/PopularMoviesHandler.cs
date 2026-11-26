using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class PopularMoviesHandler(HttpClient http, TimeProvider timeProvider) : ISourceListHandler
{
    private const string Host = "popular-movies-data.stevenlu.com";
    private const string Origin = "https://" + Host;
    private static readonly TimeSpan PollInterval = TimeSpan.FromDays(1);

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

    private readonly ConcurrentDictionary<string, FeedPoll> _polls = new(StringComparer.Ordinal);

    public bool Supports(Uri url) => FeedName(url) is not null;

    public Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken)
    {
        var name = FeedName(url) ?? throw new ArgumentException("Not a Steven Lu Popular Movies feed URL.", nameof(url));
        cancellationToken.ThrowIfCancellationRequested();
        var poll = _polls.GetOrAdd(url.AbsolutePath, static _ => new());
        Task<SourceList> result;
        lock (poll)
        {
            // Reuse success and failure alike. An aborted sync stops waiting, but must not open a retry window.
            if (poll.Result is null || (poll.Result.IsCompleted && timeProvider.GetElapsedTime(poll.StartedAt) >= PollInterval))
            {
                poll.StartedAt = timeProvider.GetTimestamp();
                poll.Result = FetchAsync(new Uri(Origin + url.AbsolutePath), name);
            }
            result = poll.Result;
        }

        return result.WaitAsync(cancellationToken);
    }

    private async Task<SourceList> FetchAsync(Uri url, string name)
    {
        try
        {
            var json = await http.GetStringAsync(url).ConfigureAwait(false);
            return new(name, ReadItems(json));
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

            var ids = new Dictionary<string, string>();
            if (entry.TryGetProperty("imdb_id", out var imdb) && imdb.ValueKind != JsonValueKind.Null)
            {
                if (imdb.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("Expected an IMDb ID string.");
                }
                if (imdb.GetString() is { Length: > 0 } id)
                {
                    if (id.Length < 3 || !id.StartsWith("tt", StringComparison.Ordinal) ||
                        id.AsSpan(2).ContainsAnyExceptInRange('0', '9'))
                    {
                        throw new JsonException("Expected an IMDb ID starting with 'tt' followed by digits.");
                    }
                    ids["Imdb"] = id;
                }
            }
            if (entry.TryGetProperty("tmdb_id", out var tmdb) && tmdb.ValueKind != JsonValueKind.Null)
            {
                if (tmdb.ValueKind != JsonValueKind.Number || !tmdb.TryGetInt64(out var id) || id <= 0)
                {
                    throw new JsonException("Expected a positive integer TMDb ID.");
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

    private static string? FeedName(Uri url)
    {
        // Check the original spelling of the path too: Uri normalizes dot segments, escapes and backslashes.
        return url.IsAbsoluteUri && url.Host == Host &&
            url.OriginalString.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) &&
            url.OriginalString.AsSpan(Origin.Length).SequenceEqual(url.AbsolutePath) &&
            FeedNames.TryGetValue(url.AbsolutePath, out var name) ? name : null;
    }

    private sealed class FeedPoll
    {
        public long StartedAt { get; set; }
        public Task<SourceList>? Result { get; set; }
    }
}
