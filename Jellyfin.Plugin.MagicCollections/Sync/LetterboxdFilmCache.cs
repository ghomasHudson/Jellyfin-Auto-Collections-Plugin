using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal sealed class LetterboxdFilmCache(string dataPath)
{
    // Jellyfin periodically deletes old files under CachePath. Keep these IDs in DataPath.
    private readonly string _directory = Path.Combine(dataPath, "magic-collections", "letterboxd-films-v1");
    // Serialize detail fetches and clearing so a fetch can't write an old result after a clear.
    private readonly SemaphoreSlim _gate = new(1);

    public async Task<MediaIdentity?> ReadOrFetchAsync(string filmPath, Func<Task<MediaIdentity?>> fetch,
        CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filmPath)));
        var path = Path.Combine(_directory, key + ".json");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await TryReadAsync(path, cancellationToken).ConfigureAwait(false) is { } cached)
            {
                return cached;
            }

            var identity = await fetch().ConfigureAwait(false);
            if (identity is not null)
            {
                var entry = new Entry(identity.Type, identity.ProviderIds["Tmdb"], identity.ProviderIds.GetValueOrDefault("Imdb"));
                await TryWriteAsync(path, entry, cancellationToken).ConfigureAwait(false);
            }
            return identity;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Clearing an empty cache succeeds too.
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<MediaIdentity?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var file = File.OpenRead(path);
            var entry = await JsonSerializer.DeserializeAsync<Entry>(file, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (entry is null || entry.Type is not (SourceMediaType.Movie or SourceMediaType.Series) ||
                entry.TmdbId is not { Length: > 0 } tmdbId || tmdbId[0] == '0' ||
                tmdbId.AsSpan().ContainsAnyExceptInRange('0', '9'))
            {
                return null;
            }
            var ids = new Dictionary<string, string> { ["Tmdb"] = tmdbId };
            if (entry.ImdbId is { } imdbId)
            {
                if (imdbId.Length < 9 || !imdbId.StartsWith("tt", StringComparison.Ordinal) ||
                    imdbId.AsSpan(2).ContainsAnyExceptInRange('0', '9'))
                {
                    return null;
                }
                ids["Imdb"] = imdbId;
            }
            return new(entry.Type, ids);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing, unreadable or corrupt entry is a cache miss.
            return null;
        }
    }

    private async Task TryWriteAsync(string path, Entry entry, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var temporary = Path.Combine(_directory, $"{Guid.NewGuid():N}.tmp");
            try
            {
                using (var file = File.Create(temporary))
                {
                    await JsonSerializer.SerializeAsync(file, entry, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache write failure does not invalidate verified IDs from Letterboxd.
        }
    }

    private sealed record Entry([property: JsonRequired] SourceMediaType Type, string? TmdbId, string? ImdbId);
}
