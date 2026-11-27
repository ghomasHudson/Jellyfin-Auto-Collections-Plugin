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
    private static readonly Regex Caption = new(@"\A(.+) \(([0-9]{4})\)\z", RegexOptions.NonBacktracking);

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

        string html;
        try
        {
            using var response = await http.GetAsync(GetCanonicalUrl(url), cancellationToken).ConfigureAwait(false);
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
        var movies = article is null ? [] : ReadMovies(article);
        if (movies.Count == 0)
        {
            throw new SourceListReadException(url, "Expected film sections in the BFI article.");
        }

        var name = ReadHeadline(page) ?? article?.QuerySelector(":scope > header h1")?.TextContent.Trim();
        return new(name, []) { MoviesByTitleYear = movies };
    }

    private static List<MovieTitleYear> ReadMovies(IElement article)
    {
        var movies = new List<MovieTitleYear>();
        var heading = string.Empty;
        // Sections can span adjacent text blocks. Don't scan the hero, footer or related articles.
        foreach (var element in article.Children.Where(block => block.Matches(TextBlock)).SelectMany(block => block.Children))
        {
            if (element.LocalName == "h2")
            {
                heading = MovieTitleYear.NormalizeTitle(element.TextContent);
                continue;
            }

            foreach (var caption in element.QuerySelectorAll("figure figcaption"))
            {
                var text = MovieTitleYear.NormalizeTitle(caption.TextContent);
                var match = Caption.Match(text);
                if (!match.Success || (!heading.Equals(text, StringComparison.OrdinalIgnoreCase) &&
                    !heading.EndsWith(" in " + text, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var year = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (year > 0)
                {
                    movies.Add(new(match.Groups[1].Value, year));
                    heading = string.Empty;
                }
            }
        }
        return movies;
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
