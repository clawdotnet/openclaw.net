using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.Core.Setup;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class LocalInferenceSupervisorTests : IDisposable
{
    private readonly string _tempDir;

    public LocalInferenceSupervisorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-local-inference-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task EmbeddedClient_StartsFakeSidecarStreamsRestartsAndCapturesLogs()
    {
        if (OperatingSystem.IsWindows() || !Python3Available())
            return;

        var package = GetPackage();
        var modelsRoot = Path.Combine(_tempDir, "models");
        var modelSource = Path.Combine(_tempDir, "fake.gguf");
        await File.WriteAllTextAsync(modelSource, "fake gguf for sidecar");
        var install = await LocalModelCache.InstallAsync(
            package,
            new LocalModelInstallRequest
            {
                SourcePath = modelSource,
                ModelsRoot = modelsRoot,
                AcceptLicense = true
            },
            CancellationToken.None);
        Assert.True(install.Success, install.Message);

        var runtimePath = WriteFakeSidecarScript();
        var logsPath = Path.Combine(_tempDir, "logs");
        using var client = new EmbeddedLocalChatClient(
            new LlmProviderConfig
            {
                Provider = "embedded",
                Model = package.ModelId
            },
            new LocalInferenceConfig
            {
                Enabled = true,
                AutoStart = true,
                RuntimePath = runtimePath,
                ModelsRoot = modelsRoot,
                LogsPath = logsPath,
                StartupTimeoutSeconds = 10,
                MaxRestartAttempts = 1
            });

        ChatResponse response;
        try
        {
            response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            var debugLogPath = Path.Combine(logsPath, $"{package.Id}.localinfer.log");
            var logs = File.Exists(debugLogPath) ? await File.ReadAllTextAsync(debugLogPath) : "<no log>";
            throw new InvalidOperationException($"Fake sidecar failed. Runtime={runtimePath}. Logs:{Environment.NewLine}{logs}", ex);
        }

        var assistant = Assert.Single(response.Messages);
        Assert.Contains(assistant.Contents.OfType<TextContent>(), content => content.Text == "READY");
        Assert.Equal(2, response.Usage?.InputTokenCount);
        Assert.Equal(1, response.Usage?.OutputTokenCount);

        var supervisor = Assert.IsType<LocalInferenceSupervisor>(client.GetService(typeof(LocalInferenceSupervisor)));
        var firstStatus = supervisor.GetStatus();
        Assert.True(firstStatus.Running);
        Assert.True(firstStatus.ProcessId.HasValue);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "stream")]))
            updates.Add(update);
        Assert.Contains(updates.SelectMany(static update => update.Contents).OfType<TextContent>(), content => content.Text == "STREAM");

        Process.GetProcessById(firstStatus.ProcessId!.Value).Kill(entireProcessTree: true);
        await Task.Delay(500);

        var restarted = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "restart")],
            cancellationToken: CancellationToken.None);

        Assert.Contains(
            Assert.Single(restarted.Messages).Contents.OfType<TextContent>(),
            content => content.Text == "READY");
        Assert.True(supervisor.GetStatus().Running);

        await supervisor.StopAsync(CancellationToken.None);
        Assert.False(supervisor.GetStatus().Running);

        var logPath = Path.Combine(logsPath, $"{package.Id}.localinfer.log");
        Assert.True(File.Exists(logPath));
        Assert.Contains("fake sidecar starting", await File.ReadAllTextAsync(logPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbeddedClient_PassesGemmaRuntimeFlagsToSidecar()
    {
        if (OperatingSystem.IsWindows() || !Python3Available())
            return;

        var package = GetPackage();
        var modelsRoot = Path.Combine(_tempDir, "models-flags");
        var modelSource = Path.Combine(_tempDir, "flags.gguf");
        await File.WriteAllTextAsync(modelSource, "fake gguf for sidecar flags");
        var install = await LocalModelCache.InstallAsync(
            package,
            new LocalModelInstallRequest
            {
                SourcePath = modelSource,
                ModelsRoot = modelsRoot,
                AcceptLicense = true
            },
            CancellationToken.None);
        Assert.True(install.Success, install.Message);

        var runtimePath = WriteFakeSidecarScript();
        var capturePath = Path.Combine(_tempDir, "args.txt");
        var mmprojPath = Path.Combine(_tempDir, "mmproj.gguf");
        var draftPath = Path.Combine(_tempDir, "draft.gguf");
        var mediaPath = Path.Combine(_tempDir, "media");
        Directory.CreateDirectory(mediaPath);
        await File.WriteAllTextAsync(mmprojPath, "fake projector");
        await File.WriteAllTextAsync(draftPath, "fake draft");

        EmbeddedLocalChatClient? client = null;
        Environment.SetEnvironmentVariable("OPENCLAW_ARG_CAPTURE_PATH", capturePath);
        try
        {
            client = new EmbeddedLocalChatClient(
                new LlmProviderConfig
                {
                    Provider = "embedded",
                    Model = package.ModelId
                },
                new LocalInferenceConfig
                {
                    Enabled = true,
                    AutoStart = true,
                    RuntimePath = runtimePath,
                    ModelsRoot = modelsRoot,
                    LogsPath = Path.Combine(_tempDir, "logs-flags"),
                    StartupTimeoutSeconds = 10,
                    ContextSize = 128000,
                    ChatTemplate = "gemma",
                    MultimodalProjectorPath = mmprojPath,
                    MediaPath = mediaPath,
                    DraftModelPath = draftPath,
                    DraftModelGpuLayers = "4",
                    ReasoningMode = "on",
                    ReasoningBudget = 256
                });

            _ = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                cancellationToken: CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_ARG_CAPTURE_PATH", null);
            client?.Dispose();
        }

        var args = await File.ReadAllLinesAsync(capturePath);
        Assert.Contains("-c", args);
        Assert.Contains("128000", args);
        Assert.Contains("--jinja", args);
        Assert.Contains("--chat-template", args);
        Assert.Contains("gemma", args);
        Assert.Contains("--mmproj", args);
        Assert.Contains(mmprojPath, args);
        Assert.Contains("--media-path", args);
        Assert.Contains(mediaPath, args);
        Assert.Contains("-rea", args);
        Assert.Contains("on", args);
        Assert.Contains("--reasoning-budget", args);
        Assert.Contains("256", args);
        Assert.Contains("-md", args);
        Assert.Contains(draftPath, args);
        Assert.Contains("--n-gpu-layers-draft", args);
        Assert.Contains("4", args);
    }

    [Fact]
    public async Task EmbeddedClient_StartsFakeLiteRtAdapterWithCorrectArgs()
    {
        if (OperatingSystem.IsWindows() || !Python3Available())
            return;

        var runtimePath = WriteFakeSidecarScript();
        var capturePath = Path.Combine(_tempDir, "litert-args.txt");
        var modelPath = Path.Combine(_tempDir, "gemma-4-E2B-it.litertlm");
        var graphPath = Path.Combine(_tempDir, "video-ingestion.pbtxt");
        await File.WriteAllTextAsync(modelPath, "fake litertlm");
        await File.WriteAllTextAsync(graphPath, "fake graph");

        var package = new LocalModelPackageDefinition
        {
            Id = "fake-litert",
            PresetId = "fake-litert",
            ModelId = "fake-litert",
            FileName = Path.GetFileName(modelPath),
            Format = "litertlm",
            Experimental = true,
            Capabilities = new ModelCapabilities
            {
                SupportsStreaming = true,
                SupportsSystemMessages = true,
                MaxContextTokens = 32768,
                MaxOutputTokens = 4096
            },
            Runtime = new LocalModelRuntimeDefaults
            {
                Backend = "litert",
                ContextSize = 32768,
                Threads = "2"
            }
        };
        var status = new LocalModelPackageStatus
        {
            PackageId = package.Id,
            PresetId = package.PresetId,
            ModelId = package.ModelId,
            DisplayName = "Fake LiteRT",
            Installed = true,
            Verified = true,
            ModelPath = modelPath
        };
        var supervisor = new FakePackageSupervisor(
            new LocalInferenceConfig
            {
                Enabled = true,
                AutoStart = true,
                LiteRtRuntimePath = runtimePath,
                LiteRtMediaPipeGraphPath = graphPath,
                LogsPath = Path.Combine(_tempDir, "litert-logs"),
                StartupTimeoutSeconds = 10,
                ContextSize = 32768,
                Threads = "2"
            },
            package,
            status);

        EmbeddedLocalChatClient? client = null;
        Environment.SetEnvironmentVariable("OPENCLAW_ARG_CAPTURE_PATH", capturePath);
        try
        {
            client = new EmbeddedLocalChatClient(
                new LlmProviderConfig
                {
                    Provider = "embedded",
                    Model = package.ModelId
                },
                new LocalInferenceConfig(),
                supervisor,
                new HttpClient());

            var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
            Assert.Contains(Assert.Single(response.Messages).Contents.OfType<TextContent>(), content => content.Text == "READY");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_ARG_CAPTURE_PATH", null);
            client?.Dispose();
        }

        var args = await File.ReadAllLinesAsync(capturePath);
        Assert.Contains("--model", args);
        Assert.Contains(modelPath, args);
        Assert.Contains("--context-size", args);
        Assert.Contains("32768", args);
        Assert.Contains("--threads", args);
        Assert.Contains("2", args);
        Assert.Contains("--experimental-mediapipe-graph", args);
        Assert.Contains(graphPath, args);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteFakeSidecarScript()
    {
        var path = Path.Combine(_tempDir, "fake-llama-server");
        File.WriteAllText(path, """
        #!/usr/bin/env sh
        if [ -n "$OPENCLAW_ARG_CAPTURE_PATH" ]; then
          printf '%s\n' "$@" > "$OPENCLAW_ARG_CAPTURE_PATH"
        fi
        port=""
        while [ "$#" -gt 0 ]; do
          case "$1" in
            --port) port="$2"; shift 2 ;;
            --host) shift 2 ;;
            -m) shift 2 ;;
            -md) shift 2 ;;
            -c) shift 2 ;;
            --threads) shift 2 ;;
            --n-gpu-layers) shift 2 ;;
            --n-gpu-layers-draft) shift 2 ;;
            *) shift ;;
          esac
        done
        echo "fake sidecar starting on ${port}"
        python3 -u - "$port" <<'PY'
        import json
        import sys
        from http.server import BaseHTTPRequestHandler, HTTPServer

        port = int(sys.argv[1])

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, format, *args):
                return

            def do_GET(self):
                if self.path in ['/health', '/v1/models']:
                    self.send_response(200)
                    self.end_headers()
                    self.wfile.write(b'{}')
                    return
                self.send_response(404)
                self.end_headers()

            def do_POST(self):
                length = int(self.headers.get('Content-Length', '0') or '0')
                body = self.rfile.read(length)
                payload = json.loads(body.decode('utf-8')) if body else {}
                if self.path != '/v1/chat/completions':
                    self.send_response(404)
                    self.end_headers()
                    return
                if payload.get('stream'):
                    self.send_response(200)
                    self.send_header('Content-Type', 'text/event-stream')
                    self.end_headers()
                    self.wfile.write(b'data: {"choices":[{"delta":{"content":"STREAM"}}]}\n\n')
                    self.wfile.write(b'data: [DONE]\n\n')
                    self.wfile.flush()
                    return
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(b'{"choices":[{"message":{"content":"READY"}}],"usage":{"prompt_tokens":2,"completion_tokens":1}}')

        HTTPServer(('127.0.0.1', port), Handler).serve_forever()
        PY
        """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
        return path;
    }

    private static bool Python3Available()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Any(path => File.Exists(Path.Combine(path, "python3")));
    }

    private static LocalModelPackageDefinition GetPackage()
    {
        Assert.True(LocalModelPackageCatalog.TryGet("gemma-local-small-q4", out var package));
        Assert.NotNull(package);
        return package!;
    }

    private sealed class FakePackageSupervisor : LocalInferenceSupervisor
    {
        private readonly LocalModelPackageDefinition _package;
        private readonly LocalModelPackageStatus _status;

        public FakePackageSupervisor(
            LocalInferenceConfig config,
            LocalModelPackageDefinition package,
            LocalModelPackageStatus status)
            : base(config)
        {
            _package = package;
            _status = status;
        }

        protected override bool TryResolvePackage(string modelId, out LocalModelPackageDefinition? package)
        {
            package = string.Equals(modelId, _package.ModelId, StringComparison.OrdinalIgnoreCase) ? _package : null;
            return package is not null;
        }

        protected override Task<LocalModelPackageStatus> VerifyPackageAsync(
            LocalModelPackageDefinition package,
            string? modelsRoot,
            CancellationToken ct)
            => Task.FromResult(_status);
    }
}
