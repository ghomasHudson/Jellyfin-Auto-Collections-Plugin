using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal static class JellyfinNativeFilters
{
    private const int MaxValueLength = 4096;
    private const int MaxListItems = 100;
    private static readonly string[] IsoDateFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm'Z'",
        "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    // Ordinary /Items predicates that can be applied directly to the 10.11.5 item query.
    private static readonly Dictionary<string, FilterParser> Parsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["genres"] = MetadataNames((query, names) => query.Genres = names),
        ["tags"] = MetadataNames((query, names) => query.Tags = names),
        ["years"] = List<int>(JellyfinQueryValues.TryPositiveInt, year => year.ToString(CultureInfo.InvariantCulture),
            (query, years) => query.Years = years),
        ["person"] = Parse<string>(TryText, text => text, (query, name) => query.Person = name),
        ["searchterm"] = Parse<string>(TrySearchTerm, text => text, (query, term) => query.SearchTerm = term),
        ["mincommunityrating"] = Parse<double>(TryRating, rating => rating.ToString("R", CultureInfo.InvariantCulture),
            (query, rating) => query.MinCommunityRating = rating),
        ["minpremieredate"] = Date((query, instant) => query.MinPremiereDate = instant),
        ["maxpremieredate"] = Date((query, instant) => query.MaxPremiereDate = instant),
        ["mindatelastsaved"] = Date((query, instant) => query.MinDateLastSaved = instant),
        ["officialratings"] = List<string>(TryText, text => text,
            (query, ratings) => query.OfficialRatings = ratings, allowEncodedPipe: true),
        ["hasoverview"] = Boolean((query, value) => query.HasOverview = value),
        ["hasimdbid"] = Boolean((query, value) => query.HasImdbId = value),
        ["hastmdbid"] = Boolean((query, value) => query.HasTmdbId = value),
        ["hastvdbid"] = Boolean((query, value) => query.HasTvdbId = value),
        ["hasofficialrating"] = Boolean((query, value) => query.HasOfficialRating = value),
        ["islocked"] = Boolean((query, value) => query.IsLocked = value),
        ["namestartswith"] = SortName((query, name) => query.NameStartsWith = name),
        ["namestartswithorgreater"] = SortName((query, name) => query.NameStartsWithOrGreater = name),
        ["namelessthan"] = SortName((query, name) => query.NameLessThan = name),
        ["imagetypes"] = EnumList<ImageType>((query, types) => query.ImageTypes = types),
        ["videotypes"] = EnumList<VideoType>((query, types) => query.VideoTypes = types),
        ["minwidth"] = Dimension((query, pixels) => query.MinWidth = pixels),
        ["maxwidth"] = Dimension((query, pixels) => query.MaxWidth = pixels),
        ["minheight"] = Dimension((query, pixels) => query.MinHeight = pixels),
        ["maxheight"] = Dimension((query, pixels) => query.MaxHeight = pixels)
    };

    private delegate JellyfinNativeFilter? FilterParser(string key, string encoded);
    private delegate bool ValueParser<T>(string value, out T result);

    internal static bool Supports(string key) => Parsers.ContainsKey(key);

    internal static bool TryParse(string key, string encoded, [NotNullWhen(true)] out JellyfinNativeFilter? filter)
    {
        // One encoded UTF-8 byte takes three characters per escape, up to four bytes per code point.
        filter = encoded.Length <= MaxValueLength * 12 && Parsers.TryGetValue(key, out var parser)
            ? parser(key.ToLowerInvariant(), encoded)
            : null;
        return filter is not null;
    }

    private static FilterParser Parse<T>(ValueParser<T> parse, Func<T, string> format, Action<InternalItemsQuery, T> apply) =>
        (key, encoded) => JellyfinQueryValues.DecodeValue(encoded) is { Length: > 0 and <= MaxValueLength } value &&
            parse(value, out var result)
            ? new JellyfinNativeFilter(key, Uri.EscapeDataString(format(result)), query => apply(query, result))
            : null;

    private static FilterParser MetadataNames(Action<InternalItemsQuery, string[]> apply) =>
        List<string>(TryMetadataName, JellyfinQueryValues.CleanMetadataName, apply);

    private static bool TryMetadataName(string name, out string result)
    {
        result = name;
        return !string.IsNullOrWhiteSpace(JellyfinQueryValues.CleanMetadataName(name));
    }

    private static bool TrySearchTerm(string value, out string result)
    {
        // Jellyfin also matches OriginalTitle, which does not remove accents.
        result = value.All(char.IsAscii) ? value.ToLowerInvariant() : value;
        return !string.IsNullOrWhiteSpace(JellyfinQueryValues.CleanMetadataName(value));
    }

    private static bool TryRating(string value, out double rating) =>
        double.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture,
            out rating) && rating is >= 0 and <= 10;

    private static FilterParser Date(Action<InternalItemsQuery, DateTime> apply) =>
        Parse<DateTimeOffset>(TryDate, date => date.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            (query, date) => apply(query, date.UtcDateTime));

    private static bool TryDate(string value, out DateTimeOffset date) =>
        DateTimeOffset.TryParseExact(value, IsoDateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);

    private static FilterParser Boolean(Action<InternalItemsQuery, bool> apply) =>
        Parse<bool>(bool.TryParse, value => value ? "true" : "false", apply);

    private static FilterParser Dimension(Action<InternalItemsQuery, int> apply) =>
        Parse<int>(JellyfinQueryValues.TryPositiveInt, pixels => pixels.ToString(CultureInfo.InvariantCulture), apply);

    private static FilterParser SortName(Action<InternalItemsQuery, string> apply) =>
        Parse<string>(TryText, name => name.ToLowerInvariant(), (query, name) => apply(query, name.ToLowerInvariant()));

    private static FilterParser EnumList<T>(Action<InternalItemsQuery, T[]> apply) where T : struct, Enum
    {
        var names = Enum.GetValues<T>().ToDictionary(value => value.ToString(), StringComparer.OrdinalIgnoreCase);
        return List<T>(names.TryGetValue, value => value.ToString(), apply);
    }

    private static FilterParser List<T>(ValueParser<T> parse, Func<T, string> format, Action<InternalItemsQuery, T[]> apply,
        bool allowEncodedPipe = false) =>
        (key, encoded) =>
        {
            if (JellyfinQueryValues.DecodeList(encoded, MaxListItems, allowEncodedPipe) is not { } parts ||
                parts.Sum(part => part.Length) + parts.Length - 1 > MaxValueLength)
            {
                return null;
            }

            var values = new SortedDictionary<string, T>(StringComparer.Ordinal);
            foreach (var part in parts)
            {
                if (!parse(part, out var item))
                {
                    return null;
                }
                values.TryAdd(format(item), item);
            }

            var canonical = JellyfinQueryValues.EncodeList(values.Keys);
            var items = values.Values.ToArray();
            // Keep the parsed value independent of each mutable query.
            return new JellyfinNativeFilter(key, canonical, query => apply(query, items.ToArray()));
        };

    private static bool TryText(string value, out string result)
    {
        result = value;
        return value.Length > 0;
    }
}

internal sealed class JellyfinNativeFilter(string key, string canonicalValue, Action<InternalItemsQuery> apply)
{
    internal string Key { get; } = key;
    internal string CanonicalValue { get; } = canonicalValue;

    internal void Apply(InternalItemsQuery query) => apply(query);
}
