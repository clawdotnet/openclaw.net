using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Channels;

/// <summary>
/// A channel adapter for Signal messaging via signald or signal-cli bridge.
/// Communicates over a Unix domain socket (signald) or subprocess JSON-RPC.
/// </summary>
public sealed class SignalChannel : IChannelAdapter
{
    private readonly SignalChannelConfig _config;
    private readonly ILogger<SignalChannel> _logger;
    private readonly string _accountNumber;

    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public SignalChannel(SignalChannelConfig config, ILogger<SignalChannel> logger)
    {
        _config = config;
        _logger = logger;

        var phone = SecretResolver.Resolve(config.AccountPhoneNumberRef) ?? config.AccountPhoneNumber;
        _accountNumber = phone ?? throw new InvalidOperationException("Signal account phone number not configured or missing from environment.");
    }

    public string ChannelType => "signal";
    public string ChannelId => "signal";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (string.Equals(_config.Driver, "signald", StringComparison.OrdinalIgnoreCase))
            _receiveLoop = RunSignaldLoopAsync(_cts.Token);
        else if (string.Equals(_config.Driver, "signal_cli", StringComparison.OrdinalIgnoreCase))
            _receiveLoop = RunSignalCliLoopAsync(_cts.Token);
        else
            throw new InvalidOperationException($"Unknown Signal driver: '{_config.Driver}'. Expected 'signald' or 'signal_cli'.");

