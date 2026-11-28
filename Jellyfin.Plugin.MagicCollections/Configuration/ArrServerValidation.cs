using Jellyfin.Plugin.MagicCollections.Sync;

namespace Jellyfin.Plugin.MagicCollections.Configuration;

internal static class ArrServerValidation
{
    public static void EnsureValid(PluginConfiguration config)
    {
        var bases = new HashSet<(string Origin, string Path)>();
        if (CheckBases(config.RadarrServers?.Select(row => row?.BaseUrl),
            config.SonarrServers?.Select(row => row?.BaseUrl), bases) is { } error)
        {
            throw new ArgumentException($"{error.Service} server {error.Index + 1}: {error.Message}");
        }

        CheckKeys(ArrService.Radarr, config.RadarrServers);
        CheckKeys(ArrService.Sonarr, config.SonarrServers);

        if (bases.Count == 0)
        {
            return;
        }
        var urls = config.SourceUrls ?? [];
        for (var index = 0; index < urls.Length; index++)
        {
            if (ArrTagSource.IsDecoratedTagUrl(urls[index], bases))
            {
                throw new ArgumentException($"Source list URL {index + 1}: Remove credentials, query strings, and fragments from *arr tag URLs.");
            }
        }
    }

    public static ArrBaseError? FindInvalidBase(IEnumerable<string?>? radarrBases, IEnumerable<string?>? sonarrBases) =>
        CheckBases(radarrBases, sonarrBases, []);

    private static ArrBaseError? CheckBases(IEnumerable<string?>? radarrBases, IEnumerable<string?>? sonarrBases,
        HashSet<(string Origin, string Path)> seen)
    {
        return CheckServiceBases(ArrService.Radarr, radarrBases ?? []) ??
            CheckServiceBases(ArrService.Sonarr, sonarrBases ?? []);

        ArrBaseError? CheckServiceBases(ArrService service, IEnumerable<string?> bases)
        {
            var index = 0;
            foreach (var baseUrl in bases)
            {
                if (ArrTagSource.ParseBase(baseUrl) is not { } location)
                {
                    return new(service.ToString(), index,
                        "Enter a valid HTTP or HTTPS base URL without credentials, a query string, a fragment, or invalid path segments.");
                }
                if (!seen.Add(location))
                {
                    return new(service.ToString(), index, "This base URL is already used by another Radarr or Sonarr server.");
                }
                index++;
            }
            return null;
        }
    }

    private static void CheckKeys(ArrService service, ArrServerConfiguration[]? servers)
    {
        for (var index = 0; index < servers?.Length; index++)
        {
            if (ArrTagSource.ReadApiKey(servers[index]?.ApiKey) is null)
            {
                throw new ArgumentException($"{service} server {index + 1}: Enter an API key with no spaces or non-ASCII characters.");
            }
        }
    }
}

internal sealed record ArrBaseError(string Service, int Index, string Message);
