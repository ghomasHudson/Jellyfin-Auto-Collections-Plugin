using SkiaSharp;

namespace Jellyfin.Plugin.MagicCollections;

internal sealed class CollectionFaviconClient(HttpClient http)
{
    private const int MaxBytes = 64 * 1024;
    private const int MaxDimension = 512;

    public async Task<SKBitmap?> TryLoadAsync(IReadOnlyList<Uri> urls, CancellationToken cancellationToken)
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
                if (await TryDownloadAsync(url, timeout.Token).ConfigureAwait(false) is { } icon)
                {
                    return icon;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // Try the next candidate. Posters don't depend on the favicon being available.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<SKBitmap?> TryDownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        // The source HTTP client does not follow redirects from the handler-approved URL.
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxBytes + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        return length > MaxBytes ? null : Decode(buffer, length);
    }

    private static SKBitmap? Decode(byte[] buffer, int length)
    {
        using var data = new MemoryStream(buffer, 0, length, writable: false);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width is <= 0 or > MaxDimension || codec.Info.Height is <= 0 or > MaxDimension)
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
