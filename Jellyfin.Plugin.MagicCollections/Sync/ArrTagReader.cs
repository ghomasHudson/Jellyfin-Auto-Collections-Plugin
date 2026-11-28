using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal static class ArrTagReader
{
    public static async Task<SourceList> ReadAsync(HttpClient http, ArrTagSource source, CancellationToken cancellationToken)
    {
        try
        {
            using var tags = await ReadArrayAsync("api/v3/tag").ConfigureAwait(false);
            int? tagId = null;
            var tagIds = new HashSet<int>();
            var labels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in tags.RootElement.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.Object || !tag.TryGetProperty("id", out var id) ||
                    !id.TryReadPositiveInt(out var value) || !tag.TryGetProperty("label", out var property) ||
                    property.ValueKind != JsonValueKind.String || ReadString(property) is not { } label ||
                    string.IsNullOrWhiteSpace(label) || label.Any(char.IsControl) || !tagIds.Add(value) || !labels.Add(label))
                {
                    throw new JsonException();
                }

                if (label == source.Label)
                {
                    tagId = value;
                }
            }
            if (tagId is null)
            {
                throw ReadError("Tag does not exist.");
            }

            var (endpoint, mediaType, primaryField, primaryProvider) = source.Service switch
            {
                ArrService.Radarr => ("api/v3/movie", SourceMediaType.Movie, "tmdbId", "Tmdb"),
                ArrService.Sonarr => ("api/v3/series", SourceMediaType.Series, "tvdbId", "Tvdb"),
                _ => throw new InvalidOperationException("Unsupported *arr service.")
            };
            using var media = await ReadArrayAsync(endpoint).ConfigureAwait(false);
            var items = new List<MediaIdentity>();
            foreach (var row in media.RootElement.EnumerateArray())
            {
                if (HasTag(row, tagId.Value))
                {
                    var providers = new Dictionary<string, string>();
                    row.TryGetProperty("imdbId", out var imdb);
                    if (ReadImdbId(imdb) is { } imdbId)
                    {
                        providers["Imdb"] = imdbId;
                    }
                    var suppliedPrimaryId = AddNumericId(row, primaryField, primaryProvider, providers);
                    if (source.Service == ArrService.Sonarr)
                    {
                        // Older Sonarr versions do not include a TMDb ID.
                        AddNumericId(row, "tmdbId", "Tmdb", providers);
                    }
                    if (!suppliedPrimaryId && providers.Count == 0)
                    {
                        throw new JsonException();
                    }
                    if (providers.Count != 0)
                    {
                        items.Add(new(mediaType, providers));
                    }
                }
            }
            return new(source.Label, items);
        }
        catch (JsonException)
        {
            throw ReadError("Invalid or incomplete API response.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw ReadError("API request failed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ReadError("API request timed out.");
        }

        SourceListReadException ReadError(string reason, HttpStatusCode? status = null) =>
            new(source.BaseUrl, $"{source.Service}: {reason}") { StatusCode = status };

        async Task<JsonDocument> ReadArrayAsync(string endpoint)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(source.BaseUrl, endpoint));
            request.Headers.Add("X-Api-Key", source.ApiKey);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw ReadError(FormattableString.Invariant($"API returned HTTP {(int)response.StatusCode}."), response.StatusCode);
            }
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var data = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (data.RootElement.ValueKind != JsonValueKind.Array)
            {
                data.Dispose();
                throw new JsonException();
            }
            return data;
        }
    }

    // JsonDocument parses escapes lazily; extracting a string with invalid Unicode can throw
    // InvalidOperationException rather than JsonException.
    private static string? ReadString(JsonElement value) => ReadJsonValue(value, element => element.GetString());

    private static string? ReadImdbId(JsonElement value) =>
        ReadJsonValue(value, element => ImdbId.ReadOptional(element, minimumDigits: 7));

    private static string? ReadJsonValue(JsonElement value, Func<JsonElement, string?> read)
    {
        try
        {
            return read(value);
        }
        catch (InvalidOperationException)
        {
            throw new JsonException();
        }
    }

    private static bool HasTag(JsonElement row, int tagId)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("tags", out var tags) ||
            tags.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException();
        }

        var match = false;
        foreach (var tag in tags.EnumerateArray())
        {
            if (!tag.TryReadPositiveInt(out var id))
            {
                throw new JsonException();
            }
            match |= id == tagId;
        }
        return match;
    }

    private static bool AddNumericId(JsonElement row, string field, string provider, Dictionary<string, string> providers)
    {
        if (!row.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || id < 0)
        {
            throw new JsonException();
        }
        if (id > 0)
        {
            providers[provider] = id.ToString(CultureInfo.InvariantCulture);
        }
        return true;
    }

    private static bool TryReadPositiveInt(this JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && value > 0;
    }
}
