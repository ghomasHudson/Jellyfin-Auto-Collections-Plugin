using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal sealed class BundledImdbPageRenderer(ImdbHelperInstaller installer) : IImdbPageRenderer
{
    private const int MaxReplyChars = 16 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1);

    public async Task<string> RenderAsync(Uri canonicalUrl, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var runtime = await installer.GetRuntimeAsync(ct).ConfigureAwait(false);
            return await RenderPageAsync(runtime, canonicalUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or
            HttpRequestException or UnauthorizedAccessException or TimeoutException)
        {
            throw new SourceListReadException(canonicalUrl, ex.Message);
        }
        catch (Win32Exception)
        {
            throw new SourceListReadException(canonicalUrl, "Could not launch the IMDb helper. Check that the Jellyfin data directory allows executable files.");
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            throw new SourceListReadException(canonicalUrl, "The IMDb helper returned an invalid reply.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> RenderPageAsync(string runtime, Uri url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var python = Path.Combine(runtime, ImdbHelperInstaller.PythonPath);
        var isRoot = GetEffectiveUserId() == 0;
        const string setpriv = "/usr/bin/setpriv";
        if (isRoot && !File.Exists(setpriv))
            throw new InvalidOperationException("The IMDb helper must run as a non-root user. Start Jellyfin as a non-root user on this host.");

        var profile = Directory.CreateTempSubdirectory("magic-collections-imdb-");
        try
        {
            const uint nobody = 65534;
            if (isRoot && ChangeOwner(profile.FullName, nobody, nobody) != 0)
                throw new IOException("Could not assign the IMDb browser its temporary directory.");
            return await RunInProfileAsync(profile.FullName, runtime, python, isRoot ? setpriv : null, url, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            try { profile.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> RunInProfileAsync(string profile, string runtime, string python,
        string? setpriv, Uri url, CancellationToken ct)
    {
        var start = new ProcessStartInfo(setpriv ?? python)
        {
            WorkingDirectory = profile,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
        };
        var libraryPath = $"{Path.Combine(runtime, "native")}:{Path.Combine(runtime, "python", "lib")}";
        if (setpriv is not null)
        {
            // Keep the sandbox enabled even when the stock image starts Jellyfin as root.
            foreach (var argument in new[] { "--reuid", "65534", "--regid", "65534", "--clear-groups",
                         "--inh-caps=-all", "--ambient-caps=-all", "--no-new-privs",
                         "/usr/bin/env", $"LD_LIBRARY_PATH={libraryPath}", python })
                start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("-s");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add("-P");
        start.ArgumentList.Add(Path.Combine(runtime, ImdbHelperInstaller.ScriptPath));
        start.ArgumentList.Add(url.AbsoluteUri);
        start.Environment.Clear();
        start.Environment["HOME"] = profile;
        start.Environment["TMPDIR"] = profile;
        start.Environment["PATH"] = "/usr/bin:/bin";
        start.Environment["PYTHONHOME"] = Path.Combine(runtime, "python");
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PLAYWRIGHT_BROWSERS_PATH"] = Path.Combine(runtime, "browsers");
        if (setpriv is null)
            start.Environment["LD_LIBRARY_PATH"] = libraryPath;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the IMDb helper.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        try
        {
            var reply = await ReadReplyAsync(process.StandardOutput, deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (reply.Length == 0)
                throw new InvalidDataException("The IMDb helper did not return a page. Check network access and whether this host supports Chromium sandboxing.");
            using var json = JsonDocument.Parse(reply);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var statusValue) ||
                statusValue.ValueKind != JsonValueKind.Number || !statusValue.TryGetInt32(out var status))
                throw new InvalidDataException("The IMDb helper did not report a page. Check browser sandbox and network access.");
            if (status != 200)
                throw new InvalidDataException($"IMDb returned HTTP {status}; no collection was updated from this page.");
            if (process.ExitCode != 0 ||
                !root.TryGetProperty("finalUrl", out var finalUrl) || finalUrl.ValueKind != JsonValueKind.String ||
                !string.Equals(finalUrl.GetString(), url.AbsoluteUri, StringComparison.Ordinal) ||
                !root.TryGetProperty("pageData", out var data) || data.ValueKind != JsonValueKind.String ||
                data.GetString() is not { Length: > 0 } pageData)
                throw new InvalidDataException("IMDb did not return the expected title data.");
            return pageData;
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Rendering IMDb timed out.", e);
        }
        finally
        {
            deadline.Cancel();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            try { await stderr.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    private static async Task<string> ReadReplyAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            var reply = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
            {
                if (count > MaxReplyChars - reply.Length)
                    throw new InvalidDataException("The IMDb helper reply was too large.");
                reply.Append(buffer, 0, count);
            }
            return reply.ToString();
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);
}
