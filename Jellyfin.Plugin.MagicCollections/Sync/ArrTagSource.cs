using Jellyfin.Plugin.MagicCollections.Configuration;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal enum ArrService { Radarr, Sonarr }

internal interface IArrSourceListHandler
{
    ArrService Service { get; }
}

internal sealed class ArrTagSource(ArrService service, Uri baseUrl, string label, string apiKey)
{
    public ArrService Service { get; } = service;
    public Uri BaseUrl { get; } = baseUrl;
    public string Label { get; } = label;
    public string ApiKey { get; } = apiKey;
    public Uri CanonicalUrl => new(BaseUrl, Uri.EscapeDataString(Label));

    public static ArrTagSource? Resolve(Uri url, PluginConfiguration configuration, ArrService expectedService)
    {
        var match = Match(url, SavedBases(configuration.RadarrServers, ArrService.Radarr)
            .Concat(SavedBases(configuration.SonarrServers, ArrService.Sonarr)), expectedService);
        if (match is not { } source ||
            source.Value?.ApiKey?.Trim() is not { Length: > 0 } key || key.Any(c => c is < '!' or > '~'))
        {
            return null;
        }
        return new(expectedService, source.BaseUrl, source.Label, key);
    }

    public static bool SupportsDraft(Uri url, string[]? radarrBases, string[]? sonarrBases, ArrService expectedService) =>
        Match(url, (radarrBases ?? []).Select(value => (ArrService.Radarr, (string?)value, true))
            .Concat((sonarrBases ?? []).Select(value => (ArrService.Sonarr, (string?)value, true))), expectedService) is not null;

    private static IEnumerable<(ArrService Service, string? BaseUrl, ArrServerConfiguration? Value)> SavedBases(
        ArrServerConfiguration[]? servers, ArrService service) => (servers ?? []).Select(server => (service, server?.BaseUrl, server));

    private static (Uri BaseUrl, string Label, T Value)? Match<T>(Uri url,
        IEnumerable<(ArrService Service, string? BaseUrl, T Value)> bases, ArrService expectedService)
    {
        if (GetLocation(url) is not { } location)
        {
            return null;
        }

        (Uri BaseUrl, string Label, T Value)? match = null;
        foreach (var (service, rawBaseUrl, value) in bases)
        {
            if (!Uri.TryCreate(rawBaseUrl?.Trim(), UriKind.Absolute, out var configuredUrl) ||
                GetLocation(configuredUrl) is not { } configured || location.Origin != configured.Origin)
            {
                continue;
            }

            var basePath = configured.Path.TrimEnd('/') + "/";
            if (!location.Path.StartsWith(basePath, StringComparison.Ordinal))
            {
                continue;
            }

            var segment = location.Path[basePath.Length..];
            if (segment.Length == 0 || segment.Contains('/'))
            {
                continue;
            }

            // Any second matching row (even one without a usable key) makes the source ambiguous.
            if (match is not null || service != expectedService)
            {
                return null;
            }

            var label = Uri.UnescapeDataString(segment);
            if (string.IsNullOrWhiteSpace(label) || label.Any(char.IsControl))
            {
                return null;
            }
            match = (new(configured.Origin + basePath), label, value);
        }
        return match;
    }

    private static (string Origin, string Path)? GetLocation(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https") || url.Host.Length == 0 ||
            !url.IsWellFormedOriginalString() || url.UserInfo.Length != 0 ||
            url.Query.Length != 0 || url.Fragment.Length != 0)
        {
            return null;
        }

        // Uri normalizes dot segments before exposing AbsolutePath. Check the original path first
        // so a URL cannot contain extra segments that disappear before matching a configured base.
        var original = url.OriginalString.AsSpan();
        var delimiter = original.IndexOf("://", StringComparison.Ordinal);
        if (delimiter < 0)
        {
            return null;
        }
        var authorityAndPath = original[(delimiter + 3)..];
        var slash = authorityAndPath.IndexOf('/');
        var authority = slash < 0 ? authorityAndPath : authorityAndPath[..slash];
        var rawPath = slash < 0 ? string.Empty : authorityAndPath[slash..].ToString();
        if (authority.Contains('@') || rawPath.Contains("//", StringComparison.Ordinal) ||
            rawPath.Split('/').Any(segment => Uri.UnescapeDataString(segment) is "." or ".."))
        {
            return null;
        }

        var origin = new UriBuilder(url.Scheme, url.IdnHost, url.IsDefaultPort ? -1 : url.Port)
            .Uri.GetLeftPart(UriPartial.Authority);
        // Equivalent spellings of an escaped byte should not affect matching; literal path case does.
        var path = string.Create(url.AbsolutePath.Length, url.AbsolutePath, (result, value) =>
        {
            value.AsSpan().CopyTo(result);
            for (var index = 0; index < result.Length - 2; index++)
            {
                if (result[index] == '%')
                {
                    result[index + 1] = char.ToUpperInvariant(result[index + 1]);
                    result[index + 2] = char.ToUpperInvariant(result[index + 2]);
                    index += 2;
                }
            }
        });
        return (origin, path);
    }
}
