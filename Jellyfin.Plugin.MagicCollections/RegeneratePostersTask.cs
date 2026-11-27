using Jellyfin.Plugin.MagicCollections.Configuration;
using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MagicCollections;

public sealed class RegeneratePostersTask(IEnumerable<ISourceListHandler> handlers, ICollectionLibrary library,
    Func<PluginConfiguration> getConfiguration, ILogger<RegeneratePostersTask> logger) : IScheduledTask
{
    public string Name => "Regenerate Magic Collections Posters";
    public string Description => "Replace posters, including custom artwork, for managed collections using the current Poster settings.";
    public string Category => "Magic Collections";
    public string Key => "MagicCollectionsRegeneratePosters";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = getConfiguration();
        if (config.HasPosterElements)
        {
            foreach (var (id, tags) in library.GetCollectionsWithTag(CollectionSync.ManagedCollectionTag))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sources = FindSources(tags).Take(2).ToArray();
                if (sources.Length != 1)
                {
                    logger.LogWarning("Skipping poster regeneration for collection {CollectionId}: expected one supported source list URL tag", id);
                    continue;
                }

                var (url, handler) = sources[0];
                SourceList? list = null;
                if (config.PosterBackgroundImage || config.PosterIcon)
                {
                    try
                    {
                        list = await handler.ReadAsync(url, cancellationToken).ConfigureAwait(false);
                    }
                    catch (SourceListReadException ex)
                    {
                        logger.LogWarning(ex, "Skipping poster regeneration for source list {SourceUrl}", url);
                        continue;
                    }
                }

                await library.RegeneratePosterAsync(id, list?.FaviconUrls ?? [], list?.ImageUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        progress.Report(100);
    }

    private IEnumerable<(Uri Url, ISourceListHandler Handler)> FindSources(IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            if (Uri.TryCreate(tag, UriKind.Absolute, out var url) &&
                handlers.FirstOrDefault(handler => handler.Supports(url)) is { } handler)
            {
                yield return (url, handler);
            }
        }
    }
}
