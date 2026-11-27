using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class BfiHandler(HttpClient http) : ISourceListHandler
{
    private const string Origin = "https://www.bfi.org.uk";
    private const string ArticleMain = "main > article > div[class*='ArticlePageTemplate__ArticleMain-']";
    private const string TextBlock = "div[class*='EmbeddedArticleText__TextContent-']";
    private static readonly Regex ListPath = new(@"\A/lists/[a-z0-9]+(?:-[a-z0-9]+)*/?\z", RegexOptions.NonBacktracking);
    private static readonly Regex TitleThenYear = new(@"\A(.+) \(([0-9]{4})\)\z", RegexOptions.NonBacktracking);
    private static readonly Regex YearThenTitle = new(@"\A([0-9]{4}): (.+)\z", RegexOptions.NonBacktracking);

    public string SourceTag => "BFI";

    public bool Supports(Uri url) => url.IsAbsoluteUri && url.OriginalString == Origin + url.AbsolutePath &&
        ListPath.IsMatch(url.AbsolutePath);

    public Uri GetCanonicalUrl(Uri url) =>
        Supports(url) ? new(Origin + url.AbsolutePath.TrimEnd('/')) : url;

    public async Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!Supports(url))
        {
            throw new ArgumentException("Not a BFI editorial list URL.", nameof(url));
        }

        var pageUrl = GetCanonicalUrl(url);
        string html;
        try
        {
            using var response = await http.GetAsync(pageUrl, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new SourceListReadException(url, $"BFI returned HTTP {(int)response.StatusCode}.");
            }

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw new SourceListReadException(url, "Expected a BFI HTML article.");
            }
            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new SourceListReadException(url, $"BFI fetch failed: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceListReadException(url, "BFI fetch timed out.", ex);
        }

        if (!html.AsSpan().TrimEnd().EndsWith("</html>", StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceListReadException(url, "Incomplete BFI HTML article.");
        }

        using var page = await new HtmlParser().ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        var article = page.QuerySelector(ArticleMain);
        if (article is null || ReadMovies(article) is not { } movies)
        {
            throw new SourceListReadException(url, "Expected captioned film sections in the BFI article.");
        }

        var name = ReadHeadline(page) ?? article.QuerySelector(":scope > header h1")?.TextContent.Trim();
        var description = page.Head?.QuerySelector("meta[property='og:description']")?.GetAttribute("content")?.Trim();
        return new(name, [], description)
        {
            MoviesByTitleYear = movies,
            ImageUrl = SourceImages.ReadListImageUrl(page, pageUrl, assetHost: "core-cms.bfi.org.uk")
        };
    }

    private static List<MovieTitleYear>? ReadMovies(IElement article)
    {
        var movies = new List<MovieTitleYear>();
        var filmHeadings = 0;
        var captionedHeadings = 0;
        Match heading = Match.Empty;
        var hasMatchingCaption = false;
        // Sections can span adjacent text blocks. Don't scan the hero, footer or related articles.
        foreach (var element in article.Children.Where(block => block.Matches(TextBlock)).SelectMany(block => block.Children))
        {
            if (element.LocalName == "h2")
            {
                hasMatchingCaption = false;
                var title = MovieTitleYear.NormalizeTitle(element.TextContent);
                var yearFirst = YearThenTitle.Match(title);
                heading = TitleThenYear.Match(yearFirst.Success
                    ? $"{yearFirst.Groups[2].Value} ({yearFirst.Groups[1].Value})"
                    : title);
                if (heading.Success)
                {
                    filmHeadings++;
                }
                continue;
            }

            if (!heading.Success)
            {
                continue;
            }

            foreach (var caption in element.QuerySelectorAll("figure figcaption"))
            {
                // Image credits are separate from the caption, even when rendered inline.
                var text = MovieTitleYear.NormalizeTitle(string.Concat(caption.ChildNodes
                    .Where(node => node is not IElement { LocalName: "small" }).Select(node => node.TextContent)));
                var match = TitleThenYear.Match(text);
                var title = match.Success ? match.Groups[1].Value : text;
                var headingTitle = heading.Groups[1].Value;
                if (title.Length == 0 || (!headingTitle.Equals(title, StringComparison.OrdinalIgnoreCase) &&
                    !headingTitle.EndsWith(" in " + title, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!hasMatchingCaption)
                {
                    captionedHeadings++;
                    hasMatchingCaption = true;
                }
                if (match.Success && match.Groups[2].Value == heading.Groups[2].Value &&
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) is > 0 and var year)
                {
                    movies.Add(new(title, year));
                    heading = Match.Empty;
                    break;
                }
            }
        }

        // A stray still in a shot survey is not a list. The caption-backed format should
        // account for most dated headings, though individual film captions can be unusable.
        return captionedHeadings > filmHeadings / 2 ? movies : null;
    }

    private static string? ReadHeadline(IDocument page)
    {
        foreach (var script in page.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent);
                var value = json.RootElement;
                if (value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty("@type", out var type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "Article" && value.TryGetProperty("headline", out var headline) &&
                    headline.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(headline.GetString()))
                {
                    return headline.GetString()!.Trim();
                }
            }
            catch (JsonException)
            {
                // Metadata is optional; the editorial heading can still supply the name.
            }
        }
        return null;
    }
}
