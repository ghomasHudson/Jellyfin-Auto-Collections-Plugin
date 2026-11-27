using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MagicCollections.Configuration;
using Jellyfin.Plugin.MagicCollections.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace MagicCollections.Tests;

public sealed class TraktConnectionControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"magic-trakt-{Guid.NewGuid():N}");
    private readonly PluginConfiguration _config = new() { TraktClientId = "development-id" };
    private readonly Guid _admin = Guid.NewGuid();
    private readonly TestClock _clock = new();
    private readonly TraktReplies _replies = new();
    private readonly HttpClient _http;

    public TraktConnectionControllerTests() => _http = new(_replies);

    [Fact]
    public async Task Only_the_initiating_admin_session_can_exchange_the_Pages_response_once()
    {
        var controller = Dashboard("admin-session");
        var start = Assert.IsType<TraktSignIn>(Assert.IsType<OkObjectResult>(
            await controller.Start(default)).Value);
        var authorize = new Uri(start.AuthorizationUrl);
        Assert.Equal("https://auth.trakt.tv/oauth/authorize", authorize.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("development-id", query["client_id"]);
        Assert.Equal("https://ghomashudson.github.io/Jellyfin-Magic-Collections-Plugin/trakt/callback.html", query["redirect_uri"]);

        var response = new TraktCallbackResponse("sample-code", query["state"].ToString());
        controller.ControllerContext.HttpContext.User = Principal("different-session");
        Assert.IsType<BadRequestObjectResult>(await controller.Complete(response, default));
        Assert.Empty(_replies.Requests);

        controller.ControllerContext.HttpContext.User = Principal("admin-session");
        Assert.IsType<BadRequestObjectResult>(await controller.Complete(response with { State = "wrong-state" }, default));
        Assert.Empty(_replies.Requests);

        var status = Assert.IsType<TraktConnectionStatus>(Assert.IsType<OkObjectResult>(
            await controller.Complete(response, default)).Value);
        Assert.Equal("List Owner", status.Account);
        Assert.True(status.Connected);
        Assert.DoesNotContain("first-access", JsonSerializer.Serialize(status));

        var exchange = _replies.Requests[0];
        Assert.Equal("https://auth.trakt.tv/oauth/token", exchange.Url);
        var body = exchange.Body;
        Assert.Equal("authorization_code", body.GetProperty("grant_type").GetString());
        Assert.Equal("development-id", body.GetProperty("client_id").GetString());
        Assert.Equal(query["redirect_uri"], body.GetProperty("redirect_uri").GetString());
        Assert.Equal("sample-code", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("client_secret", out _));
        var verifier = body.GetProperty("code_verifier").GetString()!;
        Assert.InRange(verifier.Length, 43, 128);
        Assert.Equal(query["code_challenge"], WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        Assert.Equal("Bearer first-access", _replies.Requests[1].Authorization);

        Assert.IsType<BadRequestObjectResult>(await controller.Complete(response, default));
        Assert.Equal(2, _replies.Requests.Count);
        var afterRestart = Assert.IsType<TraktConnectionStatus>(Assert.IsType<OkObjectResult>(
            await Dashboard("admin-session").Status(default)).Value);
        Assert.Equal(status, afterRestart);
    }

    [Fact]
    public async Task Expired_responses_and_Jellyfin_API_keys_cannot_link_an_account()
    {
        var controller = Dashboard("admin-session");
        var start = Assert.IsType<TraktSignIn>(Assert.IsType<OkObjectResult>(await controller.Start(default)).Value);
        var state = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["state"].ToString();
        _clock.Now += TimeSpan.FromMinutes(11);
        Assert.IsType<BadRequestObjectResult>(await controller.Complete(new("sample-code", state), default));
        Assert.Empty(_replies.Requests);

        controller.HttpContext.User = Principal("server-api-key", apiKey: true);
        Assert.IsType<ForbidResult>(await controller.Start(default));
    }

    [Fact]
    public async Task Disconnect_removes_persisted_access_even_if_Trakt_does_not_revoke_the_token()
    {
        var controller = Dashboard("admin-session");
        var start = Assert.IsType<TraktSignIn>(Assert.IsType<OkObjectResult>(await controller.Start(default)).Value);
        var state = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["state"].ToString();
        Assert.IsType<OkObjectResult>(await controller.Complete(new("sample-code", state), default));
        var disconnected = Assert.IsType<TraktConnectionStatus>(Assert.IsType<OkObjectResult>(
            await controller.Disconnect(default)).Value);
        Assert.False(disconnected.Connected);
        Assert.Contains("Remove the app in Trakt", disconnected.Notice);
        var revoke = _replies.Requests.Last();
        Assert.Equal("https://auth.trakt.tv/oauth/revoke", revoke.Url);
        Assert.Equal("first-access", revoke.Body.GetProperty("token").GetString());
        Assert.False(revoke.Body.TryGetProperty("client_secret", out _));
        var status = Assert.IsType<TraktConnectionStatus>(Assert.IsType<OkObjectResult>(
            await Dashboard("admin-session").Status(default)).Value);
        Assert.False(status.Connected);
    }

    [Fact]
    public async Task Incomplete_stored_credentials_do_not_report_a_connected_account()
    {
        _config.TraktClientId = "";
        Directory.CreateDirectory(Path.Combine(_directory, "magic-collections"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "magic-collections", "trakt-connection.json"), "{}");
        Assert.IsType<BadRequestObjectResult>(await Dashboard("admin-session").Status(default));
        Assert.Empty(_replies.Requests);
    }

    [Fact]
    public async Task Dashboard_requires_reconnect_after_Trakt_rejects_the_saved_authorization()
    {
        var connection = NewConnection();
        var controller = Dashboard("admin-session", connection);
        var start = Assert.IsType<TraktSignIn>(Assert.IsType<OkObjectResult>(await controller.Start(default)).Value);
        var state = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["state"].ToString();
        Assert.IsType<OkObjectResult>(await controller.Complete(new("sample-code", state), default));

        var sync = new CollectionSync([new TraktHandler(_http, () => _config, connection)], new ControlledLibrary());
        var warnings = new List<string>();
        await sync.RunAsync([], Assert.Fail, default,
            privateTraktUrls: ["https://trakt.tv/users/list-owner/lists/example"], reportPrivateFailure: warnings.Add);
        Assert.Contains("Reconnect Trakt", Assert.Single(warnings));
        Assert.Equal("Bearer first-access", _replies.Requests.Last().Authorization);

        var status = Assert.IsType<TraktConnectionStatus>(Assert.IsType<OkObjectResult>(
            await Dashboard("admin-session").Status(default)).Value);
        Assert.False(status.Connected);
        Assert.Contains("Reconnect Trakt", status.Notice);
    }

    private TraktConnection NewConnection() => new(_http, () => _config, _directory, _clock);

    private TraktConnectionController Dashboard(string session, TraktConnection? connection = null) =>
        new(connection ?? NewConnection())
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { User = Principal(session) } }
        };

    private ClaimsPrincipal Principal(string session, bool apiKey = false) => new(new ClaimsIdentity(
    [
        new("Jellyfin-UserId", _admin.ToString("N")),
        new("Jellyfin-Token", session),
        new("Jellyfin-IsApiKey", apiKey.ToString())
    ], "Test"));

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TraktReplies : HttpMessageHandler
    {
        public List<(string Url, JsonElement Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Method == HttpMethod.Post
                ? JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token))
                : default;
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add((url, body, request.Headers.Authorization?.ToString()));
            if (url == "https://auth.trakt.tv/oauth/revoke")
            {
                return new(HttpStatusCode.ServiceUnavailable);
            }
            if (url == "https://api.trakt.tv/users/list-owner/lists/example/")
            {
                return new(HttpStatusCode.Unauthorized);
            }

            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(url switch
                {
                    "https://auth.trakt.tv/oauth/token" =>
                        """{"access_token":"first-access","refresh_token":"first-refresh","expires_in":604800}""",
                    "https://api.trakt.tv/users/settings" =>
                        """{"user":{"username":"List Owner","ids":{"slug":"list-owner","uuid":"test-owner"}}}""",
                    _ => throw new InvalidOperationException($"Unexpected request to {url}")
                })
            };
        }
    }
}
