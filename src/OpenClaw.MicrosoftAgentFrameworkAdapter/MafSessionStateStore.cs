using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Models;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

public sealed class MafSessionStateStore
{
    private const int CurrentSchemaVersion = 2;
    private const string LegacyDefaultSessionSidecarPath = "experiments/maf/sessions";

    private readonly string _rootPath;
    private readonly string? _legacyRootPath;
    private readonly ILogger<MafSessionStateStore> _logger;
    private readonly string _mafPackageVersion;

    public MafSessionStateStore(
        GatewayConfig config,
        IOptions<MafOptions> options,
        ILogger<MafSessionStateStore> logger)
    {
        var sidecarPath = NormalizeSidecarPath(options.Value.SessionSidecarPath);
        _rootPath = Path.GetFullPath(Path.Join(config.Memory.StoragePath, sidecarPath));
        _legacyRootPath = string.Equals(sidecarPath, MafOptions.DefaultSessionSidecarPath, StringComparison.Ordinal)
            ? Path.GetFullPath(Path.Join(config.Memory.StoragePath, NormalizeSidecarPath(LegacyDefaultSessionSidecarPath)))
            : null;
        _logger = logger;
        _mafPackageVersion = ResolveMafPackageVersion();
    }

    public async ValueTask<AgentSession> LoadAsync(ChatClientAgent agent, Session session, CancellationToken ct)
    {
        var path = GetSessionPath(session.Id);
        if (!File.Exists(path))
        {
            var legacyPath = GetLegacySessionPath(session.Id);
            if (legacyPath is null || !File.Exists(legacyPath))
                return await agent.CreateSessionAsync(ct);

            path = legacyPath;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var envelope = await JsonSerializer.DeserializeAsync(stream, MafJsonContext.Default.MafSessionEnvelope, ct)
                as MafSessionEnvelope;
            if (envelope is null)
            {
                _logger.LogInformation("Discarding MAF session sidecar for {SessionId}: envelope was missing.", session.Id);
                return await agent.CreateSessionAsync(ct);
            }

            if (envelope.SchemaVersion != CurrentSchemaVersion)
            {
                _logger.LogInformation(
                    "Discarding MAF session sidecar for {SessionId}: schema version {SchemaVersion} != {ExpectedSchemaVersion}.",
                    session.Id,
                    envelope.SchemaVersion,
                    CurrentSchemaVersion);
                return await agent.CreateSessionAsync(ct);
            }

            if (!string.Equals(envelope.SessionId, session.Id, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Discarding MAF session sidecar for {SessionId}: stored session id {StoredSessionId} did not match.",
                    session.Id,
                    envelope.SessionId);
                return await agent.CreateSessionAsync(ct);
            }

            var currentHistoryHash = ComputeHistoryHash(session);
            if (!string.Equals(envelope.HistoryHash, currentHistoryHash, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Discarding MAF session sidecar for {SessionId}: history hash mismatch.",
                    session.Id);
                return await agent.CreateSessionAsync(ct);
            }

            if (!string.Equals(envelope.MafPackageVersion, _mafPackageVersion, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Discarding MAF session sidecar for {SessionId}: package version {StoredVersion} != {CurrentVersion}.",
                    session.Id,
                    envelope.MafPackageVersion,
                    _mafPackageVersion);
                return await agent.CreateSessionAsync(ct);
            }

            _logger.LogInformation("Restored MAF session sidecar for {SessionId}.", session.Id);
            return await agent.DeserializeSessionAsync(envelope.State, jsonSerializerOptions: null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load MAF session sidecar for {SessionId}; starting a fresh agent session.", session.Id);
            return await agent.CreateSessionAsync(ct);
        }
    }

    public async Task SaveAsync(ChatClientAgent agent, Session session, AgentSession agentSession, CancellationToken ct)
    {
        var state = await agent.SerializeSessionAsync(agentSession, jsonSerializerOptions: null, ct);
        var envelope = new MafSessionEnvelope
        {
            SchemaVersion = CurrentSchemaVersion,
            SessionId = session.Id,
            MafPackageVersion = _mafPackageVersion,
            HistoryHash = ComputeHistoryHash(session),
            State = state.Clone()
        };

        var path = GetSessionPath(session.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, MafJsonContext.Default.MafSessionEnvelope, ct);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    internal string GetSessionPath(string sessionId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
        return Path.Join(_rootPath, hash + ".json");
    }

    private string? GetLegacySessionPath(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(_legacyRootPath))
            return null;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
        return Path.Join(_legacyRootPath, hash + ".json");
    }

    internal static string NormalizeSidecarPath(string? sidecarPath)
    {
        var normalized = string.IsNullOrWhiteSpace(sidecarPath)
            ? MafOptions.DefaultSessionSidecarPath
            : sidecarPath.Trim();
        var root = Path.GetPathRoot(normalized);
        if (!string.IsNullOrEmpty(root))
            normalized = normalized[root.Length..];

        normalized = normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(normalized)
            ? MafOptions.DefaultSessionSidecarPath
            : normalized;
    }

    private static string ResolveMafPackageVersion()
    {
        var assembly = typeof(ChatClientAgent).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    internal static string ComputeHistoryHash(Session session)
    {
        var historyJson = JsonSerializer.Serialize(session.History, CoreJsonContext.Default.ListChatTurn);
        var modelOverride = session.ModelOverride ?? string.Empty;
        var systemPromptOverride = session.SystemPromptOverride ?? string.Empty;
        var routePresetId = session.RoutePresetId ?? string.Empty;
        var routeAllowedTools = session.RouteAllowedTools.Length == 0
            ? string.Empty
            : string.Join(",", session.RouteAllowedTools.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase));
        var payload = $"{modelOverride}\n{systemPromptOverride}\n{routePresetId}\n{routeAllowedTools}\n{historyJson}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
