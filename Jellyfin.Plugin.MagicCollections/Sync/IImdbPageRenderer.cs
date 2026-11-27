namespace Jellyfin.Plugin.MagicCollections.Sync;

internal interface IImdbPageRenderer
{
    // Return only the text of script#__NEXT_DATA__, from a successful document at canonicalUrl.
    // Report navigation and extraction failures as SourceListReadException; preserve caller cancellation.
    Task<string> RenderAsync(Uri canonicalUrl, CancellationToken ct);
}
