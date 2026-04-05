using System.Text;
using System.Text.Json;
using OpenClaw.Agent.Execution;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

public sealed class ProcessTool : IToolWithContext
{
    private readonly ExecutionProcessService _processes;
    private readonly ToolingConfig _tooling;

    public ProcessTool(ExecutionProcessService processes, ToolingConfig tooling)
    {
        _processes = processes;
        _tooling = tooling;
    }

    public string Name => "process";
    public string Description => "Manage long-running background processes. Supports start, list, poll, log, wait, write, and kill actions.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "action":{"type":"string","enum":["start","list","poll","log","wait","write","kill"],"default":"start"},
        "command":{"type":"string","description":"Shell command to start when action=start."},
        "process_id":{"type":"string","description":"Background process id for poll/log/wait/write/kill."},
        "timeout_seconds":{"type":"integer","minimum":1,"maximum":3600},
        "pty":{"type":"boolean","default":false},
        "input":{"type":"string","description":"Input text to write when action=write."},
        "stdout_offset":{"type":"integer","minimum":0},
        "stderr_offset":{"type":"integer","minimum":0},
        "max_chars":{"type":"integer","minimum":1,"maximum":65536},
        "working_directory":{"type":"string"},
        "backend":{"type":"string"},
        "session_id":{"type":"string","description":"Optional owner session filter for list."}
      },
      "required":["action"]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: process requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        if (_tooling.ReadOnlyMode)
            return "Error: process is disabled because Tooling.ReadOnlyMode is enabled.";
        if (!_tooling.AllowShell)
            return "Error: process is disabled because shell execution is disabled by configuration.";

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var action = GetString(root, "action") ?? "start";

        return action switch
        {
            "start" => await StartAsync(root, context, ct),
            "list" => List(root, context),
            "poll" => Poll(root, context),
            "log" => Log(root, context),
            "wait" => await WaitAsync(root, context, ct),
            "write" => await WriteAsync(root, context, ct),
            "kill" => await KillAsync(root, context, ct),
            _ => "Error: Unknown action. Valid actions are start, list, poll, log, wait, write, and kill."
        };
    }

    private async Task<string> StartAsync(JsonElement root, ToolExecutionContext context, CancellationToken ct)
    {
        var command = GetString(root, "command");
        if (string.IsNullOrWhiteSpace(command))
            return "Error: command is required for process start.";

        var handle = await _processes.StartAsync(new ExecutionProcessStartRequest
        {
            ToolName = Name,
            BackendName = GetString(root, "backend") ?? "",
            OwnerSessionId = context.Session.Id,
            OwnerChannelId = context.Session.ChannelId,
            OwnerSenderId = context.Session.SenderId,
            Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows()
                ? ["/c", command]
                : ["-lc", command],
            WorkingDirectory = GetString(root, "working_directory") ?? _tooling.WorkspaceRoot,
            TimeoutSeconds = GetInt(root, "timeout_seconds") ?? 180,
            Pty = GetBool(root, "pty"),
            RequireWorkspace = true
        }, ct);

        return $"Started process {handle.ProcessId}\nbackend: {handle.BackendName}\ncommand: {handle.CommandPreview}";
    }

    private string List(JsonElement root, ToolExecutionContext context)
    {
        var ownerSessionId = GetString(root, "session_id") ?? context.Session.Id;
        var items = _processes.List(ownerSessionId);
        if (items.Count == 0)
            return $"No background processes found for session {ownerSessionId}.";

        var sb = new StringBuilder();
        foreach (var item in items)
            sb.AppendLine($"{item.ProcessId} [{item.State}] {item.CommandPreview}");
        return sb.ToString().TrimEnd();
    }

    private string Poll(JsonElement root, ToolExecutionContext context)
    {
        var processId = GetRequiredProcessId(root);
        if (processId is null)
            return "Error: process_id is required.";

        var status = _processes.GetStatus(processId, context.Session.Id);
        if (status is null)
            return $"Error: process '{processId}' was not found.";

        return $"{status.ProcessId} [{status.State}] exit={status.ExitCode?.ToString() ?? "-"} stdout={status.StdoutBytes} stderr={status.StderrBytes}";
    }

    private string Log(JsonElement root, ToolExecutionContext context)
    {
        var processId = GetRequiredProcessId(root);
        if (processId is null)
            return "Error: process_id is required.";

        var log = _processes.ReadLog(new ExecutionProcessLogRequest
        {
            ProcessId = processId,
            OwnerSessionId = context.Session.Id,
            StdoutOffset = GetInt(root, "stdout_offset") ?? 0,
            StderrOffset = GetInt(root, "stderr_offset") ?? 0,
            MaxChars = GetInt(root, "max_chars") ?? 8_192
        });

        if (log is null)
            return $"Error: process '{processId}' was not found.";

        return $"[stdout]\n{log.Stdout}\n[stderr]\n{log.Stderr}\n[next_stdout_offset={log.NextStdoutOffset} next_stderr_offset={log.NextStderrOffset}]";
    }

    private async Task<string> WaitAsync(JsonElement root, ToolExecutionContext context, CancellationToken ct)
    {
        var processId = GetRequiredProcessId(root);
        if (processId is null)
            return "Error: process_id is required.";

        var status = await _processes.WaitAsync(processId, context.Session.Id, ct);
        if (status is null)
            return $"Error: process '{processId}' was not found.";

        return $"{status.ProcessId} completed with state={status.State} exit={status.ExitCode?.ToString() ?? "-"}";
    }

    private async Task<string> WriteAsync(JsonElement root, ToolExecutionContext context, CancellationToken ct)
    {
        var processId = GetRequiredProcessId(root);
        var input = GetString(root, "input");
        if (processId is null)
            return "Error: process_id is required.";
        if (string.IsNullOrWhiteSpace(input))
            return "Error: input is required for write.";

        return await _processes.WriteAsync(new ExecutionProcessInputRequest
        {
            ProcessId = processId,
            OwnerSessionId = context.Session.Id,
            Data = input
        }, ct)
            ? $"Input written to process {processId}."
            : $"Error: process '{processId}' was not found.";
    }

    private async Task<string> KillAsync(JsonElement root, ToolExecutionContext context, CancellationToken ct)
    {
        var processId = GetRequiredProcessId(root);
        if (processId is null)
            return "Error: process_id is required.";

        return await _processes.KillAsync(processId, context.Session.Id, ct)
            ? $"Process {processId} terminated."
            : $"Error: process '{processId}' was not found.";
    }

    private static string? GetRequiredProcessId(JsonElement root)
        => GetString(root, "process_id") ?? GetString(root, "session_id");

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? GetInt(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;

    private static bool GetBool(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.True;
}
