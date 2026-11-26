using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MagicCollections.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MagicCollections/LetterboxdCache")]
public sealed class LetterboxdCacheController(LetterboxdHandler handler) : ControllerBase
{
    [HttpDelete]
    public async Task<IActionResult> ClearLetterboxdCache(CancellationToken cancellationToken)
    {
        await handler.ClearCacheAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
