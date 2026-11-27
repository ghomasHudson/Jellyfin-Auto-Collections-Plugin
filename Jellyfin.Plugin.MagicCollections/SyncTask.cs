using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using MagicCollectionsPlugin = Jellyfin.Plugin.MagicCollections.Plugin;

// Jellyfin derives the task ID for saved triggers and history from the full type name.
namespace Jellyfin.Plugin.AutoCollections;

public sealed class SyncTask(CollectionSync sync, ILogger<SyncTask> logger) : IScheduledTask
{
    public string Name => "Sync Magic Collections";
    public string Description => "Update managed collections from configured source list URLs.";
    public string Category => "Magic Collections";
    public string Key => "AutoCollectionsSync";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new() { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = MagicCollectionsPlugin.Instance ?? throw new InvalidOperationException("Magic Collections is not loaded.");
        var config = plugin.Configuration;
        await sync.RunAsync(config.SourceUrls,
            url => logger.LogWarning("Unsupported source list URL: {SourceUrl}", url),
            cancellationToken, config.VerboseLogging ? logger : null).ConfigureAwait(false);
        progress.Report(100);
    }
}
