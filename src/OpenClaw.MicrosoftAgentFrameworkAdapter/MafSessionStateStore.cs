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

    private readonly string _rootPath;
    private readonly ILogger<MafSessionStateStore> _logger;
    private readonly string _mafPackageVersion;

    public MafSessionStateStore(
        GatewayConfig config,
        IOptions<MafOptions> options,
        ILogger<MafSessionStateStore> logger)
    {
        _rootPath = Path.Combine(config.Memory.StoragePath, options.Value.SessionSidecarPath);
        _logger = logger;
        _mafPackageVersion = ResolveMafPackageVersion();
    }

    public async ValueTask<AgentSession> LoadAsync(ChatClientAgent agent, Session session, CancellationToken ct)
    {
        var path = GetSessionPath(session.Id);
        if (!File.Exists(path))
            return await agent.CreateSessionAsync(ct);

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
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, envelope, MafJsonContext.Default.MafSessionEnvelope, ct);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    internal string GetSessionPath(string sessionId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
        return Path.Combine(_rootPath, hash + ".json");
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
        var payload = $"{modelOverride}\n{historyJson}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
