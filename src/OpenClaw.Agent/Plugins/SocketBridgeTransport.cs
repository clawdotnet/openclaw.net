using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Observability;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Local IPC transport using Unix domain sockets on Unix and named pipes on Windows.
/// </summary>
public sealed class SocketBridgeTransport : BridgeTransportBase
{
    private readonly ILogger _logger;
    private readonly string _socketPath;
    private readonly string? _socketDirectory;
    private readonly bool _ownsSocketDirectory;
    private readonly string _authToken;
    private readonly RuntimeMetrics? _metrics;
    private readonly string? _pipeName;
    private Socket? _listener;
    private Socket? _acceptedSocket;
    private NamedPipeServerStream? _pipeServer;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public SocketBridgeTransport(
        string socketPath,
        string? socketDirectory,
        bool ownsSocketDirectory,
        string authToken,
        ILogger logger,
        RuntimeMetrics? metrics = null)
        : base(logger)
    {
        _logger = logger;
        _socketPath = socketPath;
        _socketDirectory = socketDirectory;
        _ownsSocketDirectory = ownsSocketDirectory;
        _authToken = authToken;
        _metrics = metrics;
        _pipeName = OperatingSystem.IsWindows() ? NormalizePipeName(socketPath) : null;
    }

    public override async Task PrepareAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
        {
            _pipeServer = new NamedPipeServerStream(
                _pipeName!,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            return;
        }

        var dir = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            TryRestrictUnixDirectory(dir);
        }

        if (File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); } catch { }
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(1);

        // Bind/listen is synchronous, but waiting briefly for the socket path to materialize
        // avoids connection-refused races on some Unix hosts under test load.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(_socketPath))
                break;

            await Task.Delay(10, ct);
        }
    }

    public override async Task StartAsync(Process process, CancellationToken ct)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));

        if (OperatingSystem.IsWindows())
        {
            if (_pipeServer is null)
                throw new InvalidOperationException("Named pipe server is not prepared.");

            await _pipeServer.WaitForConnectionAsync(connectCts.Token);
            (_reader, _writer) = await AuthenticateStreamAsync(_pipeServer, connectCts.Token);
            AttachReaderWriter(_reader, _writer);
            return;
        }

        if (_listener is null)
            throw new InvalidOperationException("Socket listener is not prepared.");

        while (true)
        {
            var acceptedSocket = await _listener.AcceptAsync(connectCts.Token);
            try
            {
                var stream = new NetworkStream(acceptedSocket, ownsSocket: false);
                var authenticated = await TryAuthenticateStreamAsync(stream, connectCts.Token);
                if (authenticated.Reader is null || authenticated.Writer is null)
                {
                    await stream.DisposeAsync();
                    acceptedSocket.Dispose();
                    continue;
                }

                _acceptedSocket = acceptedSocket;
                _reader = authenticated.Reader;
                _writer = authenticated.Writer;
                break;
            }
            catch
            {
                acceptedSocket.Dispose();
                throw;
            }
        }

        AttachReaderWriter(_reader, _writer);
    }

    protected override ValueTask DisposeCoreAsync()
    {
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _acceptedSocket?.Dispose(); } catch { }
        try { _listener?.Dispose(); } catch { }
        try
        {
            if (_pipeServer is not null)
            {
                if (_pipeServer.IsConnected)
                    _pipeServer.Disconnect();
                _pipeServer.Dispose();
            }
        }
        catch { }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if (File.Exists(_socketPath))
                    File.Delete(_socketPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to remove bridge socket path {SocketPath}", _socketPath);
            }

            if (_ownsSocketDirectory && !string.IsNullOrWhiteSpace(_socketDirectory))
            {
                try
                {
                    if (Directory.Exists(_socketDirectory))
                        Directory.Delete(_socketDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to remove bridge socket directory {SocketDirectory}", _socketDirectory);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private async Task<(StreamReader? Reader, StreamWriter? Writer)> TryAuthenticateStreamAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            return await AuthenticateStreamAsync(stream, ct);
        }
        catch (InvalidOperationException ex)
        {
            _metrics?.IncrementPluginBridgeAuthFailures();
            _logger.LogWarning(ex, "Rejected unauthenticated local IPC client for {SocketPath}", _socketPath);
            return (null, null);
        }
    }

    private async Task<(StreamReader Reader, StreamWriter Writer)> AuthenticateStreamAsync(Stream stream, CancellationToken ct)
    {
        var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        var line = await ReadLineWithCancellationAsync(reader, ct);
        if (!IsExpectedAuthLine(line))
        {
            reader.Dispose();
            writer.Dispose();
            throw new InvalidOperationException("Bridge client failed local IPC authentication.");
        }

        return (reader, writer);
    }

    private static async Task<string?> ReadLineWithCancellationAsync(StreamReader reader, CancellationToken ct)
    {
        var readTask = reader.ReadLineAsync();
        var completed = await Task.WhenAny(readTask, Task.Delay(Timeout.InfiniteTimeSpan, ct));
        if (!ReferenceEquals(completed, readTask))
            throw new OperationCanceledException(ct);

        return await readTask;
    }

    private bool IsExpectedAuthLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        try
        {
            using var auth = JsonDocument.Parse(line);
            if (!auth.RootElement.TryGetProperty("type", out var typeProperty) ||
                !string.Equals(typeProperty.GetString(), "bridge_auth", StringComparison.Ordinal))
            {
                return false;
            }

            if (!auth.RootElement.TryGetProperty("token", out var tokenProperty))
                return false;

            var token = tokenProperty.GetString();
            if (string.IsNullOrEmpty(token))
                return false;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token),
                Encoding.UTF8.GetBytes(_authToken));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void TryRestrictUnixDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static string NormalizePipeName(string socketPath)
    {
        const string prefix = @"\\.\pipe\";
        if (socketPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return socketPath[prefix.Length..];

        var sanitized = socketPath
            .Replace('\\', '-')
            .Replace('/', '-')
            .Replace(':', '-');

        return sanitized.Trim('-');
    }
}
