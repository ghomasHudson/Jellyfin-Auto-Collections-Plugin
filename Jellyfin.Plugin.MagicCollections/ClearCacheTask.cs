using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MagicCollections;

public sealed class ClearCacheTask(LetterboxdHandler letterboxd) : IScheduledTask
{
    public string Name => "Clear Magic Collections Cache";
    public string Description => "Clear saved Letterboxd film IDs without changing collections. The next sync will fetch the IDs again.";
    public string Category => "Magic Collections";
    public string Key => "MagicCollectionsClearCache";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await letterboxd.ClearCacheAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
