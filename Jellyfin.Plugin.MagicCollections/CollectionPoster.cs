using System.Globalization;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Buffer = HarfBuzzSharp.Buffer;

namespace Jellyfin.Plugin.MagicCollections;

internal static class CollectionPoster
{
    public static Stream Render(string title, SKBitmap? logo)
    {
        const int width = 600;
        const int height = 900;
        const float maxTextWidth = 500;
        const float maxTextHeight = 800;
        const float maxLogoSize = 112;
        const float logoGap = 32;
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        title = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        using var text = new PosterText(title);
        // Keep color-font glyphs white too.
        using var white = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, ColorFilter = white };

        canvas.Clear(SKColors.Black);
        var lines = WrapTitle(title, text, maxTextWidth).ToArray();
        var logoScale = logo is null ? 0 : maxLogoSize / Math.Max(logo.Width, logo.Height);
        var logoWidth = (logo?.Width ?? 0) * logoScale;
        var logoHeight = (logo?.Height ?? 0) * logoScale;
        var logoSpace = logoHeight + (logo is not null && lines.Length != 0 ? logoGap : 0);
        var ascent = lines.Length == 0 ? 0 : lines.Min(line => line.Ascent);
        var descent = lines.Length == 0 ? 0 : lines.Max(line => line.Descent);
        var lineHeight = descent - ascent;
        var spacing = lineHeight + 8;
        var textHeight = lines.Length == 0 ? 0 : (lines.Length - 1) * spacing + lineHeight;
        var scale = lines.Length == 0 ? 1 :
            Math.Min(1, Math.Min(maxTextWidth / lines.Max(line => line.Width), (maxTextHeight - logoSpace) / textHeight));
        var top = (height - logoSpace - textHeight * scale) / 2;

        if (logo is not null)
        {
            var left = (width - logoWidth) / 2;
            var bounds = SKRect.Create(left, top, logoWidth, logoHeight);
            using var image = SKImage.FromBitmap(logo);
            canvas.DrawImage(image, bounds, new SKSamplingOptions(SKFilterMode.Linear));
        }

