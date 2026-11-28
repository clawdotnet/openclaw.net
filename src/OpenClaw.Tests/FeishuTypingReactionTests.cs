using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FeishuTypingReactionTests
{
    [Fact]
    public async Task TypingReaction_WhenStartAndStop_ShouldPostThenDelete()
    {
        var calls = new List<(string Method, string Path)>();
        using var http = new HttpClient(new RecordingHandler(req =>
        {
            calls.Add((req.Method.Method, req.RequestUri!.AbsolutePath));
            if (req.Method == HttpMethod.Post &&
                req.RequestUri!.AbsolutePath.Contains("/reactions", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"code":0,"data":{"reaction_id":"react_typing_1"}}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0}""")
            };
        }));

        var channel = CreateChannelWithToken(http, typingIndicator: true);

        await channel.SetTypingReactionAsync("om_msg_1", true, CancellationToken.None);
        await channel.SetTypingReactionAsync("om_msg_1", false, CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.Equal("POST", calls[0].Method);
        Assert.Contains("/im/v1/messages/om_msg_1/reactions", calls[0].Path, StringComparison.Ordinal);
        Assert.Equal("DELETE", calls[1].Method);
        Assert.Contains(
            "/im/v1/messages/om_msg_1/reactions/react_typing_1",
            calls[1].Path,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypingReaction_WhenDispose_ShouldClearOutstanding()
    {
        var calls = new List<(string Method, string Path)>();
        using var http = new HttpClient(new RecordingHandler(req =>
        {
            calls.Add((req.Method.Method, req.RequestUri!.AbsolutePath));
            if (req.Method == HttpMethod.Post &&
                req.RequestUri!.AbsolutePath.Contains("/reactions", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"code":0,"data":{"reaction_id":"react_dispose_1"}}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0}""")
            };
        }));

        var channel = CreateChannelWithToken(http, typingIndicator: true);

        await channel.SetTypingReactionAsync("om_dispose", true, CancellationToken.None);
        await channel.DisposeAsync();

        Assert.Contains(
            calls,
            c => c.Method == "DELETE" &&
                 c.Path.Contains("/im/v1/messages/om_dispose/reactions/react_dispose_1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendTyping_WhenTypingIndicatorDisabled_ShouldNoOp()
    {
        var calls = new List<(string Method, string Path)>();
        using var http = new HttpClient(new RecordingHandler(req =>
        {
            calls.Add((req.Method.Method, req.RequestUri!.AbsolutePath));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0}""")
            };
        }));

        var channel = CreateChannelWithToken(http, typingIndicator: false);

        await channel.SendTypingAsync(
            "ou_user",
            isTyping: true,
            accountId: null,
            messageId: "om_disabled",
            CancellationToken.None);

        Assert.Empty(calls);
    }

    [Fact]
    public async Task SendTyping_WhenMessageIdMissing_ShouldNoOp()
    {
        var calls = new List<(string Method, string Path)>();
        using var http = new HttpClient(new RecordingHandler(req =>
        {
            calls.Add((req.Method.Method, req.RequestUri!.AbsolutePath));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0}""")
            };
        }));

        var channel = CreateChannelWithToken(http, typingIndicator: true);

        await channel.SendTypingAsync(
            "ou_user",
            isTyping: true,
            accountId: null,
            messageId: null,
            CancellationToken.None);

        Assert.Empty(calls);
    }

    private static FeishuChannel CreateChannelWithToken(HttpClient http, bool typingIndicator)
    {
        var channel = new FeishuChannel(
            new FeishuChannelConfig { TypingIndicator = typingIndicator },
            NullLogger<FeishuChannel>.Instance,
            http);

        typeof(FeishuChannel)
            .GetField("_appAccessToken", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(channel, "fixture-token");
        typeof(FeishuChannel)
            .GetField("_tokenExpiry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(channel, DateTimeOffset.UtcNow.AddHours(1));

        return channel;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
