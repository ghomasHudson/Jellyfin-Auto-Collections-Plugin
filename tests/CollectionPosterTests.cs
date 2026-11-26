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
        bool HasWhitePixels(int row) => Enumerable.Range(0, poster.Width).Any(x => poster.GetPixel(x, row) == SKColors.White);
        Assert.Contains(Enumerable.Range(poster.Height / 2 - 130, 80), HasWhitePixels);
        Assert.Contains(Enumerable.Range(poster.Height / 2 + 50, 80), HasWhitePixels);
    }

    [Theory]
    [InlineData("/collections/custom.jpg")]
    [InlineData("https://example.com/collection.jpg")]
    public async Task Does_not_replace_an_existing_primary_image(string path)
    {
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

    private static SKBitmap DecodePoster(JellyfinBoundary host)
    {
        var poster = SKBitmap.Decode(host.Image);
        Assert.NotNull(poster);
        return poster;
    }

    private sealed class JellyfinBoundary : IDisposable
    {
        private readonly ServiceProvider _services;

        public JellyfinBoundary(TestCollection collection)
        {
            var services = new ServiceCollection();
            services.AddSingleton(HostService<ILibraryManager>.Create((method, arguments) => method.Name switch
            {
                nameof(ILibraryManager.GetItemById) when Equals(arguments[0], collection.Id) => collection,
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
        }

        public ICollectionLibrary Library => _services.GetRequiredService<ICollectionLibrary>();
        public string? MimeType { get; private set; }
        public byte[]? Image { get; private set; }

        public void Dispose() => _services.Dispose();

        private async Task SaveImageAsync(BaseItem item, Stream stream, string mimeType, ImageType type)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Image = copy.ToArray();
            MimeType = mimeType;
            item.SetImage(new ItemImageInfo { Path = "poster.png", Type = type }, 0);
        }
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
