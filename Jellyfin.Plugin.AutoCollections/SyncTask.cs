using Jellyfin.Plugin.AutoCollections.Sync;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCollections;

public sealed class SyncTask(CollectionSync sync, ILogger<SyncTask> logger) : IScheduledTask
{
    public string Name => "Sync Auto Collections";
    public string Description => "Update managed collections from configured source list URLs.";
    public string Category => "Auto Collections";
    public string Key => "AutoCollectionsSync";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new() { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Auto Collections is not loaded.");
        await sync.RunAsync(plugin.Configuration.SourceUrls,
            url => logger.LogWarning("Unsupported source list URL: {SourceUrl}", url),
            cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
