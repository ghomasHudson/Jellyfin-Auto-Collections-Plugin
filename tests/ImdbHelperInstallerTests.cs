using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Jellyfin.Plugin.MagicCollections.Sync;
using Xunit;

namespace MagicCollections.Tests;

public sealed class ImdbHelperInstallerTests : IDisposable
{
    private static readonly Uri Asset = new("https://github.com/example/repo/releases/download/v1/helper.zip");
    private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory();

    [Fact]
    public async Task InstallsOnlyOnDemandAndReusesCompleteRuntime()
    {
        var payload = Archive();
        var requests = 0;
        using var client = Client(async (_, ct) =>
        {
            Interlocked.Increment(ref requests);
            await Task.Delay(30, ct);
            return Download(payload);
        });
        var install = Installer(client, payload);
        Assert.Equal(0, requests);
        var results = await Task.WhenAll(install.GetRuntimeAsync(default), install.GetRuntimeAsync(default));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, requests);
        Assert.True(File.Exists(Path.Combine(results[0], ImdbHelperInstaller.ReadyFile)));
        Assert.True(File.Exists(Path.Combine(results[0], ImdbHelperInstaller.PythonPath)));
        if (OperatingSystem.IsLinux())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(results[0], ImdbHelperInstaller.PythonPath)).HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(Path.Combine(results[0], ImdbHelperInstaller.ScriptPath)).HasFlag(UnixFileMode.UserExecute));
        }
        Assert.Equal(results[0], await Installer(client, payload).GetRuntimeAsync(default));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task RejectsWrongChecksumAndCanRetry()
    {
        var payload = Archive();
        var wrong = payload.ToArray();
        wrong[0] ^= 1;
        var requests = 0;
        using var client = Client((_, _) => Task.FromResult(Download(++requests == 1 ? wrong : payload)));
        var install = Installer(client, payload);
        await Assert.ThrowsAsync<InvalidDataException>(() => install.GetRuntimeAsync(default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_data.FullName, "magic-collections", "imdb-helper")));
        Assert.True(Directory.Exists(await install.GetRuntimeAsync(default)));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task ChangingTheReleasePinInstallsTheNewRuntimeInASeparateDirectory()
    {
        var oldPayload = Archive();
        var newPayload = Archive("licenses/notice.txt", 0x81A4);
        var requests = 0;
        using var client = Client((_, _) => Task.FromResult(Download(++requests == 1 ? oldPayload : newPayload)));
        var oldPath = await Installer(client, oldPayload).GetRuntimeAsync(default);
        var newPath = await Installer(client, newPayload).GetRuntimeAsync(default);
        Assert.NotEqual(oldPath, newPath);
        Assert.True(Directory.Exists(oldPath));
        Assert.True(File.Exists(Path.Combine(newPath, "licenses", "notice.txt")));
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("../outside", 0x81A4)]
    [InlineData("python/link", 0xA1FF)]
    [InlineData(".ready", 0x81A4)]
    public async Task RejectsUnsafeArchiveEntriesEvenWithMatchingChecksum(string name, int mode)
    {
        var payload = Archive(name, mode);
        using var client = Client((_, _) => Task.FromResult(Download(payload)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(client, payload).GetRuntimeAsync(default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_data.FullName, "magic-collections", "imdb-helper")));
    }

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/asset", true)]
    [InlineData("https://not-github.example/asset", false)]
    [InlineData("http://release-assets.githubusercontent.com/asset", false)]
    public async Task OnlyFollowsApprovedAssetRedirect(string location, bool allowed)
    {
        var payload = Archive();
        var seen = new List<Uri>();
        using var client = Client((request, _) =>
        {
            seen.Add(request.RequestUri!);
            if (request.RequestUri != Asset)
                return Task.FromResult(Download(payload));
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        });
        var install = Installer(client, payload);
        if (allowed)
        {
            await install.GetRuntimeAsync(default);
            Assert.Equal([Asset, new Uri(location)], seen);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => install.GetRuntimeAsync(default));
            Assert.Equal([Asset], seen);
        }
    }

    [Fact]
    public async Task CancellingAnInstallLeavesNoPartialRuntime()
    {
        var payload = Archive();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        });
        using var cancel = new CancellationTokenSource();
        var attempt = Installer(client, payload).GetRuntimeAsync(cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_data.FullName, "magic-collections", "imdb-helper")));
    }

    [Theory]
    [InlineData(126, "env: private path: Permission denied", "Check that its runtime user can traverse the directories")]
    [InlineData(127, "env: private path: No such file or directory", "executable or one of its runtime dependencies could not be loaded")]
    [InlineData(1, "IMDb sandboxed browser could not start.", "IMDb sandboxed browser could not start.")]
    [InlineData(1, "IMDb navigation failed.", "IMDb navigation failed.")]
    [InlineData(1, "private path\nIMDb navigation failed.", "The IMDb helper did not return a page.")]
    public async Task Renderer_reports_known_failures_without_exposing_raw_stderr(int exitCode, string stderr, string reason)
    {
        if (!OperatingSystem.IsLinux())
            return;

        // The stock container can start as root; the renderer then runs the helper as nobody.
        File.SetUnixFileMode(_data.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        var payload = Archive(python: $"#!/bin/sh\nprintf '%s\\n' '{stderr}' >&2\nexit {exitCode}\n");
        using var client = Client((_, _) => Task.FromResult(Download(payload)));
        var renderer = new BundledImdbPageRenderer(Installer(client, payload));
        var error = await Assert.ThrowsAsync<SourceListReadException>(() =>
            renderer.RenderAsync(new("https://www.imdb.com/list/ls055592025/"), default));

        Assert.Contains(reason, error.Reason);
        Assert.DoesNotContain("private path", error.Reason);
    }

    public void Dispose() => _data.Delete(recursive: true);

    private ImdbHelperInstaller Installer(HttpClient client, byte[] payload) =>
        new(client, _data.FullName, () => new(Asset, payload.Length, Convert.ToHexStringLower(SHA256.HashData(payload))));

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new FakeHandler(handler));

    private static HttpResponseMessage Download(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

    private static byte[] Archive(string? extra = null, int mode = 0, string python = "test")
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { ImdbHelperInstaller.PythonPath, ImdbHelperInstaller.ScriptPath,
                         ImdbHelperInstaller.DriverPath, "native/lib.so",
                         "browsers/chromium_headless_shell-123/chrome-headless-shell-linux64/chrome-headless-shell" })
                Add(zip, name, 0x81ED, name == ImdbHelperInstaller.PythonPath ? python : "test");
            if (extra is not null)
                Add(zip, extra, mode);
        }
        return bytes.ToArray();
    }

    private static void Add(ZipArchive zip, string name, int mode, string contents = "test")
    {
        var entry = zip.CreateEntry(name);
        entry.ExternalAttributes = mode << 16;
        using var writer = new StreamWriter(entry.Open());
        writer.Write(contents);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handler(request, ct);
    }
}
