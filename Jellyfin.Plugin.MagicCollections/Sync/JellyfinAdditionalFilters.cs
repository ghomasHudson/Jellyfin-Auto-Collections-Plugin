using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal static class JellyfinAdditionalFilters
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

    // This is an allowlist of /Items predicates implemented by the 10.11.5 item repository.
    // Scope, base item types, user context, and selection controls belong to the caller.
    private static readonly Dictionary<string, FilterParser> Parsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mincommunityrating"] = Parse<double>(TryRating, rating => rating.ToString("R", CultureInfo.InvariantCulture),
            (query, rating) => query.MinCommunityRating = rating),
        ["minpremieredate"] = Date((query, instant) => query.MinPremiereDate = instant),
        ["maxpremieredate"] = Date((query, instant) => query.MaxPremiereDate = instant),
        ["mindatelastsaved"] = Date((query, instant) => query.MinDateLastSaved = instant),
        ["officialratings"] = List<string>([',', '|'], TryText, text => text,
            (query, ratings) => query.OfficialRatings = ratings),
        ["hasoverview"] = Boolean((query, value) => query.HasOverview = value),
        ["hasimdbid"] = Boolean((query, value) => query.HasImdbId = value),
        ["hastmdbid"] = Boolean((query, value) => query.HasTmdbId = value),
        ["hastvdbid"] = Boolean((query, value) => query.HasTvdbId = value),
        ["hasofficialrating"] = Boolean((query, value) => query.HasOfficialRating = value),
        ["islocked"] = Boolean((query, value) => query.IsLocked = value),
        ["namestartswith"] = SortName((query, name) => query.NameStartsWith = name),
        ["namestartswithorgreater"] = SortName((query, name) => query.NameStartsWithOrGreater = name),
        ["namelessthan"] = SortName((query, name) => query.NameLessThan = name),
        ["genreids"] = Guids((query, ids) => query.GenreIds = ids),
        ["personids"] = Guids((query, ids) => query.PersonIds = ids),
        ["studioids"] = Guids((query, ids) => query.StudioIds = ids),
        ["imagetypes"] = EnumList<ImageType>((query, types) => query.ImageTypes = types),
        ["videotypes"] = EnumList<VideoType>((query, types) => query.VideoTypes = types),
        ["minwidth"] = Dimension((query, pixels) => query.MinWidth = pixels),
        ["maxwidth"] = Dimension((query, pixels) => query.MaxWidth = pixels),
        ["minheight"] = Dimension((query, pixels) => query.MinHeight = pixels),
        ["maxheight"] = Dimension((query, pixels) => query.MaxHeight = pixels)
    };

    private delegate JellyfinAdditionalFilter? FilterParser(string key, string value);
    private delegate bool ValueParser<T>(string value, out T result);

    internal static bool Supports(string key) => Parsers.ContainsKey(key);

    internal static bool TryParse(string key, string value, [NotNullWhen(true)] out JellyfinAdditionalFilter? filter)
    {
        filter = Parsers.TryGetValue(key, out var parser) && value.Length <= MaxValueLength && !value.Any(char.IsControl)
            ? parser(key.ToLowerInvariant(), value.Trim())
            : null;
        return filter is not null;
    }

    private static FilterParser Parse<T>(ValueParser<T> parse, Func<T, string> format, Action<InternalItemsQuery, T> apply) =>
        (key, value) => parse(value, out var result)
            ? new JellyfinAdditionalFilter(key, format(result), query => apply(query, result))
            : null;

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
        Parse<int>(TryDimension, pixels => pixels.ToString(CultureInfo.InvariantCulture), apply);

    private static bool TryDimension(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

    private static FilterParser SortName(Action<InternalItemsQuery, string> apply) =>
        (key, value) =>
        {
            if (value.Length == 0)
            {
                return null;
            }
            var name = value.ToLowerInvariant();
            return new JellyfinAdditionalFilter(key, name, query => apply(query, name));
        };

    private static FilterParser Guids(Action<InternalItemsQuery, Guid[]> apply) =>
        List<Guid>([','], TryItemId, id => id.ToString("N"), apply);

    private static bool TryItemId(string value, out Guid id) => Guid.TryParse(value, out id) && id != Guid.Empty;

    private static FilterParser EnumList<T>(Action<InternalItemsQuery, T[]> apply) where T : struct, Enum
    {
        var names = Enum.GetValues<T>().ToDictionary(value => value.ToString(), StringComparer.OrdinalIgnoreCase);
        return List<T>([','], names.TryGetValue, value => value.ToString(), apply);
    }

    private static FilterParser List<T>(char[] separators, ValueParser<T> parse, Func<T, string> format,
        Action<InternalItemsQuery, T[]> apply) =>
        (key, value) =>
        {
            var parts = value.Split(separators, MaxListItems + 1, StringSplitOptions.TrimEntries);
            if (parts.Length > MaxListItems)
            {
                return null;
            }

            var values = new SortedDictionary<string, T>(StringComparer.Ordinal);
            foreach (var part in parts)
            {
                if (part.Length == 0 || !parse(part, out var item))
                {
                    return null;
                }
                values.TryAdd(format(item), item);
            }

            var canonical = string.Join(',', values.Keys);
            var items = values.Values.ToArray();
            // Keep the parsed value independent of each mutable query.
            return new JellyfinAdditionalFilter(key, canonical, query => apply(query, items.ToArray()));
        };

    private static bool TryText(string value, out string result)
    {
        result = value;
        return value.Length > 0;
    }
}

internal sealed class JellyfinAdditionalFilter(string key, string canonicalValue, Action<InternalItemsQuery> apply)
{
    internal string Key { get; } = key;
    internal string CanonicalValue { get; } = canonicalValue;

    internal void Apply(InternalItemsQuery query) => apply(query);
}
