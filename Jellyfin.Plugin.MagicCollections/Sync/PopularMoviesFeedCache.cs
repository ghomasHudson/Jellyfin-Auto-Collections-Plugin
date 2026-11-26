using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal sealed class PopularMoviesFeedCache(string dataPath, TimeProvider timeProvider)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(24);
    private readonly string _directory = Path.Combine(dataPath, "magic-collections", "popular-movies");
    private readonly ConcurrentDictionary<string, FeedPoll> _polls = new(StringComparer.Ordinal);

    // The handler passes only canonical URLs from its allowlist, so their paths are safe basenames.
    public Task<IReadOnlyList<MediaIdentity>> ReadAsync(Uri feedUrl,
        Func<Uri, Task<IReadOnlyList<MediaIdentity>>> fetch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = feedUrl.AbsolutePath[1..];
        var poll = _polls.GetOrAdd(fileName, static _ => new());
        Task<IReadOnlyList<MediaIdentity>> result;
        lock (poll)
        {
            if (poll.InFlight is null || poll.InFlight.IsCompleted)
            {
                poll.InFlight = ReadOrFetchAsync(feedUrl, Path.Combine(_directory, fileName), fetch);
            }
            result = poll.InFlight;
        }

        // A cancelled sync stops waiting, but the shared poll can still persist its outcome.
        return result.WaitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<MediaIdentity>> ReadOrFetchAsync(Uri url, string path,
        Func<Uri, Task<IReadOnlyList<MediaIdentity>>> fetch)
    {
        try
        {
            var previous = Load(path);
            var now = timeProvider.GetUtcNow();
            if (previous is not null && now - previous.StartedAt < PollInterval)
            {
                if (previous.Items is { } items)
                {
                    return items;
                }

                throw new SourceListReadException(url,
                    previous.Failure ?? "The previous Steven Lu Popular Movies fetch was interrupted.");
            }

            // Commit the attempt before contacting upstream. If Jellyfin stops before the
            // outcome is committed, this reservation still prevents an early retry.
            var record = new PollRecord(now, null, null);
            Save(path, record);

            IReadOnlyList<MediaIdentity> fetched;
            try
            {
                fetched = await fetch(url).ConfigureAwait(false);
            }
            catch (SourceListReadException exception)
            {
                Save(path, record with { Failure = exception.Reason });
                throw;
            }

            Save(path, record with { Items = fetched });
            return fetched;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Never bypass the ceiling by polling when the durable state cannot be read or reserved.
            throw new SourceListReadException(url, $"Could not access Steven Lu Popular Movies poll state: {exception.Message}", exception);
        }
    }

    private static PollRecord? Load(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return JsonSerializer.Deserialize<PollRecord>(file) ?? throw new JsonException("Missing poll record.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private void Save(string path, PollRecord record)
    {
        Directory.CreateDirectory(_directory);
        // One bounded temporary filename per feed, on the same filesystem as the record.
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, record);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record PollRecord([property: JsonRequired] DateTimeOffset StartedAt,
        IReadOnlyList<MediaIdentity>? Items, string? Failure);

    private sealed class FeedPoll
    {
        public Task<IReadOnlyList<MediaIdentity>>? InFlight { get; set; }
    }
}
