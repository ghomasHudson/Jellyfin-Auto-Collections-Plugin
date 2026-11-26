namespace Jellyfin.Plugin.AutoCollections.Sync;

public sealed class SourceListReadException(Uri source, string reason, Exception innerException)
    : Exception($"Could not read source list {source.AbsoluteUri}: {reason}", innerException);
