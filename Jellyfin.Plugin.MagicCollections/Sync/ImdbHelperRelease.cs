using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal sealed record ImdbHelperRelease(Uri Url, long Size, string Sha256)
{
    internal const long MaxArchiveSize = 512L * 1024 * 1024;

    internal static ImdbHelperRelease ForCurrentPlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new InvalidOperationException("The IMDb helper is only available for Linux x64.");

        var assembly = typeof(ImdbHelperRelease).Assembly;
        var values = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        if (!values.TryGetValue("ImdbHelperSha256", out var hash) ||
            hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) ||
            !values.TryGetValue("ImdbHelperSize", out var sizeText) ||
            !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size is <= 0 or > MaxArchiveSize)
            throw new InvalidOperationException("This plugin build does not include IMDb helper download information.");

        var version = assembly.GetName().Version;
        return new ImdbHelperRelease(
            new Uri($"https://github.com/ghomasHudson/Jellyfin-Magic-Collections-Plugin/releases/download/v{version}/magic-collections-imdb-linux-x64.zip"),
            size, hash.ToLowerInvariant());
    }
}
