using AngleSharp.Dom;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal static class SourceImages
{
    public static IReadOnlyList<Uri> ReadFaviconUrls(IDocument page, Uri pageUrl, string? assetHost = null)
    {
        var declared = new List<Uri>();
        var links = page.Head?.QuerySelectorAll("link[rel][href]").AsEnumerable() ?? [];
        foreach (var link in links)
        {
            var rel = link.GetAttribute("rel")!.Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries);
            if (!rel.Contains("icon", StringComparer.OrdinalIgnoreCase) ||
                Resolve(pageUrl, link.GetAttribute("href"), assetHost) is not { } icon || declared.Contains(icon))
            {
                continue;
            }

            declared.Add(icon);
            if (declared.Count == 4)
            {
                break;
            }
        }

        var fallback = new Uri(pageUrl, "/favicon.ico");
        if (!declared.Contains(fallback))
        {
            declared.Add(fallback);
        }

        return declared;
    }

    public static Uri? ReadListImageUrl(IDocument page, Uri pageUrl, string? assetHost = null) =>
        Resolve(pageUrl, page.Head?.QuerySelector("""meta[property="og:image"]""")?.GetAttribute("content"), assetHost);

    // Resolve against the fetched page, not a <base> URL supplied by its HTML.
    private static Uri? Resolve(Uri pageUrl, string? value, string? assetHost) =>
        !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(pageUrl, value.Trim(), out var url) &&
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo.Length == 0 &&
        (url.IdnHost == pageUrl.IdnHost || url.IdnHost == assetHost) ? url : null;
}
