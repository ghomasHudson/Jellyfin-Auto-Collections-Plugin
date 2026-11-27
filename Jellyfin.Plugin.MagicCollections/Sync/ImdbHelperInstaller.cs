using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal sealed class ImdbHelperInstaller(HttpClient client, string dataPath, Func<ImdbHelperRelease> getRelease,
    ILogger<ImdbHelperInstaller>? logger = null)
{
    internal const string PythonPath = "python/bin/python3.12";
    internal const string ScriptPath = "render.py";
    internal const string DriverPath = "python/lib/python3.12/site-packages/playwright/driver/node";
    internal const string ReadyFile = ".ready";
    private const long MaxExpandedSize = 1024L * 1024 * 1024;
    private const int MaxEntries = 20_000;
    private const int BufferSize = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1);

    internal async Task<string> GetRuntimeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var release = getRelease();
        var root = Path.Combine(dataPath, "magic-collections", "imdb-helper");
        var runtime = Path.Combine(root, release.Sha256);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsReady(runtime, release.Sha256))
                return runtime;
            if (Directory.Exists(runtime) && !IsReady(runtime, release.Sha256))
                throw new IOException("The installed IMDb helper is incomplete. Remove its cached directory and retry.");
            CreateDirectory(root);
            var stage = Path.Combine(root, $".install-{Guid.NewGuid():N}");
            CreateDirectory(stage, shareReadAccess: false);
            try
            {
                var archive = Path.Combine(stage, "helper.zip");
                var payload = Path.Combine(stage, "runtime");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                try
                {
                    logger?.LogInformation("Downloading the IMDb helper ({DownloadBytes} bytes)", release.Size);
                    await DownloadAsync(release, archive, deadline.Token).ConfigureAwait(false);
                    await ExtractAsync(archive, payload, deadline.Token).ConfigureAwait(false);
                    if (!HasEntrypoints(payload))
                        throw new InvalidDataException("The IMDb helper archive is missing required files.");
                    await File.WriteAllTextAsync(Path.Combine(payload, ReadyFile), release.Sha256, deadline.Token)
                        .ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    try
                    {
                        Directory.Move(payload, runtime);
                    }
                    catch (IOException) when (IsReady(runtime, release.Sha256))
                    {
                        // Another process published this exact archive while we were preparing it.
                    }
                    logger?.LogInformation("The IMDb helper is ready");
                    return runtime;
                }
                catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("Downloading or unpacking the IMDb helper timed out.", e);
                }
            }
            finally
            {
                TryDelete(stage);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task DownloadAsync(ImdbHelperRelease release, string archive, CancellationToken ct)
    {
        using var response = await OpenAssetAsync(release.Url, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"IMDb helper download returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is long advertisedSize && advertisedSize != release.Size)
            throw new InvalidDataException("The IMDb helper download was not the expected size.");

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = File.Create(archive);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var size = await CopyAsync(source, destination, release.Size, hash, ct).ConfigureAwait(false);
        if (size != release.Size ||
            !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The IMDb helper did not match this plugin version's checksum.");
    }

    private async Task<HttpResponseMessage> OpenAssetAsync(Uri asset, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.MovedPermanently))
            return response;

        using (response)
        {
            var location = response.Headers.Location;
            if (location is null || !location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps ||
                !location.IsDefaultPort || location.UserInfo.Length != 0 ||
                location.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new HttpRequestException("GitHub redirected the IMDb helper download to an unexpected host.");

            using var redirect = new HttpRequestMessage(HttpMethod.Get, location);
            return await client.SendAsync(redirect, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
    }

    private static bool IsReady(string path, string hash)
    {
        var marker = Path.Combine(path, ReadyFile);
        return HasEntrypoints(path) && File.Exists(marker) && new FileInfo(marker).Length == hash.Length &&
            string.Equals(File.ReadAllText(marker), hash, StringComparison.Ordinal);
    }

    private static bool HasEntrypoints(string root)
    {
        var browsers = Path.Combine(root, "browsers");
        return File.Exists(Path.Combine(root, PythonPath)) && File.Exists(Path.Combine(root, ScriptPath)) &&
            File.Exists(Path.Combine(root, DriverPath)) && Directory.Exists(Path.Combine(root, "native")) &&
            Directory.Exists(browsers) &&
            Directory.EnumerateDirectories(browsers, "chromium_headless_shell-*")
                .Any(path => File.Exists(Path.Combine(path, "chrome-headless-shell-linux64", "chrome-headless-shell")));
    }

    private static void CreateDirectory(string path, bool shareReadAccess = true)
    {
        if (!OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(path);
            return;
        }

        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode readers = UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Directory.CreateDirectory(path, owner | (shareReadAccess ? readers : 0));
    }

    private static async Task ExtractAsync(string archive, string payload, CancellationToken ct)
    {
        CreateDirectory(payload);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > MaxEntries)
            throw new InvalidDataException("The IMDb helper archive contains too many files.");

        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName;
            var directory = name.EndsWith('/');
            var relative = directory ? name[..^1] : name;
            var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            var type = mode & 0xF000;
            if (relative == ReadyFile || relative.Contains('\\') || relative.Contains(':') ||
                relative.Split('/').Any(segment => segment is "" or "." or "..") ||
                (type != 0 && type != (directory ? 0x4000 : 0x8000)) ||
                entry.Length < 0 || entry.Length > MaxExpandedSize - expanded)
                throw new InvalidDataException("The IMDb helper archive contains an invalid file.");

            var target = Path.Combine(payload, relative);
            if (directory)
            {
                if (entry.Length != 0)
                    throw new InvalidDataException("The IMDb helper archive contains an invalid directory.");
                CreateDirectory(target);
                continue;
            }

            CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            {
                if (await CopyAsync(source, destination, entry.Length, null, ct).ConfigureAwait(false) != entry.Length)
                    throw new InvalidDataException("The IMDb helper archive contains a truncated file.");
            }
            expanded += entry.Length;

            // Ignore special/write mode bits from the archive. A root service reads the helper as a non-root user.
            if (OperatingSystem.IsLinux())
            {
                const UnixFileMode readOnly = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                const UnixFileMode executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(target, readOnly | (IsEntrypoint(relative) ? executable : 0));
            }
        }
    }

    private static bool IsEntrypoint(string path) =>
        path is PythonPath or DriverPath ||
        (path.StartsWith("browsers/chromium_headless_shell-", StringComparison.Ordinal) &&
         path.EndsWith("/chrome-headless-shell-linux64/chrome-headless-shell", StringComparison.Ordinal));

    private static async Task<long> CopyAsync(Stream source, Stream destination, long limit, IncrementalHash? hash, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long written = 0;
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) != 0)
            {
                if (count > limit - written)
                    throw new InvalidDataException("The IMDb helper data was larger than expected.");
                hash?.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                written += count;
            }
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
