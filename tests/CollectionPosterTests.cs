using System.Reflection;
using Jellyfin.Plugin.MagicCollections;
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
    [Fact]
    public async Task Saves_a_black_portrait_PNG_with_white_title_text_as_the_primary_image()
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        using var host = new JellyfinBoundary(collection);

        await host.Library.EnsurePosterAsync(collection.Id, CancellationToken.None);

        Assert.Equal("image/png", host.MimeType);
        Assert.Equal(ImageType.Primary, Assert.Single(collection.ImageInfos).Type);
        Assert.Equal(ItemUpdateType.ImageUpdate, collection.LastUpdate);
        using var poster = DecodePoster(host);
        Assert.Equal(poster.Width * 3, poster.Height * 2);
        Assert.Equal(SKColors.Black, poster.GetPixel(0, 0));
        Assert.Equal(SKColors.Black, poster.GetPixel(poster.Width - 1, poster.Height - 1));
        Assert.Contains(SKColors.White, poster.Pixels);
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

        await host.Library.EnsurePosterAsync(collection.Id, CancellationToken.None);

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

        await host.Library.EnsurePosterAsync(collection.Id, CancellationToken.None);

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

        await host.Library.EnsurePosterAsync(title.Id, CancellationToken.None);
        await comparison.Library.EnsurePosterAsync(repeated.Id, CancellationToken.None);

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

        await host.Library.EnsurePosterAsync(joined.Id, CancellationToken.None);
        await comparison.Library.EnsurePosterAsync(unjoined.Id, CancellationToken.None);

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

        await host.Library.EnsurePosterAsync(rtl.Id, CancellationToken.None);
        await comparison.Library.EnsurePosterAsync(ltr.Id, CancellationToken.None);

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
        using var host = new JellyfinBoundary(collection);

        await host.Library.EnsurePosterAsync(collection.Id, CancellationToken.None);

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

        await host.Library.EnsurePosterAsync(collection.Id, CancellationToken.None);

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

        await host.Library.EnsurePosterAsync(collection.Id, CancellationToken.None);

        Assert.Same(original, Assert.Single(collection.ImageInfos));
        Assert.Null(host.Image);
    }

    [Fact]
    public async Task Does_not_redraw_a_generated_poster_after_the_collection_is_renamed()
    {
        var collection = new TestCollection { Id = Guid.NewGuid(), Name = "Festival picks" };
        using var host = new JellyfinBoundary(collection);
        await host.Library.EnsurePosterAsync(collection.Id, CancellationToken.None);
        var original = host.Image;
        Assert.NotNull(original);

        collection.Name = "My festival";
        await host.Library.EnsurePosterAsync(collection.Id, CancellationToken.None);

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

        await host.Library.EnsurePosterAsync(collection.Id, CancellationToken.None);

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

        public JellyfinBoundary(TestCollection collection, TestCollection? afterFirstRead = null)
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