        return Task.CompletedTask;
    }

    public ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        var delivery = new ChannelDeliveryAttempt();
        return delivery.ExecuteAsync(() => SendCoreAsync(outbound, delivery, ct), ct);
    }

    private async Task SendCoreAsync(OutboundMessage outbound, ChannelDeliveryAttempt delivery, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text))
            throw new ChannelDeliveryException("Signal message is empty.", false);
        if (string.Equals(_config.Driver, "signald", StringComparison.OrdinalIgnoreCase))
            await SendViaSignaldAsync(outbound.RecipientId, outbound.Text, ct);
        else
            await SendViaSignalCliAsync(outbound.RecipientId, outbound.Text, ct);
        delivery.RecordAccepted();
        LogSend(outbound.RecipientId);
    }

    private void LogSend(string recipient)
    {
        if (_config.NoContentLogging)
            _logger.LogInformation("Sent Signal message to {Recipient}. [content redacted]", recipient);
        else
            _logger.LogInformation("Sent Signal message to {Recipient}.", recipient);
    }

    // ── signald driver ──────────────────────────────────────────

    private async Task RunSignaldLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        const int maxBackoff = 60;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint(_config.SocketPath);
                await socket.ConnectAsync(endpoint, ct);
                _logger.LogInformation("Connected to signald at {Path}.", _config.SocketPath);

                backoff = TimeSpan.FromSeconds(1);

                // Subscribe to account
                var subscribe = $$"""{"type":"subscribe","account":"{{_accountNumber}}"}""";
                await SendToSocketAsync(socket, subscribe, ct);

                if (_config.TrustAllKeys)
                {
                    var trust = $$"""{"type":"trust","account":"{{_accountNumber}}","trust_level":"TRUSTED_UNVERIFIED"}""";
                    await SendToSocketAsync(socket, trust, ct);
                }

                using var stream = new NetworkStream(socket, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;

                    await ProcessSignaldMessageAsync(line, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "signald connection lost. Reconnecting in {Backoff}s.", backoff.TotalSeconds);
            }

            await Task.Delay(backoff, ct);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoff));
        }
    }

    private async Task ProcessSignaldMessageAsync(string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "IncomingMessage", StringComparison.OrdinalIgnoreCase))
                return;

            if (!root.TryGetProperty("data", out var data))
                return;

            // Skip group messages (DM-only in v1)
            if (data.TryGetProperty("data_message", out var dataMsg))
            {
                if (dataMsg.TryGetProperty("group", out _) || dataMsg.TryGetProperty("groupV2", out _))
                {
                    _logger.LogDebug("Ignoring Signal group message (DM-only mode).");
                    return;
                }

                var body = dataMsg.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
                var source = data.TryGetProperty("source", out var src) ? src.GetString() : null;

                if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(source))
                    return;

                await DispatchInboundAsync(source, body, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process signald message.");
        }
    }

    private async Task SendViaSignaldAsync(string recipient, string text, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(_config.SocketPath);
        await socket.ConnectAsync(endpoint, ct);

        var payload = $$"""{"type":"send","username":"{{_accountNumber}}","recipientAddress":{"number":"{{recipient}}"},"messageBody":"{{EscapeJson(text)}}"}""";
        await SendToSocketAsync(socket, payload, ct);
        throw new ChannelDeliveryException("signald request written without a correlated delivery acknowledgment.", false, outcomeUnknown: true);
    }

    private static async Task SendToSocketAsync(Socket socket, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        var offset = 0;
        while (offset < bytes.Length)
        {
            var written = await socket.SendAsync(bytes.AsMemory(offset), SocketFlags.None, ct);
            if (written == 0) throw new IOException("Signal socket closed during write.");
            offset += written;
        }
    }

    // ── signal-cli driver ───────────────────────────────────────

    private async Task RunSignalCliLoopAsync(CancellationToken ct)
    {
        var cliPath = _config.SignalCliPath ?? "signal-cli";
        var backoff = TimeSpan.FromSeconds(1);
        const int maxBackoff = 60;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = $"-u {_accountNumber} daemon --json",
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process is null)
                {
                    _logger.LogError("Failed to start signal-cli process.");
                    break;
                }

                _logger.LogInformation("Started signal-cli daemon for {Account}.", _accountNumber);
                backoff = TimeSpan.FromSeconds(1);

                try
                {
                    using var reader = process.StandardOutput;
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (line is null) break;

                        await ProcessSignalCliMessageAsync(line, ct);
                    }
                }
                finally
                {
                    // Ensure the daemon process is terminated to avoid orphans
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch { /* best effort */ }
                    }
                }

                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "signal-cli process exited. Restarting in {Backoff}s.", backoff.TotalSeconds);
            }

            await Task.Delay(backoff, ct);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoff));
        }
    }

    private async Task ProcessSignalCliMessageAsync(string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("envelope", out var envelope))
                return;

            var source = envelope.TryGetProperty("sourceNumber", out var src) ? src.GetString() : null;

            if (!envelope.TryGetProperty("dataMessage", out var dataMsg))
                return;

            // Skip group messages
            if (dataMsg.TryGetProperty("groupInfo", out _))
            {
                _logger.LogDebug("Ignoring Signal group message (DM-only mode).");
                return;
            }

            var body = dataMsg.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(source))
                return;

            await DispatchInboundAsync(source, body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process signal-cli message.");
        }
    }

    private async Task SendViaSignalCliAsync(string recipient, string text, CancellationToken ct)
    {
        var cliPath = _config.SignalCliPath ?? "signal-cli";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = cliPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(_accountNumber);
        psi.ArgumentList.Add("send");
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(text);
        psi.ArgumentList.Add(recipient);

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
            throw new ChannelDeliveryException("signal-cli could not be started.", false);
        var output = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
            await output;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new ChannelDeliveryException("signal-cli exited without confirming the send.", false, outcomeUnknown: true);
    }

    // ── common ──────────────────────────────────────────────────

    private async Task DispatchInboundAsync(string senderNumber, string text, CancellationToken ct)
    {
        // Allowlist check
        if (_config.AllowedFromNumbers.Length > 0)
        {
            if (!Array.Exists(_config.AllowedFromNumbers, n => string.Equals(n, senderNumber, StringComparison.Ordinal)))
                return;
        }

        if (text.Length > _config.MaxInboundChars)
            text = text[.._config.MaxInboundChars];

        if (_config.NoContentLogging)
            _logger.LogInformation("Received Signal message from {Sender}. [content redacted]", senderNumber);
        else
            _logger.LogInformation("Received Signal message from {Sender}.", senderNumber);

        var message = new InboundMessage
        {
            ChannelId = "signal",
            SenderId = senderNumber,
            Text = text,
            IsGroup = false,
        };

        if (OnMessageReceived is not null)
            await OnMessageReceived(message, ct);
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
    }
}

[JsonSerializable(typeof(SignalSendRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class SignalJsonContext : JsonSerializerContext;

public sealed class SignalSendRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "send";

    [JsonPropertyName("username")]
    public required string Username { get; set; }

    [JsonPropertyName("messageBody")]
    public required string MessageBody { get; set; }
}
