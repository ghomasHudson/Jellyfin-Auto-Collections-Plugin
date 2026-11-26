using AngleSharp.Dom;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal static class ListDescription
{
    public static string? Read(IElement? description)
    {
        if (description is null)
        {
            return null;
        }

        // TextContent decodes entities and keeps link text but adds no spacing for HTML breaks.
        var formatted = (IElement)description.Clone(deep: true);
        foreach (var br in formatted.QuerySelectorAll("br"))
        {
            br.InsertAdjacentText("afterend", "\n");
        }
        foreach (var paragraph in formatted.QuerySelectorAll("p"))
        {
            if (paragraph.NextSibling is IText spacing && string.IsNullOrWhiteSpace(spacing.TextContent))
            {
                spacing.TextContent = "";
            }
            paragraph.InsertAdjacentText("afterend", "\n\n");
        }
        return formatted.TextContent.Trim();
    }
}
