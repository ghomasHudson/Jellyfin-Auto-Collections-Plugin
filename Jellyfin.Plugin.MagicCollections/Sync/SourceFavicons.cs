using AngleSharp.Dom;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal static class SourceFavicons
{
    public static IReadOnlyList<Uri> FromPage(IDocument page, Uri pageUrl, string? assetHost = null)
    {
        var declared = new List<Uri>();
        // Resolve against the fetched page, not a <base> URL supplied by its HTML.
        var links = page.Head?.QuerySelectorAll("link[rel][href]").AsEnumerable() ?? [];
        foreach (var link in links)
        {
            var rel = link.GetAttribute("rel")!.Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries);
            var href = link.GetAttribute("href");
            if (!rel.Contains("icon", StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(href) ||
                !Uri.TryCreate(pageUrl, href.Trim(), out var icon) || icon.Scheme != Uri.UriSchemeHttps ||
                !icon.IsDefaultPort || icon.UserInfo.Length != 0 ||
                (icon.IdnHost != pageUrl.IdnHost && icon.IdnHost != assetHost) || declared.Contains(icon))
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
}