        if (lines.Length != 0)
        {
            canvas.Translate(width / 2f, top + logoSpace);
            canvas.Scale(scale);
            var baseline = -ascent;
            foreach (var line in lines)
            {
                using var builder = new SKTextBlobBuilder();
                foreach (var run in line.Runs)
                {
                    builder.AddPositionedRun(run.Glyphs, run.Font, run.Positions);
                }

                using var blob = builder.Build();
                if (blob is not null)
                {
                    canvas.DrawText(blob, -line.Width / 2, baseline, paint);
                }

                baseline += spacing;
            }
        }

        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new MemoryStream(png.ToArray(), writable: false);
    }

    private sealed class PosterText(string title) : IDisposable
    {
        private readonly PosterBidi? _bidi = PosterBidi.ForTitle(title);
        private readonly SKTypeface _defaultFace = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold);
        private readonly Dictionary<int, SKTypeface?> _fallbacks = new();
        private readonly Dictionary<SKTypeface, (SKFont Font, SKShaper Shaper)> _fonts = new();

        public ShapedLine Shape(int start, int end)
        {
            var runs = new List<GlyphRun>();
            float width = 0, ascent = 0, descent = 0;
            var line = title.AsSpan(start, end - start);
            var directions = _bidi?.VisualRuns(start, end) ??
                [new(start, end, FirstContentScript(line).HorizontalDirection == Direction.RightToLeft)];
            foreach (var direction in directions)
            {
                var logicalRuns = FontRuns(title.AsSpan(direction.Start, direction.End - direction.Start));
                if (direction.RightToLeft)
                {
                    logicalRuns.Reverse();
                }

                foreach (var run in logicalRuns)
                {
                    var (font, shaper) = FontFor(run.Typeface);
                    using var buffer = new Buffer
                    {
                        Direction = direction.RightToLeft ? Direction.RightToLeft : Direction.LeftToRight,
                        Script = run.Script
                    };
                    // Give HarfBuzz the line as context for joining across fallback font runs,
                    // while only shaping the text belonging to this font/script.
                    buffer.AddUtf16(line, direction.Start - start + run.Start, run.End - run.Start);
                    buffer.GuessSegmentProperties();
                    var shaped = shaper.Shape(buffer, width, 0, font);
                    width += shaped.Width;
                    ascent = Math.Min(ascent, font.Metrics.Ascent);
                    descent = Math.Max(descent, font.Metrics.Descent);
                    if (shaped.Codepoints.Length > 0)
                    {
                        runs.Add(new(font, Array.ConvertAll(shaped.Codepoints, glyph => (ushort)glyph), shaped.Points));
                    }
                }
            }

            return new(runs, width, ascent, descent);
        }

        private List<FontRun> FontRuns(ReadOnlySpan<char> text)
        {
            var runs = new List<FontRun>();
            var script = FirstContentScript(text);
            for (var start = 0; start < text.Length;)
            {
                var end = start + StringInfo.GetNextTextElementLength(text[start..]);
                var cluster = text[start..end];
                var clusterScript = FirstContentScript(cluster);
                if (!clusterScript.Equals(Script.Unknown))
                {
                    script = clusterScript;
                }

                var face = runs.Count > 0 && runs[^1].Script.Equals(script) && Covers(runs[^1].Typeface, cluster)
                    ? runs[^1].Typeface
                    : MatchTypeface(cluster);
                if (runs.Count > 0 && runs[^1].Typeface == face && runs[^1].Script.Equals(script))
                {
                    runs[^1] = runs[^1] with { End = end };
                }
                else
                {
                    runs.Add(new(face, script, start, end));
                }

                start = end;
            }

            return runs;
        }

        private static Script FirstContentScript(ReadOnlySpan<char> text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                var script = UnicodeFunctions.Default.GetScript(rune.Value);
                if (!script.Equals(Script.Common) && !script.Equals(Script.Inherited) && !script.Equals(Script.Unknown))
                {
                    return script;
                }
            }

            return Script.Unknown;
        }

        private static bool Covers(SKTypeface face, ReadOnlySpan<char> cluster)
        {
            foreach (var rune in cluster.EnumerateRunes())
            {
                if (NeedsGlyph(rune) && face.GetGlyph(rune.Value) == 0)
                {
                    return false;
                }
            }

            return true;
        }

        private SKTypeface MatchTypeface(ReadOnlySpan<char> cluster)
        {
            if (Covers(_defaultFace, cluster))
            {
                return _defaultFace;
            }

            SKTypeface? partialMatch = null;
            foreach (var rune in cluster.EnumerateRunes())
            {
                if (!NeedsGlyph(rune))
                {
                    continue;
                }

                if (!_fallbacks.TryGetValue(rune.Value, out var fallback))
                {
                    fallback = SKFontManager.Default.MatchCharacter(null, SKFontStyle.Bold, null, rune.Value);
                    _fallbacks.Add(rune.Value, fallback);
                }

                if (fallback is not null && Covers(fallback, cluster))
                {
                    return fallback;
                }

                partialMatch ??= fallback;
            }

            return partialMatch ?? _defaultFace;
        }

        private static bool NeedsGlyph(Rune rune) =>
            Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format
            && rune.Value is not (>= 0xfe00 and <= 0xfe0f or >= 0xe0100 and <= 0xe01ef);

        private (SKFont, SKShaper) FontFor(SKTypeface face)
        {
            if (!_fonts.TryGetValue(face, out var font))
            {
                font = (new SKFont(face, 56), new SKShaper(face));
                _fonts.Add(face, font);
            }

            return font;
        }

        public void Dispose()
        {
            _bidi?.Dispose();
            foreach (var (font, shaper) in _fonts.Values)
            {
                shaper.Dispose();
                font.Dispose();
            }

            // SkiaSharp prevents public disposal of MatchCharacter results.
            _defaultFace.Dispose();
        }

        private record struct FontRun(SKTypeface Typeface, Script Script, int Start, int End);
    }

    private sealed record ShapedLine(List<GlyphRun> Runs, float Width, float Ascent, float Descent);

    private sealed record GlyphRun(SKFont Font, ushort[] Glyphs, SKPoint[] Positions);

    private static IEnumerable<ShapedLine> WrapTitle(string title, PosterText text, float maxTextWidth)
    {
        int[] boundaries = [.. StringInfo.ParseCombiningCharacters(title), title.Length];

        for (var start = 0; start < boundaries.Length - 1;)
        {
            var end = start + 1;
            var line = text.Shape(boundaries[start], boundaries[end]);
            var lastSpace = -1;
            ShapedLine? lineBeforeSpace = null;
            while (end < boundaries.Length - 1)
            {
                if (title[boundaries[end]] == ' ')
                {
                    lastSpace = end;
                    lineBeforeSpace = line;
                }

                var candidate = text.Shape(boundaries[start], boundaries[end + 1]);
                if (candidate.Width > maxTextWidth)
                {
                    break;
                }

                line = candidate;
                end++;
            }

            if (end < boundaries.Length - 1 && lineBeforeSpace is not null)
            {
                end = lastSpace;
                line = lineBeforeSpace;
            }

            yield return line;
            start = end < boundaries.Length - 1 && title[boundaries[end]] == ' ' ? end + 1 : end;
        }
    }
}
