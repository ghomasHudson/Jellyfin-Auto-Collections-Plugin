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
    public bool Validate([FromBody] SourceListUrlRequest request)
    {
        if (request.RadarrBaseUrls is null && request.SonarrBaseUrls is null)
        {
            return SourceListHandlerLookup.Find(handlers, request.Url) is not null;
        }

        // The dashboard already has the current draft; do not look up saved *arr credentials or call an *arr API.
        return SourceListHandlerLookup.Find(handlers, request.Url, (handler, url) => handler is ArrHandler arr
            ? arr.SupportsDraft(url, request.RadarrBaseUrls, request.SonarrBaseUrls)
            : handler.Supports(url)) is not null;
    }
}

public sealed record SourceListUrlRequest(string? Url, string[]? RadarrBaseUrls = null, string[]? SonarrBaseUrls = null);
