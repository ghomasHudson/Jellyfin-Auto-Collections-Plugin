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
        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var document = await FetchPageAsync(url, pagePath, cancellationToken).ConfigureAwait(false);
            var page = ParseListPage(document, url, pagePath);
            if ((name is not null && name != page.Name) || (total is not null && total != page.Total))
            {
                throw InvalidPage(url, pagePath, "the list name or size changed between pages");
            }
            name ??= page.Name;
            total ??= page.Total;

            // Check positional membership independently of the distinct detail URLs. A missing
            // row must not silently turn into fewer verified movies or an apparently empty list.
            if (page.Entries.Count > total.Value - seen || (page.Entries.Count == 0 && total != 0))
            {
                throw InvalidPage(url, pagePath, "unexpected number of entries");
            }
            foreach (var entry in page.Entries)
            {
                if (entry.Index != seen || !entryIds.Add(entry.Id))
                {
                    throw InvalidPage(url, pagePath, "unexpected entry position or repeated list entry");
                }
                seen++;
                if (uniqueFilmPaths.Add(entry.FilmPath))
                {
                    filmPaths.Add(entry.FilmPath);
                }
            }

            if (seen == total)
            {
                if (page.NextLink is not null)
                {
                    throw InvalidPage(url, pagePath, "unexpected extra page");
                }
                return (name, filmPaths);
            }

            var expectedNext = $"{url.AbsolutePath}page/{(++pageNumber).ToString(CultureInfo.InvariantCulture)}/";
            if (page.NextLink != expectedNext && page.NextLink != Origin + expectedNext)
            {
                throw InvalidPage(url, pagePath, "missing or invalid next page link");
            }
            pagePath = expectedNext;
        }
    }

    private sealed record ListEntry(string Id, int Index, string FilmPath);

    private sealed record ListPage(string Name, int Total, List<ListEntry> Entries, string? NextLink);

    private static ListPage ParseListPage(IDocument document, Uri source, string path)
    {
        var name = document.QuerySelector("main #userpanel[data-component-class=ListSidebar][data-list-type=public]")
            ?.GetAttribute("data-list-name")?.Trim();
        var grid = document.QuerySelector(ListContent);
        if (Meta(document, """meta[property="og:type"]""") != "letterboxd:list" ||
            !IsPage(document, path) || grid is null || string.IsNullOrEmpty(name))
        {
            throw InvalidPage(source, path, "expected the public list page");
        }

        var count = ListCount.Match(Meta(document, "meta[name=description]") ?? "");
        if (!count.Success || !int.TryParse(count.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var total))
        {
            throw InvalidPage(source, path, "missing or invalid list size");
        }

        var entries = new List<ListEntry>();
        foreach (var row in grid.QuerySelectorAll("ul.poster-list > li.posteritem"))
        {
            var posters = row.Children.Where(c => c.GetAttribute("data-component-class") == "LazyPoster").ToArray();
            var link = posters.Length == 1 ? posters[0].GetAttribute("data-item-link") : null;
            var id = row.GetAttribute("data-object-id");
            if (string.IsNullOrWhiteSpace(id) || link is null || !FilmPath.IsMatch(link) ||
                !int.TryParse(posters[0].GetAttribute("data-list-index"), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                throw InvalidPage(source, path, "invalid or missing list entry");
            }
            entries.Add(new(id, index, link));
        }

        var nextLinks = grid.QuerySelectorAll("div.pagination a.next");
        var nextLink = nextLinks.Length == 1 ? nextLinks[0].GetAttribute("href") : null;
        if (nextLinks.Length != 0 && string.IsNullOrEmpty(nextLink))
        {
            throw InvalidPage(source, path, "invalid next page link");
        }
        return new(name, total, entries, nextLink);
    }

    private static string? Meta(IDocument page, string selector) =>
        page.Head?.QuerySelector(selector)?.GetAttribute("content");

    private static bool IsPage(IDocument page, string path) =>
        Meta(page, """meta[property="og:url"]""") == Origin + path &&
        page.QuerySelector("main#content") is not null && page.QuerySelector("footer#page-footer") is not null;

    private static SourceListReadException InvalidPage(Uri source, string path, string reason) =>
        new(source, $"Invalid Letterboxd page {path}: {reason}.");

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
        var links = film.QuerySelectorAll("main #film-page-wrapper a[data-track-action]");
        var tmdb = MatchProviderPath(ProviderLinks(links, "TMDB"), "themoviedb.org", TmdbPath);
        if (!tmdb.Success)
        {
            return null;
        }

        // Letterboxd can label a series as a movie in its general metadata.
        // The type comes from the TMDb link's explicit movie or TV namespace.
        var type = tmdb.Groups[1].Value == "tv" ? SourceMediaType.Series : SourceMediaType.Movie;
        var ids = new Dictionary<string, string> { ["Tmdb"] = tmdb.Groups[2].Value };
        var imdb = MatchProviderPath(ProviderLinks(links, "IMDb"), "imdb.com", ImdbPath);
        if (imdb.Success)
        {
            ids["Imdb"] = imdb.Groups[1].Value;
        }

        return new(type, ids);
    }

    private static string?[] ProviderLinks(IEnumerable<IElement> links, string action) =>
        links.Where(link => string.Equals(link.GetAttribute("data-track-action"), action, StringComparison.OrdinalIgnoreCase))
            .Select(link => link.GetAttribute("href")).Distinct(StringComparer.Ordinal).ToArray();

    private static Match MatchProviderPath(string?[] hrefs, string host, Regex path)
    {
        return hrefs.Length == 1 && Uri.TryCreate(hrefs[0], UriKind.Absolute, out var link) &&
            (link.Scheme == "https" || (host == "imdb.com" && link.Scheme == "http")) &&
            (link.Host == host || link.Host == "www." + host) && link.IsDefaultPort && link.UserInfo.Length == 0 &&
            hrefs[0] == link.GetLeftPart(UriPartial.Path)
            ? path.Match(link.AbsolutePath)
            : Match.Empty;
    }
}
