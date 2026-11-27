using System.Net;
using System.Reflection;
using Jellyfin.Plugin.MagicCollections;
using Jellyfin.Plugin.MagicCollections.Configuration;
using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace MagicCollections.Tests;

public class CollectionPosterTests
{
    private static readonly Uri SourceIcon = new("https://example.com/icon.png");
    private static readonly Uri ListImage = new("https://example.com/list.jpg");

    [Fact]
    public async Task Saves_a_black_portrait_PNG_with_white_title_text_as_the_primary_image()
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        using var host = new JellyfinBoundary(collection);

        await host.Library.EnsurePosterAsync(collection.Id, [], null, CancellationToken.None);

        Assert.Equal("image/png", host.MimeType);
        Assert.Equal(ImageType.Primary, Assert.Single(collection.ImageInfos).Type);
        Assert.Equal(ItemUpdateType.ImageUpdate, collection.LastUpdate);
        using var poster = DecodePoster(host);
        Assert.Equal(poster.Width * 3, poster.Height * 2);
        Assert.Equal(SKColors.Black, poster.GetPixel(0, 0));
        Assert.Equal(SKColors.Black, poster.GetPixel(poster.Width - 1, poster.Height - 1));
        Assert.Contains(SKColors.White, poster.Pixels);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Omits_only_the_poster_element_that_is_turned_off(bool showBackground, bool showIcon, bool showName)
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        var config = new PluginConfiguration
        {
            PosterBackgroundImage = showBackground,
            PosterIcon = showIcon,
            PosterListName = showName
        };
        var artwork = EncodeSolidImage(24);
        var responses = new RecordedHttpResponses();
        responses.Record(SourceIcon.AbsoluteUri, () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(artwork) });
        responses.Record(ListImage.AbsoluteUri, () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(artwork) });
        using var http = new HttpClient(responses);
        using var host = new JellyfinBoundary(collection, imageHttp: http, config: config);

        await host.Library.EnsurePosterAsync(collection.Id, [SourceIcon], ListImage, CancellationToken.None);

        Assert.Equal(showIcon, responses.Requests.Contains(SourceIcon.AbsoluteUri));
        Assert.Equal(showBackground, responses.Requests.Contains(ListImage.AbsoluteUri));
        using var poster = DecodePoster(host);
        Assert.Equal(showBackground, poster.GetPixel(0, 0) != SKColors.Black);
        var pixels = poster.Pixels;
        Assert.Equal(showIcon, pixels.Contains(SKColors.OrangeRed));
        Assert.Equal(showName, pixels.Contains(SKColors.White));
    }

    [Fact]
    public async Task Skips_poster_generation_when_all_poster_elements_are_turned_off()
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        var config = new PluginConfiguration { PosterBackgroundImage = false, PosterIcon = false, PosterListName = false };
        var responses = new RecordedHttpResponses();
        using var http = new HttpClient(responses);
        using var host = new JellyfinBoundary(collection, imageHttp: http, config: config);

        await host.Library.EnsurePosterAsync(collection.Id, [SourceIcon], ListImage, CancellationToken.None);

        Assert.Empty(responses.Requests);
        Assert.Empty(collection.ImageInfos);
        Assert.Null(host.Image);
    }

    [Theory]
    [InlineData(48)]
    [InlineData(216)]
    public async Task Normalizes_light_and_dark_list_artwork_to_midgray_before_applying_the_black_overlay(int brightness)
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        using var background = new SKBitmap(24, 12);
        var gray = (byte)brightness;
        using (var canvas = new SKCanvas(background))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(gray, gray, gray) };
            canvas.DrawRect(0, 0, 24, 6, paint);
        }
        using var data = background.Encode(SKEncodedImageFormat.Png, 100);
        var responses = new RecordedHttpResponses();
        responses.Record(ListImage.AbsoluteUri, () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data.ToArray()) });
        using var http = new HttpClient(responses);
        using var host = new JellyfinBoundary(collection, imageHttp: http);

        await host.Library.EnsurePosterAsync(collection.Id, [], ListImage, CancellationToken.None);

        using var poster = DecodePoster(host);
        var color = poster.GetPixel(0, 0);
        // Midgray at 20% brightness is about 26 on an 8-bit scale.
        Assert.InRange(color.Red, 25, 26);
        Assert.Equal(new SKColor(color.Red, color.Red, color.Red), color);
        Assert.Equal(SKColors.Black, poster.GetPixel(0, poster.Height - 1));
    }

    [Fact]
    public async Task Draws_a_favicon_above_the_title_preserving_its_color_transparency_and_aspect_ratio()
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        using var icon = new SKBitmap(24, 12);
        using (var canvas = new SKCanvas(icon))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.OrangeRed };
            canvas.DrawRect(0, 0, 18, 12, paint);
        }

        using var data = icon.Encode(SKEncodedImageFormat.Png, 100);
        var fallback = new Uri(SourceIcon, "/favicon.ico");
        var responses = new RecordedHttpResponses();
        responses.Record(SourceIcon.AbsoluteUri, () => new(HttpStatusCode.NotFound));
        // An ICO can contain a PNG frame.
        responses.Record(fallback.AbsoluteUri, () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(WrapInIcon(data.ToArray())) });
        using var http = new HttpClient(responses);
        using var host = new JellyfinBoundary(collection, imageHttp: http);

        await host.Library.EnsurePosterAsync(collection.Id, [SourceIcon, fallback], null, CancellationToken.None);

        Assert.Equal([SourceIcon.AbsoluteUri, fallback.AbsoluteUri], responses.Requests);
        using var poster = DecodePoster(host);
        var logo = poster.Pixels.Select((color, index) => (color, x: index % poster.Width, y: index / poster.Width))
            .Where(pixel => pixel.color == SKColors.OrangeRed).ToArray();
        Assert.NotEmpty(logo);
        var top = logo.Min(pixel => pixel.y);
        var bottom = logo.Max(pixel => pixel.y);
        Assert.InRange(bottom - top + 1, 54, 56);
        Assert.InRange(logo.Max(pixel => pixel.x) - logo.Min(pixel => pixel.x) + 1, 81, 84);
        Assert.Equal(SKColors.Black, poster.GetPixel(poster.Width / 2 + 48, (top + bottom) / 2));
        Assert.Contains(Enumerable.Range(bottom + 1, poster.Height - bottom - 1), row => HasWhitePixels(poster, row));
    }

    [Fact]
    public async Task Center_crops_and_darkens_the_list_image_behind_the_favicon_and_title()
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        using var background = new SKBitmap(1200, 600);
        using (var canvas = new SKCanvas(background))
        {
            canvas.Clear(SKColors.Red);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(300, 0, 600, 300, paint);
            paint.Color = SKColors.Cyan;
            canvas.DrawRect(300, 300, 600, 300, paint);
        }
        using var data = background.Encode(SKEncodedImageFormat.Png, 100);
        var responses = new RecordedHttpResponses();
        responses.Record(SourceIcon.AbsoluteUri, () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(EncodeSolidImage(24)) });
        responses.Record(ListImage.AbsoluteUri, () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data.ToArray()) });
        using var http = new HttpClient(responses);
        using var host = new JellyfinBoundary(collection, imageHttp: http);

        await host.Library.EnsurePosterAsync(collection.Id, [SourceIcon], ListImage, CancellationToken.None);

        using var poster = DecodePoster(host);
        var top = poster.GetPixel(0, 0);
        var bottom = poster.GetPixel(0, poster.Height - 1);
        // The brightness of the cropped white and cyan halves is about 89% before normalization.
        Assert.InRange(top.Red, 28, 29);
        Assert.Equal(new SKColor(top.Red, top.Red, top.Red), top);
        Assert.Equal(new SKColor(0, top.Red, top.Red), bottom);
        Assert.Equal(top, poster.GetPixel(poster.Width - 1, 0));
        Assert.Equal(bottom, poster.GetPixel(poster.Width - 1, poster.Height - 1));
        Assert.Contains(SKColors.OrangeRed, poster.Pixels);
        Assert.Contains(SKColors.White, poster.Pixels);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("oversized image")]
    [InlineData("oversized response")]
    [InlineData("redirect")]
    [InlineData("network error")]
    public async Task Saves_the_same_title_only_poster_when_the_favicon_cannot_be_used(string failure)
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        var responses = new RecordedHttpResponses();
        responses.Record(SourceIcon.AbsoluteUri, () => failure switch
        {
            "invalid" => new(HttpStatusCode.OK) { Content = new StringContent("Not an image") },
            "oversized image" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(EncodeSolidImage(1024)) },
            "oversized response" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[65_537]) },
            "redirect" => new(HttpStatusCode.Found) { Headers = { Location = new Uri(SourceIcon, "/redirected.png") } },
            _ => throw new HttpRequestException("Connection failed.")
        });
        using var http = new HttpClient(responses);
        using var host = new JellyfinBoundary(collection, imageHttp: http);

        await host.Library.EnsurePosterAsync(collection.Id, [SourceIcon], null, CancellationToken.None);

        using var plainPoster = CollectionPoster.Render(collection.Name, null, null);
        using var expected = SKBitmap.Decode(plainPoster);
        using var actual = DecodePoster(host);
        Assert.True(expected.Pixels.SequenceEqual(actual.Pixels));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("oversized image")]
    [InlineData("oversized response")]
    [InlineData("redirect")]
    [InlineData("not found")]
    public async Task Keeps_the_black_background_and_favicon_when_the_list_image_cannot_be_used(string failure)
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        var responses = new RecordedHttpResponses();
        var icon = EncodeSolidImage(24);
        responses.Record(SourceIcon.AbsoluteUri, () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(icon) });
        responses.Record(ListImage.AbsoluteUri, () => failure switch
        {
            "invalid" => new(HttpStatusCode.OK) { Content = new StringContent("Not an image") },
            "oversized image" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(EncodeSolidImage(4097)) },
            "oversized response" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]) },
            "redirect" => new(HttpStatusCode.Found) { Headers = { Location = new Uri(ListImage, "/redirected.png") } },
            _ => new(HttpStatusCode.NotFound)
        });
        using var http = new HttpClient(responses);
        using var host = new JellyfinBoundary(collection, imageHttp: http);

        await host.Library.EnsurePosterAsync(collection.Id, [SourceIcon], ListImage, CancellationToken.None);

        using var logo = SKBitmap.Decode(icon);
        using var plainPoster = CollectionPoster.Render(collection.Name, logo, null);
        using var expected = SKBitmap.Decode(plainPoster);
        using var actual = DecodePoster(host);
        Assert.True(expected.Pixels.SequenceEqual(actual.Pixels));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_source_image_request_cancels_poster_generation(bool isListImage)
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        var responses = new RecordedHttpResponses();
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        var url = isListImage ? ListImage : SourceIcon;
        responses.RecordPending(url.AbsoluteUri, pending.Task);
        using var http = new HttpClient(responses);
        using var host = new JellyfinBoundary(collection, imageHttp: http);
        using var cancellation = new CancellationTokenSource();

        var generating = host.Library.EnsurePosterAsync(collection.Id,
            isListImage ? [] : [SourceIcon], isListImage ? ListImage : null, cancellation.Token);
        Assert.Equal(url.AbsoluteUri, Assert.Single(responses.Requests));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generating);
        Assert.Null(host.Image);
    }

    [Fact]
    public async Task Keeps_a_long_collection_name_readable_on_multiple_lines()
    {
        var collection = new TestCollection
        {
            Id = Guid.NewGuid(),
            Name = "An international film festival featuring films from around the world"
        };
        using var host = new JellyfinBoundary(collection);

        await host.Library.EnsurePosterAsync(collection.Id, [], null, CancellationToken.None);

        using var poster = DecodePoster(host);
        Assert.Contains(Enumerable.Range(poster.Height / 2 - 130, 80), row => HasWhitePixels(poster, row));
        Assert.Contains(Enumerable.Range(poster.Height / 2 + 50, 80), row => HasWhitePixels(poster, row));
    }

    [Fact]
    public async Task Keeps_a_long_unbroken_collection_name_readable_on_multiple_lines()
    {
        var collection = new TestCollection
        {
            Id = Guid.NewGuid(),
            Name = "Across-the-world-documentaries-and-international-film-festival-award-winners"
        };
        using var host = new JellyfinBoundary(collection);

        await host.Library.EnsurePosterAsync(collection.Id, [], null, CancellationToken.None);

        using var poster = DecodePoster(host);
        Assert.Contains(Enumerable.Range(poster.Height / 2 - 150, 95), row => HasWhitePixels(poster, row));
        Assert.Contains(Enumerable.Range(poster.Height / 2 + 55, 95), row => HasWhitePixels(poster, row));
    }

    [Fact]
    public async Task Draws_distinct_Japanese_characters_in_a_collection_title()
    {
        var title = new TestCollection { Id = Guid.NewGuid(), Name = "日本映画" };
        var repeated = new TestCollection { Id = Guid.NewGuid(), Name = "日日日日" };
        using var host = new JellyfinBoundary(title);
        using var comparison = new JellyfinBoundary(repeated);

        await host.Library.EnsurePosterAsync(title.Id, [], null, CancellationToken.None);
        await comparison.Library.EnsurePosterAsync(repeated.Id, [], null, CancellationToken.None);

        using var poster = DecodePoster(host);
        using var repeatedPoster = DecodePoster(comparison);
        Assert.Contains(SKColors.White, poster.Pixels);
        Assert.False(poster.Pixels.SequenceEqual(repeatedPoster.Pixels), "Different Japanese characters must not render as identical boxes.");
    }

    [Fact]
    public async Task Joins_Arabic_letters_in_a_collection_title()
    {
        var joined = new TestCollection { Id = Guid.NewGuid(), Name = "لا" };
        var unjoined = new TestCollection { Id = Guid.NewGuid(), Name = "ل\u200Cا" }; // U+200C prevents joining.
        using var host = new JellyfinBoundary(joined);
        using var comparison = new JellyfinBoundary(unjoined);

        await host.Library.EnsurePosterAsync(joined.Id, [], null, CancellationToken.None);
        await comparison.Library.EnsurePosterAsync(unjoined.Id, [], null, CancellationToken.None);

        using var poster = DecodePoster(host);
        using var comparisonPoster = DecodePoster(comparison);
        Assert.Contains(SKColors.White, poster.Pixels);
        Assert.True(InkWidth(poster) < InkWidth(comparisonPoster), "Lam and alef should form a single ligature.");
    }

    [Fact]
    public async Task Keeps_Latin_text_and_digits_in_left_to_right_order_in_an_Arabic_collection_title()
    {
        // With a right-to-left base, "Film 12" stays in that order on the left.
        var rtl = new TestCollection { Id = Guid.NewGuid(), Name = "لا Film 12" };
        var ltr = new TestCollection { Id = Guid.NewGuid(), Name = "Film 12" };
        using var host = new JellyfinBoundary(rtl);
        using var comparison = new JellyfinBoundary(ltr);

        await host.Library.EnsurePosterAsync(rtl.Id, [], null, CancellationToken.None);
        await comparison.Library.EnsurePosterAsync(ltr.Id, [], null, CancellationToken.None);

        using var poster = DecodePoster(host);
        using var comparisonPoster = DecodePoster(comparison);
        var expected = Ink(comparisonPoster);
        var leftmost = Ink(poster, InkWidth(comparisonPoster));
        // Allow small rasterization shifts caused by fractional alignment on the canvas.
        Assert.True(expected.Intersect(leftmost).Count() >= 0.85 * expected.Count,
            "The left side of the Arabic title should contain 'Film 12' in that order.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Does_not_replace_an_existing_primary_image(bool remote)
    {
        using var files = new TestFiles();
        var path = remote ? "https://example.com/collection.jpg" : files.PathFor("custom.jpg");
        if (!remote)
        {
            File.WriteAllText(path, "custom poster");
        }

        var original = new ItemImageInfo { Type = ImageType.Primary, Path = path };
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks", ImageInfos = [original] };
        var responses = new RecordedHttpResponses();
        using var http = new HttpClient(responses);
        using var host = new JellyfinBoundary(collection, imageHttp: http);

        await host.Library.EnsurePosterAsync(collection.Id, [SourceIcon], ListImage, CancellationToken.None);

        Assert.Empty(responses.Requests);
        Assert.Same(original, Assert.Single(collection.ImageInfos));
        Assert.Equal(path, original.Path);
        Assert.Null(host.Image);
        Assert.Null(collection.LastUpdate);
    }

    [Fact]
    public async Task Restores_a_primary_image_whose_local_file_was_deleted_outside_Jellyfin()
    {
        using var files = new TestFiles();
        var path = files.PathFor("custom.jpg");
        File.WriteAllText(path, "custom poster");
        var collection = new TestCollection
        {
            Id = Guid.NewGuid(),
            Name = "Festival picks",
            ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = path }]
        };
        using var host = new JellyfinBoundary(collection);
        File.Delete(path);

        await host.Library.EnsurePosterAsync(collection.Id, [], null, CancellationToken.None);

        Assert.NotEqual(path, Assert.Single(collection.ImageInfos).Path);
        Assert.Equal(ItemUpdateType.ImageUpdate, collection.LastUpdate);
        using var poster = DecodePoster(host);
        Assert.Contains(SKColors.White, poster.Pixels);
    }

    [Fact]
    public async Task Does_not_treat_a_filesystem_error_as_a_missing_primary_image()
    {
        using var files = new TestFiles();
        // This path cannot be probed; the filesystem reports an error, not absence.
        var path = files.PathFor(new string('a', 32768));
        Assert.ThrowsAny<IOException>(() => File.GetAttributes(path));
        var original = new ItemImageInfo { Type = ImageType.Primary, Path = path };
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks", ImageInfos = [original] };
        using var host = new JellyfinBoundary(collection);

        await host.Library.EnsurePosterAsync(collection.Id, [], null, CancellationToken.None);

        Assert.Same(original, Assert.Single(collection.ImageInfos));
        Assert.Null(host.Image);
    }

    [Fact]
    public async Task Does_not_redraw_a_generated_poster_after_the_collection_is_renamed()
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        using var host = new JellyfinBoundary(collection);
        await host.Library.EnsurePosterAsync(collection.Id, [], null, CancellationToken.None);
        var original = host.Image;
        Assert.NotNull(original);

        collection.Name = "My festival";
        await host.Library.EnsurePosterAsync(collection.Id, [], null, CancellationToken.None);

        Assert.Same(original, host.Image);
    }

    [Fact]
    public async Task Keeps_a_custom_poster_registered_while_the_generated_poster_is_being_prepared()
    {
        using var files = new TestFiles();
        const string customPoster = "custom poster bytes";
        var path = files.PathFor("custom.png");
        File.WriteAllText(path, customPoster);
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        var updated = new TestCollection
        {
            Id = collection.Id,
            Name = collection.Name,
            ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = path }]
        };
        using var host = new JellyfinBoundary(collection, updated);

        await host.Library.EnsurePosterAsync(collection.Id, [], null, CancellationToken.None);

        Assert.Equal(path, Assert.Single(updated.ImageInfos).Path);
        Assert.Equal(customPoster, File.ReadAllText(path));
        Assert.Empty(collection.ImageInfos);
        Assert.Null(host.Image);
        Assert.Null(collection.LastUpdate);
    }

    private static SKBitmap DecodePoster(JellyfinBoundary host)
    {
        var poster = SKBitmap.Decode(host.Image);
        Assert.NotNull(poster);
        return poster;
    }

    private static byte[] WrapInIcon(byte[] png)
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        writer.Write((ushort)0); // Reserved
        writer.Write((ushort)1); // ICO
        writer.Write((ushort)1); // One frame
        writer.Write((byte)24);
        writer.Write((byte)12);
        writer.Write((ushort)0); // Palette size and reserved byte
        writer.Write((ushort)1); // Color planes
        writer.Write((ushort)32); // Bits per pixel
        writer.Write(png.Length);
        writer.Write(22); // Frame offset
        writer.Write(png);
        return data.ToArray();
    }

    private static byte[] EncodeSolidImage(int width)
    {
        using var bitmap = new SKBitmap(width, 12);
        bitmap.Erase(SKColors.OrangeRed);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static bool HasWhitePixels(SKBitmap poster, int row) =>
        Enumerable.Range(0, poster.Width).Any(x => poster.GetPixel(x, row) == SKColors.White);

    private static int InkWidth(SKBitmap poster)
    {
        return Ink(poster).Max(pixel => pixel.X) + 1;
    }

    private static HashSet<(int X, int Y)> Ink(SKBitmap poster, int maxWidth = int.MaxValue)
    {
        var ink = poster.Pixels.Select((color, i) => (color, X: i % poster.Width, Y: i / poster.Width))
            .Where(pixel => pixel.color.Red >= 128)
            .Select(pixel => (pixel.X, pixel.Y))
            .ToArray();
        var left = ink.Min(pixel => pixel.X);
        var selected = ink.Where(pixel => pixel.X - left < maxWidth).ToArray();
        var top = selected.Min(pixel => pixel.Y);
        return selected.Select(pixel => (pixel.X - left, pixel.Y - top)).ToHashSet();
    }

    private sealed class JellyfinBoundary : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly TestFiles _files = new();

        public JellyfinBoundary(TestCollection collection, TestCollection? afterFirstRead = null, HttpClient? imageHttp = null,
            PluginConfiguration? config = null)
        {
            var firstRead = true;
            var services = new ServiceCollection();
            services.AddSingleton(HostService<ILibraryManager>.Create((method, arguments) => method.Name switch
            {
                nameof(ILibraryManager.GetItemById) when Equals(arguments[0], collection.Id) => ReadCollection(),
                _ => throw new NotSupportedException(method.Name)
            }));
            services.AddSingleton(HostService<ICollectionManager>.Create((method, _) =>
                throw new NotSupportedException(method.Name)));
            services.AddSingleton(HostService<IProviderManager>.Create((method, arguments) => method.Name switch
            {
                nameof(IProviderManager.SaveImage) => SaveImageAsync(
                    (BaseItem)arguments[0]!, (Stream)arguments[1]!, (string)arguments[2]!, (ImageType)arguments[3]!),
                _ => throw new NotSupportedException(method.Name)
            }));
            new ServiceRegistrator().RegisterServices(services, null!);
            if (imageHttp is not null)
            {
                services.AddSingleton(new CollectionImageClient(imageHttp));
            }
            var testConfig = config ?? new PluginConfiguration();
            services.AddSingleton<Func<PluginConfiguration>>(() => testConfig);

            _services = services.BuildServiceProvider();

            TestCollection ReadCollection()
            {
                var current = firstRead ? collection : afterFirstRead ?? collection;
                firstRead = false;
                return current;
            }
        }

        public ICollectionLibrary Library => _services.GetRequiredService<ICollectionLibrary>();
        public string? MimeType { get; private set; }
        public byte[]? Image { get; private set; }

        public void Dispose()
        {
            _services.Dispose();
            _files.Dispose();
        }

        private async Task SaveImageAsync(BaseItem item, Stream stream, string mimeType, ImageType type)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Image = copy.ToArray();
            MimeType = mimeType;
            var path = _files.PathFor("poster.png");
            await File.WriteAllBytesAsync(path, Image);
            item.SetImage(new ItemImageInfo { Path = path, Type = type }, 0);
        }
    }

    private sealed class TestFiles : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("magiccollections-test-");

        public string PathFor(string fileName) => Path.Combine(_directory.FullName, fileName);

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class TestCollection : BoxSet
    {
        public ItemUpdateType? LastUpdate { get; private set; }

        public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
        {
            LastUpdate = updateReason;
            return Task.CompletedTask;
        }
    }

    private class HostService<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[], object?> _invoke = null!;

        public static T Create(Func<MethodInfo, object?[], object?> invoke)
        {
            var service = Create<T, HostService<T>>();
            ((HostService<T>)(object)service)._invoke = invoke;
            return service;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args!);
    }
}
