using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.PromptCaching;

internal sealed class PromptCacheTraceWriter
{
    private readonly GatewayConfig _config;
    private readonly IRedactionPipeline _redaction;
    private readonly Lock _gate = new();

    public PromptCacheTraceWriter(GatewayConfig config, IRedactionPipeline? redaction = null)
    {
        _config = config;
        _redaction = redaction ?? new NoopRedactionPipeline();
    }

    public void WriteRequest(PromptCacheDescriptor descriptor, IReadOnlyList<ChatMessage> messages, ChatOptions options)
    {
        if (!IsEnabled(descriptor))
            return;

        Write(new PromptCacheTraceEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Event = "request",
            SessionId = descriptor.SessionId,
            ProfileId = descriptor.ProfileId,
            ProviderId = descriptor.ProviderId,
            ModelId = descriptor.ModelId,
            Dialect = descriptor.Dialect,
            Retention = descriptor.Retention,
            Fingerprint = descriptor.StableFingerprint,
            StableSystemPrompt = ShouldIncludeSystem() ? _redaction.Redact(descriptor.StableSystemPrompt) : null,
            PromptText = ShouldIncludePrompt() ? _redaction.Redact(descriptor.VolatileSuffix) : null,
            MessageCount = messages.Count,
            AdditionalProperties = options.AdditionalProperties?.ToDictionary(kvp => kvp.Key, kvp => (string?)_redaction.Redact(RenderPropertyValue(kvp.Value)))
        });
    }

    public void WriteResponse(PromptCacheDescriptor descriptor, long cacheReadTokens, long cacheWriteTokens)
    {
        if (!IsEnabled(descriptor))
            return;

        Write(new PromptCacheTraceEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Event = "response",
            SessionId = descriptor.SessionId,
            ProfileId = descriptor.ProfileId,
            ProviderId = descriptor.ProviderId,
            ModelId = descriptor.ModelId,
            Dialect = descriptor.Dialect,
            Retention = descriptor.Retention,
            Fingerprint = descriptor.StableFingerprint,
            CacheReadTokens = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens
        });
    }

    private bool IsEnabled(PromptCacheDescriptor descriptor)
    {
        if (GetBoolEnv("OPENCLAW_CACHE_TRACE"))
            return true;

        return descriptor.Enabled && (_config.Diagnostics.CacheTrace.Enabled || _config.Llm.PromptCaching.TraceEnabled == true);
    }

    private bool ShouldIncludePrompt() => GetBoolEnv("OPENCLAW_CACHE_TRACE_PROMPT", _config.Diagnostics.CacheTrace.IncludePrompt);
    private bool ShouldIncludeSystem() => GetBoolEnv("OPENCLAW_CACHE_TRACE_SYSTEM", _config.Diagnostics.CacheTrace.IncludeSystem);

    private string ResolvePath(PromptCacheDescriptor descriptor)
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_CACHE_TRACE_FILE");
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);
        if (!string.IsNullOrWhiteSpace(descriptor.StableFingerprint) && !string.IsNullOrWhiteSpace(_config.Llm.PromptCaching.TraceFilePath))
            return Path.GetFullPath(_config.Llm.PromptCaching.TraceFilePath);
        if (!string.IsNullOrWhiteSpace(_config.Diagnostics.CacheTrace.FilePath))
            return Path.GetFullPath(_config.Diagnostics.CacheTrace.FilePath);
        return Path.GetFullPath(Path.Combine(_config.Memory.StoragePath, "logs", "cache-trace.jsonl"));
    }

    private void Write(PromptCacheTraceEntry entry)
    {
        var path = ResolvePath(new PromptCacheDescriptor
        {
            SessionId = entry.SessionId ?? "unknown",
            ProfileId = entry.ProfileId ?? "unknown",
            ProviderId = entry.ProviderId ?? "unknown",
            ModelId = entry.ModelId ?? "unknown",
            Dialect = entry.Dialect ?? "none",
            Retention = entry.Retention ?? "auto",
            StableFingerprint = entry.Fingerprint ?? string.Empty,
            StableSystemPrompt = string.Empty,
            VolatileSuffix = string.Empty,
            ToolSignature = string.Empty,
            CreatedAtUtc = entry.TimestampUtc,
            Enabled = true,
            KeepWarmEligible = false
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(entry, PromptCacheTraceJsonContext.Default.PromptCacheTraceEntry);
        lock (_gate)
        {
            File.AppendAllText(path, json + Environment.NewLine);
        }
    }

    private static bool GetBoolEnv(string name, bool fallback = false)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        return raw.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => bool.TryParse(raw, out var parsed) ? parsed : fallback
        };
    }

    private static string? RenderPropertyValue(object? value)
        => value switch
        {
            null => null,
            string stringValue => stringValue,
            bool boolValue => boolValue ? "true" : "false",
            byte byteValue => byteValue.ToString(),
            sbyte sbyteValue => sbyteValue.ToString(),
            short shortValue => shortValue.ToString(),
            ushort ushortValue => ushortValue.ToString(),
            int intValue => intValue.ToString(),
            uint uintValue => uintValue.ToString(),
            long longValue => longValue.ToString(),
            ulong ulongValue => ulongValue.ToString(),
            float floatValue => floatValue.ToString(),
            double doubleValue => doubleValue.ToString(),
            decimal decimalValue => decimalValue.ToString(),
            Guid guidValue => guidValue.ToString(),
            DateTime dateTimeValue => dateTimeValue.ToString("O"),
            DateTimeOffset dateTimeOffsetValue => dateTimeOffsetValue.ToString("O"),
            TimeSpan timeSpanValue => timeSpanValue.ToString(),
            Uri uriValue => uriValue.ToString(),
            _ => "[OMITTED]"
        };

    internal sealed class PromptCacheTraceEntry
    {
        public DateTimeOffset TimestampUtc { get; init; }
        public string? Event { get; init; }
        public string? SessionId { get; init; }
        public string? ProfileId { get; init; }
        public string? ProviderId { get; init; }
        public string? ModelId { get; init; }
        public string? Dialect { get; init; }
        public string? Retention { get; init; }
        public string? Fingerprint { get; init; }
        public string? StableSystemPrompt { get; init; }
        public string? PromptText { get; init; }
        public int MessageCount { get; init; }
        public long CacheReadTokens { get; init; }
        public long CacheWriteTokens { get; init; }
        public Dictionary<string, string?>? AdditionalProperties { get; init; }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PromptCacheTraceWriter.PromptCacheTraceEntry))]
internal sealed partial class PromptCacheTraceJsonContext : JsonSerializerContext;
