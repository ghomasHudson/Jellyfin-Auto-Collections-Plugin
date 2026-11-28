using Jellyfin.Plugin.MagicCollections.Configuration;

namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class SonarrHandler(HttpClient http, Func<PluginConfiguration> configuration) : ISourceListHandler, IArrSourceListHandler
{
    public string SourceTag => "Sonarr";
    ArrService IArrSourceListHandler.Service => ArrService.Sonarr;

    public bool Supports(Uri url) => Resolve(url) is not null;

    public Uri GetCanonicalUrl(Uri url) => RequireSource(url).CanonicalUrl;

    public Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken) =>
        ArrTagReader.ReadAsync(http, RequireSource(url), cancellationToken);

    private ArrTagSource? Resolve(Uri url) => ArrTagSource.Resolve(url, configuration(), ArrService.Sonarr);

    private ArrTagSource RequireSource(Uri url) =>
        Resolve(url) ?? throw new SourceListReadException(UnresolvedSource, "Not a tag URL for a uniquely configured Sonarr instance.");

    private static readonly Uri UnresolvedSource = new("about:blank");
}
