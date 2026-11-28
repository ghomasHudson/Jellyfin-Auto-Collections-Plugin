using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal static class ArrTagReader
{
    public static async Task<SourceList> ReadAsync(HttpClient http, ArrTagSource source,
        ArrServiceDefinition definition, CancellationToken cancellationToken)
    {
        try
        {
            using var tags = await ReadArrayAsync("api/v3/tag").ConfigureAwait(false);
            int? tagId = null;
            var tagIds = new HashSet<int>();
            var labels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in tags.RootElement.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.Object || !TryGetUniqueProperty(tag, "id", out var id) ||
                    !id.TryReadPositiveInt(out var value) || !TryGetUniqueProperty(tag, "label", out var property) ||
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

            using var media = await ReadArrayAsync(definition.MediaEndpoint).ConfigureAwait(false);
            var items = new List<MediaIdentity>();
            foreach (var row in media.RootElement.EnumerateArray())
            {
                if (HasTag(row, tagId.Value))
                {
                    var providers = new Dictionary<string, string>();
                    TryGetUniqueProperty(row, "imdbId", out var imdb);
                    if (ReadImdbId(imdb) is { } imdbId)
                    {
                        providers["Imdb"] = imdbId;
                    }
                    var primaryId = ReadNumericId(row, definition.PrimaryIdField);
                    AddAvailableId(primaryId, definition.PrimaryProvider, providers);
                    if (definition.OptionalTmdbIdField is { } optionalTmdbField)
                    {
                        // Older Sonarr versions do not include a TMDb ID.
                        AddAvailableId(ReadNumericId(row, optionalTmdbField), "Tmdb", providers);
                    }
                    if (primaryId is null && providers.Count == 0)
                    {
                        throw new JsonException();
                    }
                    if (providers.Count != 0)
                    {
                        items.Add(new(definition.MediaType, providers));
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
            new(source.BaseUrl, $"{definition.Service}: {reason}") { StatusCode = status };

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
        if (row.ValueKind != JsonValueKind.Object || !TryGetUniqueProperty(row, "tags", out var tags))
        {
            throw new JsonException();
        }
        if (tags.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        if (tags.ValueKind != JsonValueKind.Array)
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

    // Zero is an explicitly unavailable ID, distinct from a missing field.
    private static int? ReadNumericId(JsonElement row, string field)
    {
        if (!TryGetUniqueProperty(row, field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || id < 0)
        {
            throw new JsonException();
        }
        return id;
    }

    private static void AddAvailableId(int? id, string provider, Dictionary<string, string> providers)
    {
        if (id is > 0)
        {
            providers[provider] = id.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    // JsonElement.TryGetProperty silently picks the last value. Reject ambiguous fields used for membership.
    private static bool TryGetUniqueProperty(JsonElement row, string name, out JsonElement value)
    {
        value = default;
        foreach (var property in row.EnumerateObject())
        {
            if (!property.NameEquals(name))
            {
                continue;
            }
            if (value.ValueKind != JsonValueKind.Undefined)
            {
                throw new JsonException();
            }
            value = property.Value;
        }
        return value.ValueKind != JsonValueKind.Undefined;
    }

    private static bool TryReadPositiveInt(this JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && value > 0;
    }
}
