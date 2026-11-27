using System.Buffers;
using SkiaSharp;

namespace Jellyfin.Plugin.MagicCollections;

internal sealed class CollectionImageClient(HttpClient http)
{
    public Task<SKBitmap?> TryLoadFaviconAsync(IReadOnlyList<Uri> urls, CancellationToken cancellationToken) =>
        TryLoadAsync(urls, maxBytes: 64 * 1024, maxDimension: 512, cancellationToken);

    public Task<SKBitmap?> TryLoadListImageAsync(Uri? url, CancellationToken cancellationToken) =>
        TryLoadAsync(url is null ? [] : [url], maxBytes: 4 * 1024 * 1024, maxDimension: 4096, cancellationToken);

    private async Task<SKBitmap?> TryLoadAsync(
        IReadOnlyList<Uri> urls, int maxBytes, int maxDimension, CancellationToken cancellationToken)
    {
        if (urls.Count == 0)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        foreach (var url in urls)
        {
            try
            {
                if (await TryDownloadAsync(url, maxBytes, maxDimension, timeout.Token).ConfigureAwait(false) is { } bitmap)
                {
                    return bitmap;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // Try the next candidate. Source images aren't required to generate a poster.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<SKBitmap?> TryDownloadAsync(Uri url, int maxBytes, int maxDimension, CancellationToken cancellationToken)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0)
        {
            return null;
        }

        // The source HTTP client does not follow redirects from the handler-approved URL.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(Plugin.HttpUserAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(maxBytes + 1);
        try
        {
            var length = await stream.ReadAtLeastAsync(buffer.AsMemory(0, maxBytes + 1), maxBytes + 1,
                throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return length > maxBytes ? null : Decode(buffer, length, maxDimension);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static SKBitmap? Decode(byte[] buffer, int length, int maxDimension)
    {
        using var data = new MemoryStream(buffer, 0, length, writable: false);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Width > maxDimension ||
            codec.Info.Height <= 0 || codec.Info.Height > maxDimension)
        {
            return null;
        }

        var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) == SKCodecResult.Success)
        {
            return bitmap;
        }

        bitmap.Dispose();
        return null;
    }
}
