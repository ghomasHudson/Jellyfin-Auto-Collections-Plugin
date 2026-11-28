using Jellyfin.Plugin.MagicCollections.Configuration;

namespace Jellyfin.Plugin.MagicCollections.Sync;

internal sealed record ArrServiceDefinition(
    ArrService Service, string MediaEndpoint, SourceMediaType MediaType,
    string PrimaryIdField, string PrimaryProvider, string? OptionalTmdbIdField = null)
{
    public static ArrServiceDefinition Radarr { get; } =
        new(ArrService.Radarr, "api/v3/movie", SourceMediaType.Movie, "tmdbId", "Tmdb");
    public static ArrServiceDefinition Sonarr { get; } =
        new(ArrService.Sonarr, "api/v3/series", SourceMediaType.Series, "tvdbId", "Tvdb", "tmdbId");
}

internal sealed class ArrHandler(HttpClient http, Func<PluginConfiguration> configuration, ArrServiceDefinition definition)
    : ISourceListHandler
{
    private static readonly Uri UnresolvedSource = new("about:blank");

    public string SourceTag => definition.Service.ToString();

    public bool Supports(Uri url) => Resolve(url) is not null;

    public bool SupportsDraft(Uri url, string[]? radarrBases, string[]? sonarrBases) =>
        ArrTagSource.SupportsDraft(url, radarrBases, sonarrBases, definition.Service);

    public Uri GetCanonicalUrl(Uri url) => RequireSource(url).CanonicalUrl;

    public Task<SourceList> ReadAsync(Uri url, CancellationToken cancellationToken) =>
        ArrTagReader.ReadAsync(http, RequireSource(url), definition, cancellationToken);

    public ArrHandler Snapshot(PluginConfiguration? snapshot)
    {
        var settings = snapshot ?? configuration();
        return new(http, () => settings, definition);
    }

    private ArrTagSource? Resolve(Uri url) => ArrTagSource.Resolve(url, configuration(), definition.Service);

    private ArrTagSource RequireSource(Uri url) =>
        Resolve(url) ?? throw new SourceListReadException(UnresolvedSource,
            $"Not a tag URL for a uniquely configured {SourceTag} instance.");
}
