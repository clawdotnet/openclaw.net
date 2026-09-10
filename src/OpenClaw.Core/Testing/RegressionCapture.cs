using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Core.Testing;

/// <summary>Captures completed exchanges, with bounded private content-addressed JSONL files.</summary>
public static class RegressionCapture
{
    public static async Task<int> CaptureAsync(Session session, string directory, IRedactionPipeline redaction,
        int maximumFiles = 100, CancellationToken ct = default)
    {
        if (maximumFiles is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        var history = session.History.ToArray();
        var written = 0;
        for (var start = 0; start < history.Length; start++)
        {
            if (history[start].Role != "user") continue;
            var end = start + 1;
            while (end < history.Length && history[end].Role != "user") end++;
            if (end <= start + 1 || history[end - 1].Role != "assistant" || history[end - 1].ToolCalls is { Count: > 0 }) continue;
            if (history[start..end].Any(t => t.ToolCalls?.Any(call => call.Result is null) == true)) continue;
            var builder = new StringBuilder();
            for (var i = start; i < end; i++)
            {
                var turn = history[i];
                Add(turn.Role == "user" ? "prompt" : "response", i - start, turn.Role, turn.Content);
                foreach (var call in turn.ToolCalls ?? [])
                {
                    Add("tool_call", i - start, null, null, call, false);
                    Add("tool_result", i - start, null, null, call, true);
                }
            }
            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            if (bytes.Length > 8 * 1024 * 1024) continue;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var file = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(bytes)) + ".jsonl");
            if (File.Exists(file)) continue;
            // Saturate instead of deleting a user's fixtures. Operators choose retention explicitly.
            if (Directory.EnumerateFiles(directory, "*.jsonl").Take(maximumFiles).Count() >= maximumFiles) return written;
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options)) { await stream.WriteAsync(bytes, ct); stream.Flush(true); }
                File.Move(temporary, file, overwrite: true); written++;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }

            void Add(string type, int turn, string? role, string? content, ToolInvocation? call = null, bool result = false)
            {
                var calls = history[start + turn].ToolCalls;
                var record = new TrajectoryExportRecord
                {
                    Type = type, SessionId = "capture", ChannelId = "capture", SenderId = "capture", TurnIndex = turn,
                    Role = role, Content = content is null ? null : TrajectorySanitizer.Redact(content, redaction),
                    CallId = call is null ? null : $"call_{turn}_{calls!.IndexOf(call)}", ToolName = call?.ToolName,
                    Arguments = call is null || result ? null : TrajectorySanitizer.RedactPayload(call.Arguments, redaction),
                    Result = result && call?.Result is not null ? TrajectorySanitizer.RedactPayload(call.Result, redaction) : null,
                    ResultStatus = result ? call?.ResultStatus : null,
                    FailureCode = result && call?.FailureCode is not null ? TrajectorySanitizer.Redact(call.FailureCode, redaction) : null,
                    FailureMessage = result && call?.FailureMessage is not null ? TrajectorySanitizer.Redact(call.FailureMessage, redaction) : null,
                    Anonymized = true
                };
                builder.AppendLine(JsonSerializer.Serialize(record, CoreJsonContext.Default.TrajectoryExportRecord));
                if (builder.Length > 8 * 1024 * 1024) throw new InvalidDataException("Capture exceeds size limit.");
            }
        }
        return written;
    }
}
