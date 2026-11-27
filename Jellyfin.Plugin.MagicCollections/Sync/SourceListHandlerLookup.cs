namespace Jellyfin.Plugin.MagicCollections.Sync;

internal static class SourceListHandlerLookup
{
    public static (Uri Source, ISourceListHandler Handler)? Find(IEnumerable<ISourceListHandler> handlers, string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var source) &&
            handlers.FirstOrDefault(handler => handler.Supports(source)) is { } handler
            ? (source, handler)
            : null;
}
