using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MagicCollections.Configuration;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MagicCollections/SourceListUrl")]
public sealed class SourceListUrlController(IEnumerable<ISourceListHandler> handlers) : ControllerBase
{
    [HttpPost]
    public bool Validate([FromBody] SourceListUrlRequest request) =>
        JellyfinQuery.ParseSource(request.Url) is { } local
            ? local.Query is not null
            : SourceListHandlerLookup.Find(handlers, request.Url) is not null;
}

public sealed record SourceListUrlRequest(string? Url);
