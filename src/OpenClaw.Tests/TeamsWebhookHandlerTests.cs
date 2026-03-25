using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class TeamsWebhookHandlerTests
{
    [Fact]
    public async Task BotFrameworkTokenValidator_AcceptsValidSignedToken()
    {
        using var rsa = RSA.Create(2048);
        var appId = "teams-app-id";
        var serviceUrl = "https://smba.trafficmanager.net/amer/";
        var validator = new BotFrameworkTokenValidator(
            appId,
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                if (request.RequestUri?.AbsoluteUri == "https://login.botframework.com/v1/.well-known/openidconfiguration")
                {
                    return JsonResponse("""{"issuer":"https://api.botframework.com","jwks_uri":"https://login.botframework.com/v1/.well-known/keys"}""");
                }

                if (request.RequestUri?.AbsoluteUri == "https://login.botframework.com/v1/.well-known/keys")
                {
                    var publicParameters = rsa.ExportParameters(false);
                    return JsonResponse(
                        $$"""
                        {
                          "keys": [
                            {
                              "kid": "kid-1",
                              "x5t": "kid-1",
                              "n": "{{Base64UrlEncode(publicParameters.Modulus!)}}",
                              "e": "{{Base64UrlEncode(publicParameters.Exponent!)}}",
                              "endorsements": ["msteams"]
                            }
                          ]
                        }
                        """);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })),
            NullLogger.Instance);

        var token = CreateJwt(
            rsa,
            "kid-1",
            new Dictionary<string, object?>
            {
                ["iss"] = "https://api.botframework.com",
                ["aud"] = appId,
                ["serviceUrl"] = serviceUrl,
                ["nbf"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
                ["exp"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()
            });

        var isValid = await validator.ValidateAsync($"Bearer {token}", serviceUrl, "msteams", CancellationToken.None);

        Assert.True(isValid);
    }

    [Fact]
    public async Task BotFrameworkTokenValidator_RejectsTokenWithInvalidSignature()
    {
        using var trustedRsa = RSA.Create(2048);
        using var forgedRsa = RSA.Create(2048);
        var appId = "teams-app-id";
        var serviceUrl = "https://smba.trafficmanager.net/amer/";
        var validator = new BotFrameworkTokenValidator(
            appId,
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                if (request.RequestUri?.AbsoluteUri == "https://login.botframework.com/v1/.well-known/openidconfiguration")
                {
                    return JsonResponse("""{"issuer":"https://api.botframework.com","jwks_uri":"https://login.botframework.com/v1/.well-known/keys"}""");
                }

                if (request.RequestUri?.AbsoluteUri == "https://login.botframework.com/v1/.well-known/keys")
                {
                    var publicParameters = trustedRsa.ExportParameters(false);
                    return JsonResponse(
                        $$"""
                        {
                          "keys": [
                            {
                              "kid": "kid-1",
                              "x5t": "kid-1",
                              "n": "{{Base64UrlEncode(publicParameters.Modulus!)}}",
                              "e": "{{Base64UrlEncode(publicParameters.Exponent!)}}",
                              "endorsements": ["msteams"]
                            }
                          ]
                        }
                        """);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })),
            NullLogger.Instance);

        var forgedToken = CreateJwt(
            forgedRsa,
            "kid-1",
            new Dictionary<string, object?>
            {
                ["iss"] = "https://api.botframework.com",
                ["aud"] = appId,
                ["serviceUrl"] = serviceUrl,
                ["nbf"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
                ["exp"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()
            });

        var isValid = await validator.ValidateAsync($"Bearer {forgedToken}", serviceUrl, "msteams", CancellationToken.None);

        Assert.False(isValid);
    }

    [Fact]
    public async Task HandleAsync_GroupAllowlistUsesTeamIdFromChannelData()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-teams-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var config = new TeamsChannelConfig
            {
                Enabled = true,
                AppId = "teams-app-id",
                AppIdRef = "",
                AppPassword = "teams-secret",
                AppPasswordRef = "",
                TenantId = "tenant-id",
                TenantIdRef = "",
                ValidateToken = true,
                RequireMention = false,
                GroupPolicy = "allowlist",
                AllowedTeamIds = ["team-123"]
            };

            var handler = new TeamsWebhookHandler(
                config,
                new AllowlistManager(storagePath, NullLogger<AllowlistManager>.Instance),
                new RecentSendersStore(storagePath, NullLogger<RecentSendersStore>.Instance),
                AllowlistSemantics.Legacy,
                NullLogger<TeamsWebhookHandler>.Instance,
                new StubTeamsTokenValidator(true));

            var channel = new TeamsChannel(
                config,
                new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
                NullLogger<TeamsChannel>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Headers.Authorization = "Bearer test-token";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                """
                {
                  "type": "message",
                  "id": "msg-1",
                  "text": "hello teams",
                  "from": {
                    "id": "29:user",
                    "name": "Taylor",
                    "aadObjectId": "aad-user-1"
                  },
                  "conversation": {
                    "id": "19:conversation-id",
                    "conversationType": "channel",
                    "tenantId": "tenant-id"
                  },
                  "channelId": "msteams",
                  "serviceUrl": "https://smba.trafficmanager.net/amer/",
                  "channelData": {
                    "team": {
                      "id": "team-123"
                    }
                  }
                }
                """));

            InboundMessage? captured = null;
            var result = await handler.HandleAsync(
                context,
                channel,
                (message, _) =>
                {
                    captured = message;
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);

            Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
            Assert.NotNull(captured);
            Assert.Equal("teams", captured!.ChannelId);
            Assert.Equal("aad-user-1", captured.SenderId);
            Assert.Equal("19:conversation-id", captured.GroupId);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string CreateJwt(RSA rsa, string keyId, IReadOnlyDictionary<string, object?> payload)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["kid"] = keyId,
            ["x5t"] = keyId
        });
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var encodedHeader = Base64UrlEncode(header);
        var encodedBody = Base64UrlEncode(body);
        var signedBytes = Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedBody}");
        var signature = rsa.SignData(signedBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{encodedHeader}.{encodedBody}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class StubTeamsTokenValidator(bool isValid) : ITeamsTokenValidator
    {
        public Task<bool> ValidateAsync(string authHeader, string? serviceUrl, string? channelId, CancellationToken ct)
        {
            _ = authHeader;
            _ = serviceUrl;
            _ = channelId;
            _ = ct;
            return Task.FromResult(isValid);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(handler(request));
        }
    }
}
