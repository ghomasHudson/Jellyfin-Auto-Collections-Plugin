namespace Jellyfin.Plugin.MagicCollections.Sync;

public sealed class SourceListReadException(Uri source, string reason, Exception? innerException = null)
    : Exception($"Could not read source list {source.AbsoluteUri}: {reason}", innerException)
{
    internal string Reason { get; } = reason;
}
