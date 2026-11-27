using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MagicCollections.Configuration;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("MagicCollections/Trakt")]
public sealed class TraktConnectionController(TraktConnection connection) : ControllerBase
{
    [HttpGet("Status")]
    public Task<IActionResult> Status(CancellationToken cancellationToken) =>
        RunAsync(_ => connection.StatusAsync(cancellationToken));

    [HttpPost("Start")]
    public Task<IActionResult> Start(CancellationToken cancellationToken) =>
        RunAsync(session => connection.StartAsync(session, cancellationToken));

    [HttpPost("Complete")]
    [RequestSizeLimit(16_384)]
    public Task<IActionResult> Complete([FromBody] TraktCallbackResponse response, CancellationToken cancellationToken) =>
        RunAsync(session => connection.CompleteAsync(session, response, cancellationToken));

    [HttpPost("Disconnect")]
    public Task<IActionResult> Disconnect(CancellationToken cancellationToken) =>
        RunAsync(_ => connection.DisconnectAsync(cancellationToken));

    private async Task<IActionResult> RunAsync<T>(Func<TraktAdminSession, Task<T>> action)
    {
        // Claim types supplied by Jellyfin 10.11's CustomAuthenticationHandler.
        if (User.Identity?.IsAuthenticated != true ||
            !string.Equals(User.FindFirst("Jellyfin-IsApiKey")?.Value, "False", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) || userId == Guid.Empty ||
            User.FindFirst("Jellyfin-Token")?.Value is not { Length: > 0 } token)
        {
            return Forbid();
        }

        var session = new TraktAdminSession(userId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
        try
        {
            return Ok(await action(session).ConfigureAwait(false));
        }
        catch (TraktConnectionException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusCode(500, new { Error = "Could not access the Trakt connection store." });
        }
    }
}

public sealed record TraktCallbackResponse(string? Code, string? State);
public sealed record TraktSignIn(string AuthorizationUrl);
public sealed record TraktConnectionStatus(bool Connected, string? Account = null, string? Notice = null);

internal sealed record TraktAdminSession(Guid UserId, string TokenHash);
