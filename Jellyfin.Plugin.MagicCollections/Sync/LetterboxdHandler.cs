using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class LetterboxdHandler(HttpClient http) : ISourceListHandler
{
    private const string Origin = "https://letterboxd.com";
    private static readonly Regex ListPath = new(@"\A/[a-z0-9_]+/list/[a-z0-9_-]+/\z", RegexOptions.NonBacktracking);
    private static readonly Regex FilmPath = new(@"\A/film/[a-z0-9_-]+/\z", RegexOptions.NonBacktracking);
    private static readonly Regex TmdbPath = new(@"\A/(movie|tv)/([1-9][0-9]*)/?\z", RegexOptions.NonBacktracking);
    private static readonly Regex ImdbPath = new(@"\A/title/(tt[0-9]{7,})(?:/maindetails)?/?\z", RegexOptions.NonBacktracking);
    private static readonly Regex ListCount = new(
        @"\AA list of ([0-9]{1,3}(?:,[0-9]{3})+|[0-9]+) films? compiled on Letterboxd(?:[,.]|\z)",
        RegexOptions.NonBacktracking);
    private const string ListContent = "main#content .col-main";

    public bool Supports(Uri url) => url.IsAbsoluteUri && url.OriginalString == Origin + url.AbsolutePath &&
        ListPath.IsMatch(url.AbsolutePath);

    public async Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!Supports(url))
        {
            throw new ArgumentException("Not a public Letterboxd list URL.", nameof(url));
        }

        var (name, filmPaths) = await ReadMembershipAsync(url, cancellationToken).ConfigureAwait(false);
        var items = new List<MediaIdentity>();
        foreach (var filmPath in filmPaths)
        {
            var film = await FetchPageAsync(url, filmPath, cancellationToken).ConfigureAwait(false);
            if (!IsPage(film, filmPath) || film.Body?.GetAttribute("data-type") != "film" ||
                film.QuerySelector("main#content #film-page-wrapper h1.primaryname") is null)
            {
                throw InvalidPage(url, filmPath, "expected the film details page");
            }

            if (ReadIdentity(film) is { } identity)
            {
                items.Add(identity);
            }
        }

        return new(name, items);
    }

    private async Task<(string Name, List<string> FilmPaths)> ReadMembershipAsync(Uri url, CancellationToken cancellationToken)
    {
        var pagePath = url.AbsolutePath;
        var pageNumber = 1;
        var seen = 0;
        int? total = null;
        string? name = null;
        var filmPaths = new List<string>();
        var uniqueFilmPaths = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var listPage = await FetchPageAsync(url, pagePath, cancellationToken).ConfigureAwait(false);
            var pageName = listPage.QuerySelector("main #userpanel[data-component-class=ListSidebar][data-list-type=public]")
                ?.GetAttribute("data-list-name")?.Trim();
            var grid = listPage.QuerySelector(ListContent);
            if (Meta(listPage, """meta[property="og:type"]""") != "letterboxd:list" ||
                !IsPage(listPage, pagePath) || grid is null || string.IsNullOrEmpty(pageName) ||
                (name is not null && name != pageName))
            {
                throw InvalidPage(url, pagePath, "expected the public list page");
            }

            var count = ListCount.Match(Meta(listPage, "meta[name=description]") ?? "");
            if (!count.Success || !int.TryParse(count.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var pageTotal) ||
                (total is not null && total != pageTotal))
            {
                throw InvalidPage(url, pagePath, "missing or inconsistent list size");
            }
            name ??= pageName;
            total ??= pageTotal;

            // Check positional membership independently of the distinct detail URLs. A missing
            // row must not silently turn into fewer verified movies or an apparently empty list.
            var rows = grid.QuerySelectorAll("ul.poster-list > li.posteritem");
            if (rows.Length > total.Value - seen || (rows.Length == 0 && total != 0))
            {
                throw InvalidPage(url, pagePath, "unexpected number of entries");
            }
            foreach (var row in rows)
            {
                var posters = row.Children.Where(c => c.GetAttribute("data-component-class") == "LazyPoster").ToArray();
                var link = posters.Length == 1 ? posters[0].GetAttribute("data-item-link") : null;
                if (string.IsNullOrWhiteSpace(row.GetAttribute("data-object-id")) ||
                    link is null || !FilmPath.IsMatch(link) ||
                    posters[0].GetAttribute("data-list-index") != seen.ToString(CultureInfo.InvariantCulture))
                {
                    throw InvalidPage(url, pagePath, "invalid or missing list entry");
                }
                seen++;
                if (uniqueFilmPaths.Add(link))
                {
                    filmPaths.Add(link);
                }
            }

            var next = grid.QuerySelectorAll("div.pagination a.next");
            if (seen == total)
            {
                if (next.Length != 0)
                {
                    throw InvalidPage(url, pagePath, "unexpected extra page");
                }
                return (name, filmPaths);
            }

            var expectedNext = $"{url.AbsolutePath}page/{(++pageNumber).ToString(CultureInfo.InvariantCulture)}/";
            var nextLink = next.Length == 1 ? next[0].GetAttribute("href") : null;
            if (nextLink != expectedNext && nextLink != Origin + expectedNext)
            {
                throw InvalidPage(url, pagePath, "missing or invalid next page link");
            }
            pagePath = expectedNext;
        }
    }

    private static string? Meta(IDocument page, string selector) =>
        page.Head?.QuerySelector(selector)?.GetAttribute("content");

    private static bool IsPage(IDocument page, string path) =>
        Meta(page, """meta[property="og:url"]""") == Origin + path &&
        page.QuerySelector("main#content") is not null && page.QuerySelector("footer#page-footer") is not null;

    private static SourceListReadException InvalidPage(Uri source, string path, string reason) =>
        new(source, $"Invalid Letterboxd page {path}: {reason}.", new FormatException(reason));

    private async Task<IDocument> FetchPageAsync(Uri source, string path, CancellationToken cancellationToken)
    {
        string html;
        try
        {
            // Paths are verified before reaching here. The production client never follows redirects.
            using var response = await http.GetAsync(Origin + path, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            }

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidPage(source, path, "expected HTML");
            }

            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new SourceListReadException(source, $"Letterboxd fetch failed for {path}: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceListReadException(source, $"Letterboxd fetch timed out for {path}.", ex);
        }

        // The HTML parser repairs truncated documents, so also check the raw response tail.
        if (!html.AsSpan().TrimEnd().EndsWith("</html>", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidPage(source, path, "incomplete HTML");
        }

        return await new HtmlParser().ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
    }

    private static MediaIdentity? ReadIdentity(IDocument film)
    {
        var links = film.QuerySelectorAll("main #film-page-wrapper p.text-footer a[data-track-action]");
        var tmdbLinks = ProviderLinks(links, "TMDB");
        if (ReadTmdb(film, tmdbLinks) is not { } tmdb)
        {
            return null;
        }

        var ids = new Dictionary<string, string> { ["Tmdb"] = tmdb.Id };
        var imdb = ProviderLink(ProviderLinks(links, "IMDb"), "imdb.com", ImdbPath);
        if (imdb.Success)
        {
            ids["Imdb"] = imdb.Groups[1].Value;
        }

        return new(tmdb.Type, ids);
    }

    private static (SourceMediaType Type, string Id)? ReadTmdb(IDocument film, string?[] links)
    {
        if (links.Length != 0)
        {
            var match = ProviderLink(links, "themoviedb.org", TmdbPath);
            return match.Success && IsTmdbId(match.Groups[2].Value) && ReadTmdbType(match.Groups[1].Value) is { } type
                ? (type, match.Groups[2].Value)
                : null;
        }

        // Letterboxd can emit data-tmdb-type="movie" even for series. Never use
        // that marker without a valid ID, or in preference to a direct provider link.
        return film.Body?.GetAttribute("data-tmdb-id") is { } id && IsTmdbId(id) &&
            ReadTmdbType(film.Body.GetAttribute("data-tmdb-type")) is { } bodyType ? (bodyType, id) : null;
    }

    private static SourceMediaType? ReadTmdbType(string? type) => type switch
    {
        "movie" => SourceMediaType.Movie,
        "tv" => SourceMediaType.Series,
        _ => null
    };

    private static string?[] ProviderLinks(IEnumerable<IElement> links, string action) =>
        links.Where(link => string.Equals(link.GetAttribute("data-track-action"), action, StringComparison.OrdinalIgnoreCase))
            .Select(link => link.GetAttribute("href")).Distinct(StringComparer.Ordinal).ToArray();

    private static Match ProviderLink(string?[] hrefs, string host, Regex path)
    {
        return hrefs.Length == 1 && Uri.TryCreate(hrefs[0], UriKind.Absolute, out var link) &&
            (link.Scheme == "https" || (host == "imdb.com" && link.Scheme == "http")) &&
            (link.Host == host || link.Host == "www." + host) && link.IsDefaultPort && link.UserInfo.Length == 0 &&
            hrefs[0] == link.GetLeftPart(UriPartial.Path)
            ? path.Match(link.AbsolutePath)
            : Match.Empty;
    }

    private static bool IsTmdbId(string id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
}
