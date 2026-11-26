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
        foreach (var br in description.QuerySelectorAll("br"))
        {
            br.InsertAdjacentText("afterend", "\n");
        }
        foreach (var paragraph in description.QuerySelectorAll("p"))
        {
            if (paragraph.NextSibling is IText spacing && string.IsNullOrWhiteSpace(spacing.TextContent))
            {
                spacing.TextContent = "";
            }
            paragraph.InsertAdjacentText("afterend", "\n\n");
        }
        return description.TextContent.Trim();
    }
}
