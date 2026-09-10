using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Actions;

/// <summary>Adapters must query provider state without performing the action in ReconcileAsync.</summary>
public interface IReconcilableTool : ITool
{
    ValueTask<ActionOutcome> ReconcileAsync(string idempotencyKey, CancellationToken ct);
    ValueTask<string> ExecuteWithIdempotencyAsync(string argumentsJson, string idempotencyKey, ToolExecutionContext context, CancellationToken ct);
}

public sealed record ActionOutcome(string State, string? Result = null);
public sealed class ActionRecord
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string CallId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string ArgumentsHash { get; set; } = "";
    public string State { get; set; } = "started";
    public string? Result { get; set; }
    public bool HistoryPersisted { get; set; }
    public string? Evidence { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

[JsonSerializable(typeof(List<ActionRecord>))]
internal partial class ActionJsonContext : JsonSerializerContext;

/// <summary>One exclusive lease per session, including across local processes. No raw arguments are stored.</summary>
public sealed class DurableActionJournal(string storagePath)
{
    private readonly string _root = Path.GetFullPath(Path.Combine(storagePath, "action-journal"));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async Task<Lease> OpenAsync(string sessionId, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        var stem = Path.Combine(_root, Hash(sessionId));
        FileStream handle;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { handle = new FileStream(stem + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33 or 35) { await Task.Delay(50, ct); }
        }
        try
        {
            var records = File.Exists(stem + ".json")
                ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(stem + ".json", ct), ActionJsonContext.Default.ListActionRecord)
                    ?? throw new InvalidDataException("Invalid action journal.")
                : [];
            if (records.Any(r => r is null || r.SessionId != sessionId || r.Id != Hash(sessionId + "\n" + r.CallId)
                || r.State is not ("started" or "completed" or "not_executed") || r.Revision < 1
                || (r.State == "completed" && r.Result is null)
                || (r.State != "started" && string.IsNullOrWhiteSpace(r.Evidence))
                || (r.HistoryPersisted && r.State != "completed"))
                || records.Select(r => r.Id).Distinct().Count() != records.Count)
                throw new InvalidDataException("Invalid action journal records.");
            return new Lease(stem + ".json", sessionId, handle, records);
        }
        catch { handle.Dispose(); throw; }
    }

    public async Task AcknowledgePersistedHistoryAsync(Session session, CancellationToken ct, ILogger? logger = null)
    {
        // A tool can persist session metadata while it holds the dispatch lease. Skip that
        // acknowledgement; the completed batch/final turn will acknowledge later.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
        try
        {
            using var lease = await OpenAsync(session.Id, timeout.Token);
            lease.AcknowledgeHistory(session);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Session history was saved, but journal acknowledgement failed. Action reconciliation remains required.");
        }
    }

    public sealed class Lease(string path, string sessionId, FileStream handle, List<ActionRecord> records) : IDisposable
    {
        public IReadOnlyList<ActionRecord> Records => records;
        public ActionRecord Begin(string callId, string toolName, string arguments)
        {
            var hash = Hash(arguments);
            var existing = records.SingleOrDefault(r => r.CallId == callId);
            if (existing is not null)
            {
                if (existing.ToolName != toolName || existing.ArgumentsHash != hash)
                    throw new InvalidOperationException("Action identity was reused with different arguments.");
                return existing;
            }
            var record = new ActionRecord { Id = Hash(sessionId + "\n" + callId), SessionId = sessionId,
                CallId = callId, ToolName = toolName, ArgumentsHash = hash };
            records.Add(record);
            Save(record);
            return record;
        }
        public void Resolve(ActionRecord record, long expectedRevision, string state, string evidence, string? result = null)
        {
            if (!records.Contains(record) || record.Revision != expectedRevision)
                throw new InvalidOperationException("Action changed; refresh before resolving.");
            if (state is not ("completed" or "not_executed") || string.IsNullOrWhiteSpace(evidence)
                || (state == "completed" && result is null))
                throw new ArgumentException("Resolution requires provider evidence and a completed result or confirmed non-execution.");
            record.State = state; record.Result = result; record.Evidence = evidence;
            Save(record);
        }
        public void AcknowledgeHistory(Session session)
        {
            var calls = session.History.SelectMany(t => t.ToolCalls ?? []).Select(c => c.CallId).ToHashSet();
            foreach (var record in records.Where(r => r.State == "completed" && !r.HistoryPersisted && calls.Contains(r.CallId)))
            { record.HistoryPersisted = true; Save(record); }
        }
        public void MarkStarted(ActionRecord record) { record.State = "started"; Save(record); }
        public void Complete(ActionRecord record, string result)
            => Resolve(record, record.Revision, "completed", "Tool returned a result.", result);
        private void Save(ActionRecord record)
        {
            record.Revision++; record.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                {
                    JsonSerializer.Serialize(stream, records, ActionJsonContext.Default.ListActionRecord);
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public void Dispose() => handle.Dispose();
    }
}
