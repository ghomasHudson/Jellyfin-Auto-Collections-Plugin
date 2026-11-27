using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.MagicCollections.Configuration;

public sealed class TraktConnection(HttpClient http, Func<PluginConfiguration> configuration,
    string dataPath, TimeProvider clock)
{
    private const string CallbackUrl = "https://ghomashudson.github.io/Jellyfin-Magic-Collections-Plugin/trakt/callback.html";
    private const string AuthOrigin = "https://auth.trakt.tv";
    private const string BundledClientId = "ciP9eCYn_QzrN3q_7LABgpl6B-wi5UIdOgvZz3Jg1Fc";
    private const string StoreName = "trakt-connection.json";
    private const string ReconnectNotice = "Trakt rejected the authorization. Use Connect Trakt in the Magic Collections settings.";
    private static readonly TimeSpan SignInLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private readonly string _directory = Path.Combine(dataPath, "magic-collections");
    private readonly SemaphoreSlim _gate = new(1);
    private PendingSignIn? _pending;

    internal Task<TraktConnectionStatus> StatusAsync(CancellationToken cancellationToken) =>
        LockedAsync(async () =>
        {
            var stored = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return stored switch
            {
                null => new(false),
                { NeedsReconnect: true } => new(false, stored.Account, ReconnectNotice),
                _ when stored.ClientId != ClientId() => new(false, stored.Account, "The Trakt Client ID changed. Connect Trakt again."),
                _ => new TraktConnectionStatus(true, stored.Account)
            };
        }, cancellationToken);

    internal Task<TraktSignIn> StartAsync(TraktAdminSession session, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            var clientId = ClientId();
            if (!ValidClientId(clientId))
            {
                throw new TraktConnectionException("Invalid Trakt Client ID in plugin settings.");
            }

            var verifier = RandomValue();
            var state = RandomValue();
            _pending = new(session, clientId, verifier, state, clock.GetUtcNow() + SignInLifetime);
            return Task.FromResult(new TraktSignIn(QueryHelpers.AddQueryString(AuthOrigin + "/oauth/authorize",
                new Dictionary<string, string?>
                {
                    ["client_id"] = clientId,
                    ["redirect_uri"] = CallbackUrl,
                    ["response_type"] = "code",
                    ["state"] = state,
                    ["code_challenge_method"] = "S256",
                    ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                })));
        }, cancellationToken);

    internal Task<TraktConnectionStatus> CompleteAsync(TraktAdminSession session, TraktCallbackResponse response,
        CancellationToken cancellationToken) =>
        LockedAsync(async () =>
        {
            if (_pending is not { } pending || pending.Session != session || pending.ClientId != ClientId() ||
                pending.ExpiresAt <= clock.GetUtcNow() || response.State != pending.State ||
                string.IsNullOrWhiteSpace(response.Code) || response.Code.Length > 4096)
            {
                throw new TraktConnectionException("This Trakt response does not match the current sign-in. Start again.");
            }

            _pending = null;
            var tokens = await RequestTokensAsync(pending.ClientId, new
            {
                grant_type = "authorization_code",
                client_id = pending.ClientId,
                redirect_uri = CallbackUrl,
                code = response.Code,
                code_verifier = pending.Verifier
            }, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.trakt.tv/users/settings");
            AddApiHeaders(request, pending.ClientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            var account = (await SendJsonAsync<AccountResponse>(request, "Trakt account lookup", cancellationToken).ConfigureAwait(false)).User;
            if (account?.Ids?.Slug is not { Length: > 0 and <= 256 } slug ||
                string.IsNullOrWhiteSpace(account.Username) || account.Username.Length > 256)
            {
                throw new TraktConnectionException("Trakt did not return an account name and list owner.");
            }

            if (pending.ClientId != ClientId())
            {
                throw new TraktConnectionException("The Trakt Client ID changed. Connect Trakt again.");
            }

            await SaveAsync(new(pending.ClientId, account.Username, slug, tokens.AccessToken!, tokens.RefreshToken!,
                clock.GetUtcNow().AddSeconds(tokens.ExpiresIn))).ConfigureAwait(false);
            return new TraktConnectionStatus(true, account.Username);
        }, cancellationToken);

    internal Task<TraktConnectionStatus> DisconnectAsync(CancellationToken cancellationToken) =>
        LockedAsync(async () =>
        {
            _pending = null;
            var stored = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return new TraktConnectionStatus(false);
            }

            File.Delete(Path.Combine(_directory, StoreName));
            using var request = new HttpRequestMessage(HttpMethod.Post, AuthOrigin + "/oauth/revoke")
            {
                Content = JsonContent.Create(new { client_id = stored.ClientId, token = stored.AccessToken })
            };
            AddClientHeaders(request, stored.ClientId);
            try
            {
                using var reply = await http.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
                if (reply.IsSuccessStatusCode)
                {
                    return new TraktConnectionStatus(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Local disconnect is complete even if Trakt is unavailable.
            }

            return new TraktConnectionStatus(false, Notice: "Disconnected in Jellyfin. Remove the app in Trakt to revoke access there.");
        }, cancellationToken);

    internal Task<HttpResponseMessage> SendListRequestAsync(HttpRequestMessage request, string owner,
        CancellationToken cancellationToken) =>
        LockedAsync(async () =>
        {
            var stored = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (stored is null || stored.ClientId != ClientId())
            {
                throw new TraktConnectionException("Connect Trakt in the Magic Collections settings.");
            }

            if (!owner.Equals(stored.Slug, StringComparison.OrdinalIgnoreCase))
            {
                throw new TraktConnectionException("The source list must belong to the connected Trakt account.");
            }

            if (stored.NeedsReconnect)
            {
                throw new TraktConnectionException(ReconnectNotice);
            }

            if (request.Method != HttpMethod.Get ||
                request.RequestUri is not { Scheme: "https", Host: "api.trakt.tv", IsDefaultPort: true, UserInfo.Length: 0 })
            {
                throw new TraktConnectionException("Invalid Trakt API destination.");
            }

            if (stored.ExpiresAt <= clock.GetUtcNow() + RefreshMargin)
            {
                Tokens tokens;
                try
                {
                    // Do not lose rotated credentials just because the scheduled task was cancelled.
                    tokens = await RequestTokensAsync(stored.ClientId, new
                    {
                        grant_type = "refresh_token",
                        client_id = stored.ClientId,
                        redirect_uri = CallbackUrl,
                        refresh_token = stored.RefreshToken
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TraktConnectionException ex) when (ex.InvalidGrant)
                {
                    File.Delete(Path.Combine(_directory, StoreName));
                    throw new TraktConnectionException("Trakt authorization expired. Connect Trakt again.");
                }

                stored = stored with
                {
                    AccessToken = tokens.AccessToken!,
                    RefreshToken = tokens.RefreshToken!,
                    ExpiresAt = clock.GetUtcNow().AddSeconds(tokens.ExpiresIn)
                };
                await SaveAsync(stored).ConfigureAwait(false);
            }

            AddApiHeaders(request, stored.ClientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stored.AccessToken);
            var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            response.Dispose();
            // Remember a known rejection without losing the ability to revoke on disconnect.
            await SaveAsync(stored with { NeedsReconnect = true }).ConfigureAwait(false);
            throw new TraktConnectionException(ReconnectNotice);
        }, cancellationToken);

    private async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ClientId() => ResolveClientId(configuration().TraktClientId);

    internal static string ResolveClientId(string? configuredId) =>
        string.IsNullOrWhiteSpace(configuredId) ? BundledClientId : configuredId.Trim();

    private static bool ValidClientId(string? id) => id is { Length: > 0 and <= 256 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string RandomValue() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private async Task<Tokens> RequestTokensAsync(string clientId, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AuthOrigin + "/oauth/token") { Content = JsonContent.Create(body) };
        AddClientHeaders(request, clientId);
        var tokens = await SendJsonAsync<Tokens>(request, "Trakt token exchange", cancellationToken,
            readOAuthError: true).ConfigureAwait(false);
        if (!ValidToken(tokens.AccessToken) || !ValidToken(tokens.RefreshToken) || tokens.ExpiresIn is <= 0 or > int.MaxValue)
        {
            throw new TraktConnectionException("Trakt did not return a valid token pair.");
        }

        return tokens;
    }

    private static bool ValidToken(string? token) => token is { Length: > 0 and <= 8192 } && token.All(c => c is >= '!' and <= '~');

    private async Task<T> SendJsonAsync<T>(HttpRequestMessage request, string operation, CancellationToken cancellationToken,
        bool readOAuthError = false)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (readOAuthError && response.StatusCode == HttpStatusCode.BadRequest)
                {
                    OAuthError? error = null;
                    try
                    {
                        error = await response.Content.ReadFromJsonAsync<OAuthError>(cancellationToken).ConfigureAwait(false);
                    }
                    catch (JsonException)
                    {
                        // Keep the HTTP status when Trakt returns a non-JSON error page.
                    }

                    if (error?.Error == "invalid_grant")
                    {
                        throw new TraktConnectionException("Trakt rejected the one-time code. Start a new connection.", invalidGrant: true);
                    }
                }

                throw new TraktConnectionException($"{operation} returned HTTP {(int)response.StatusCode}.");
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
                ?? throw new TraktConnectionException($"{operation} returned an empty response.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new TraktConnectionException($"{operation} failed. Could not read a response from Trakt.");
        }
    }

    internal static void AddApiHeaders(HttpRequestMessage request, string clientId)
    {
        request.Content ??= new ByteArrayContent([]);
        request.Content.Headers.ContentType = new("application/json");
        AddClientHeaders(request, clientId);
        request.Headers.Add("trakt-api-version", "2");
    }

    private static void AddClientHeaders(HttpRequestMessage request, string clientId)
    {
        // Trakt's OAuth endpoint can reject requests without a User-Agent before processing the grant.
        request.Headers.Add("trakt-api-key", clientId);
        request.Headers.UserAgent.ParseAdd(Plugin.HttpUserAgent);
    }

    private async Task<StoredConnection?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var file = File.OpenRead(Path.Combine(_directory, StoreName));
            var stored = await JsonSerializer.DeserializeAsync<StoredConnection>(file, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (stored is null || !ValidClientId(stored.ClientId) || string.IsNullOrWhiteSpace(stored.Account) ||
                string.IsNullOrWhiteSpace(stored.Slug) || !ValidToken(stored.AccessToken) ||
                !ValidToken(stored.RefreshToken) || stored.ExpiresAt == default)
            {
                throw new TraktConnectionException("The stored Trakt connection is unreadable. Connect Trakt again.");
            }

            return stored;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            throw new TraktConnectionException("The stored Trakt connection is unreadable. Connect Trakt again.");
        }
    }

    private async Task SaveAsync(StoredConnection stored)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
        }
        else
        {
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var temporary = Path.Combine(_directory, $"{StoreName}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            await using (var file = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(file, stored).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(_directory, StoreName), overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record PendingSignIn(TraktAdminSession Session, string ClientId, string Verifier, string State, DateTimeOffset ExpiresAt);
    private sealed record StoredConnection(string ClientId, string Account, string Slug,
        string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, bool NeedsReconnect = false);
    private sealed record Tokens([property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken, [property: JsonPropertyName("expires_in")] long ExpiresIn);
    private sealed record OAuthError(string? Error);
    private sealed record AccountResponse(TraktAccount? User);
    private sealed record TraktAccount(string? Username, AccountIds? Ids);
    private sealed record AccountIds(string? Slug);
}

internal sealed class TraktConnectionException(string message, bool invalidGrant = false) : Exception(message)
{
    public bool InvalidGrant { get; } = invalidGrant;
}
