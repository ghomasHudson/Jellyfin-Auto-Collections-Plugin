using System.Text.Json;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal static class ImdbId
{
    public static string? ReadOptional(JsonElement value, int minimumDigits)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Expected an IMDb ID string.");
        }
        if (value.GetString() is not { Length: > 0 } id)
        {
            return null;
        }
        if (id.Length < minimumDigits + 2 || !id.StartsWith("tt", StringComparison.Ordinal) ||
            id.AsSpan(2).ContainsAnyExceptInRange('0', '9'))
        {
            throw new JsonException("Expected an IMDb ID starting with 'tt' followed by digits.");
        }
        return id;
    }
}
