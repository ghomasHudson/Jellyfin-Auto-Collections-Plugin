using System.Globalization;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal sealed class JellyfinQuery
{
    private const string Prefix = "jellyfin/";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly List<JellyfinAdditionalFilter> _additionalFilters = [];

    public double? MinCriticRating { get; private set; }
    public string[] Tags { get; private set; } = [];
    public string[] Genres { get; private set; } = [];
    public string? Person { get; private set; }
    public string? SearchTerm { get; private set; }
    public string[] Studios { get; private set; } = [];
    public int[] Years { get; private set; } = [];
    public BaseItemKind[] ItemTypes { get; private set; } = [BaseItemKind.Movie, BaseItemKind.Series];
    public SeriesStatus[] SeriesStatuses { get; private set; } = [];
    public Guid? ParentId { get; private set; }
    public int? Limit { get; private set; }
    public bool Random { get; private set; }
    public bool? HasSubtitles { get; private set; }
    public bool? Is3D { get; private set; }
    public string? Name { get; private set; }
    public string? Description { get; private set; }
    public IReadOnlyList<JellyfinAdditionalFilter> AdditionalFilters => _additionalFilters;

    public static bool HasPrefix(string? source) =>
        source?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true;

    public string CanonicalSource
    {
        get
        {
            // Fixed key order, with defaults omitted. Only selection parameters contribute.
            var pairs = new List<string>();
            void Add(string key, string value) => pairs.Add($"{key}={Uri.EscapeDataString(value)}");
            if (Genres.Length > 0) Add("genres", string.Join(',', Genres));
            if (HasSubtitles is { } hasSubtitles) Add("hassubtitles", hasSubtitles ? "true" : "false");
            if (ItemTypes.Length != 2) Add("includeitemtypes", ItemTypes[0].ToString());
            if (Is3D is { } is3D) Add("is3d", is3D ? "true" : "false");
            if (Limit is { } limit) Add("limit", limit.ToString(CultureInfo.InvariantCulture));
            if (MinCriticRating is { } rating) Add("mincriticrating", rating.ToString(CultureInfo.InvariantCulture));
            if (ParentId is { } parent) Add("parentid", parent.ToString("N"));
            if (Person is not null) Add("person", Person);
            if (SearchTerm is not null) Add("searchterm", SearchTerm);
            if (SeriesStatuses.Length > 0) Add("seriesstatus", string.Join(',', SeriesStatuses));
            if (Random && Limit is not null) Add("sortby", "Random");
            if (Studios.Length > 0) Add("studios", string.Join(',', Studios));
            if (Tags.Length > 0) Add("tags", string.Join(',', Tags));
            if (Years.Length > 0) Add("years", string.Join(',', Years.Select(year => year.ToString(CultureInfo.InvariantCulture))));
            foreach (var filter in AdditionalFilters.OrderBy(filter => filter.Key, StringComparer.Ordinal))
            {
                Add(filter.Key, filter.CanonicalValue);
            }
            return Prefix + string.Join('&', pairs);
        }
    }

    public static (JellyfinQuery? Query, string? Error) Parse(string source)
    {
        var query = new JellyfinQuery();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in source[Prefix.Length..].Split('&'))
        {
            var pair = parameter.Split('=', 2);
            if (pair.Length != 2 || pair[0].Length == 0)
            {
                return (null, "Expected jellyfin/Key=Value with parameters separated by '&'.");
            }

            var key = pair[0].ToLowerInvariant();
            if (!keys.Add(key))
            {
                return (null, "A parameter is specified more than once.");
            }

            if (DecodeValue(pair[1]) is not { Length: > 0 } value)
            {
                return (null, "Each parameter needs a non-empty, URL-encoded value.");
            }

            switch (key)
            {
                case "mincriticrating":
                    if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rating) ||
                        rating is not (>= 0 and <= 100))
                    {
                        return (null, "minCriticRating must be a number between 0 and 100.");
                    }
                    query.MinCriticRating = rating;
                    break;
                case "tags":
                case "genres":
                case "studios":
                    var names = SplitList(value);
                    if (names.Contains(string.Empty))
                    {
                        return (null, "Tag, genre and studio lists cannot contain empty names.");
                    }
                    names = names.Select(name => name.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray();
                    if (key == "tags") query.Tags = names;
                    if (key == "genres") query.Genres = names;
                    if (key == "studios") query.Studios = names;
                    break;
                case "person":
                    query.Person = value;
                    break;
                case "searchterm":
                    query.SearchTerm = value.All(char.IsAscii) ? value.ToLowerInvariant() : value;
                    break;
                case "years":
                    var years = new HashSet<int>();
                    foreach (var year in SplitList(value))
                    {
                        if (!TryPositiveInt(year, out var number))
                        {
                            return (null, "years must contain positive whole numbers.");
                        }
                        years.Add(number);
                    }
                    query.Years = years.Order().ToArray();
                    break;
                case "includeitemtypes":
                    var types = new HashSet<BaseItemKind>();
                    foreach (var type in SplitList(value))
                    {
                        if (type.Equals("Movie", StringComparison.OrdinalIgnoreCase)) types.Add(BaseItemKind.Movie);
                        else if (type.Equals("Series", StringComparison.OrdinalIgnoreCase)) types.Add(BaseItemKind.Series);
                        else return (null, "includeItemTypes supports only Movie and Series.");
                    }
                    query.ItemTypes = types.Order().ToArray();
                    break;
                case "seriesstatus":
                    var statuses = new HashSet<SeriesStatus>();
                    foreach (var status in SplitList(value))
                    {
                        if (status.Equals("Continuing", StringComparison.OrdinalIgnoreCase)) statuses.Add(SeriesStatus.Continuing);
                        else if (status.Equals("Ended", StringComparison.OrdinalIgnoreCase)) statuses.Add(SeriesStatus.Ended);
                        else if (status.Equals("Unreleased", StringComparison.OrdinalIgnoreCase)) statuses.Add(SeriesStatus.Unreleased);
                        else return (null, "seriesStatus supports only Continuing, Ended and Unreleased.");
                    }
                    query.SeriesStatuses = statuses.Order().ToArray();
                    break;
                case "hassubtitles":
                case "is3d":
                    if (!bool.TryParse(value, out var flag))
                    {
                        return (null, $"'{key}' must be true or false.");
                    }
                    if (key == "hassubtitles") query.HasSubtitles = flag;
                    else query.Is3D = flag;
                    break;
                case "parentid":
                    if (!Guid.TryParse(value, out var parent) || parent == Guid.Empty)
                    {
                        return (null, "parentId must be a non-empty Jellyfin library ID (GUID).");
                    }
                    query.ParentId = parent;
                    break;
                case "limit":
                    if (!TryPositiveInt(value, out var limit))
                    {
                        return (null, "limit must be a positive whole number.");
                    }
                    query.Limit = limit;
                    break;
                case "sortby":
                    if (!value.Equals("Random", StringComparison.OrdinalIgnoreCase))
                    {
                        return (null, "sortBy supports only Random.");
                    }
                    query.Random = true;
                    break;
                case "list_name":
                    query.Name = value;
                    break;
                case "list_desc":
                    query.Description = value;
                    break;
                default:
                    if (!JellyfinAdditionalFilters.Supports(key))
                    {
                        return (null, "An unsupported parameter was specified.");
                    }
                    if (!JellyfinAdditionalFilters.TryParse(key, value, out var filter))
                    {
                        return (null, $"Invalid value for '{key}'.");
                    }
                    query._additionalFilters.Add(filter);
                    break;
            }
        }

        // Name and ID forms are alternatives, especially for studios: both write StudioIds.
        foreach (var (nameKey, idKey) in new[] { ("genres", "genreids"), ("person", "personids"), ("studios", "studioids") })
        {
            if (keys.Contains(nameKey) && keys.Contains(idKey))
            {
                return (null, $"'{nameKey}' and '{idKey}' cannot be combined.");
            }
        }
        return (query, null);
    }

    private static string[] SplitList(string value) => value.Split(',', StringSplitOptions.TrimEntries);

    private static bool TryPositiveInt(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    private static string? DecodeValue(string value)
    {
        var decoded = new StringBuilder(value.Length);
        var bytes = new List<byte>();
        for (int index = 0; index < value.Length;)
        {
            var ch = value[index];
            if (ch == '%')
            {
                bytes.Clear();
                while (index < value.Length && value[index] == '%')
                {
                    if (index + 2 >= value.Length || !byte.TryParse(value.AsSpan(index + 1, 2),
                        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var octet))
                    {
                        return null;
                    }
                    bytes.Add(octet);
                    index += 3;
                }
                try
                {
                    decoded.Append(StrictUtf8.GetString(bytes.ToArray()));
                }
                catch (DecoderFallbackException)
                {
                    return null;
                }
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or '~' or ',' or '+'))
            {
                return null;
            }
            decoded.Append(ch == '+' ? ' ' : ch);
            index++;
        }
        var result = decoded.ToString();
        return result.Any(char.IsControl) ? null : result.Trim();
    }
}
