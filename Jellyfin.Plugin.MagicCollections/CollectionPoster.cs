using SkiaSharp;

namespace Jellyfin.Plugin.MagicCollections;

internal static class CollectionPoster
{
    public static Stream Render(string title)
    {
        const int width = 600;
        const int height = 900;
        const float textWidth = 500;
        const float textHeight = 800;
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        using var typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold);
        using var font = new SKFont(typeface, 56);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        canvas.Clear(SKColors.Black);
        var lines = WrapTitle(title, font, paint, textWidth).ToArray();
        if (lines.Length != 0)
        {
            var widest = lines.Max(line => font.MeasureText(line, paint));
            font.Size *= Math.Min(1, Math.Min(textWidth / widest, textHeight / (lines.Length * font.Spacing)));
            var metrics = font.Metrics;
            var baseline = (height - (lines.Length - 1) * font.Spacing - metrics.Ascent - metrics.Descent) / 2;
            foreach (var line in lines)
            {
                canvas.DrawText(line, width / 2f, baseline, SKTextAlign.Center, font, paint);
                baseline += font.Spacing;
            }
        }

        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new MemoryStream(png.ToArray(), writable: false);
    }

    private static IEnumerable<string> WrapTitle(string title, SKFont font, SKPaint paint, float maxWidth)
    {
        var line = string.Empty;
        foreach (var word in title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (line.Length != 0 && font.MeasureText(candidate, paint) > maxWidth)
            {
                yield return line;
                line = word;
            }
            else
            {
                line = candidate;
            }
        }

        if (line.Length != 0)
        {
            yield return line;
        }
    }
}
