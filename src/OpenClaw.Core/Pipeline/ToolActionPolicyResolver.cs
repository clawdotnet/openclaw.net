using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Pipeline;

public static class ToolActionPolicyResolver
{
    private static readonly HashSet<string> AlwaysMutatingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file",
        "edit_file",
        "apply_patch",
        "shell",
        "code_exec",
        "git",
        "database",
        "home_assistant_write",
        "mqtt_publish",
        "notion_write",
        "inbox_zero",
        "email",
        "calendar",
        "delegate_agent"
    };

    public static ToolActionDescriptor Resolve(string toolName, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return new ToolActionDescriptor();

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;

            if (toolName.Equals("process", StringComparison.OrdinalIgnoreCase))
            {
                var action = GetString(root, "action") ?? "start";
                var command = GetString(root, "command");
                var processId = GetString(root, "process_id") ?? GetString(root, "session_id");
                return new ToolActionDescriptor
                {
                    Action = action,
                    IsMutation = action is "start" or "write" or "kill",
                    Summary = action switch
                    {
                        "start" => string.IsNullOrWhiteSpace(command) ? "Start a background process." : $"Start process: {command}",
                        "write" => string.IsNullOrWhiteSpace(processId) ? "Write input to a background process." : $"Write input to process {processId}.",
                        "kill" => string.IsNullOrWhiteSpace(processId) ? "Terminate a background process." : $"Terminate process {processId}.",
                        "wait" => string.IsNullOrWhiteSpace(processId) ? "Wait for a background process." : $"Wait for process {processId}.",
                        "log" => string.IsNullOrWhiteSpace(processId) ? "Read background process logs." : $"Read logs for process {processId}.",
                        "poll" => string.IsNullOrWhiteSpace(processId) ? "Check background process status." : $"Check status for process {processId}.",
                        _ => "Inspect background processes."
                    }
                };
            }

            if (toolName.Equals("automation", StringComparison.OrdinalIgnoreCase))
            {
                var action = GetString(root, "action") ?? "list";
                var automationId = GetString(root, "automation_id") ?? GetString(root, "id");
                var name = GetString(root, "name");
                return new ToolActionDescriptor
                {
                    Action = action,
                    IsMutation = action is "create" or "update" or "pause" or "resume" or "run",
                    Summary = action switch
                    {
                        "create" => string.IsNullOrWhiteSpace(name) ? "Create an automation." : $"Create automation '{name}'.",
                        "update" => string.IsNullOrWhiteSpace(automationId) ? "Update an automation." : $"Update automation {automationId}.",
                        "pause" => string.IsNullOrWhiteSpace(automationId) ? "Pause an automation." : $"Pause automation {automationId}.",
                        "resume" => string.IsNullOrWhiteSpace(automationId) ? "Resume an automation." : $"Resume automation {automationId}.",
                        "run" => string.IsNullOrWhiteSpace(automationId) ? "Run an automation." : $"Run automation {automationId}.",
                        "preview" => "Preview an automation.",
                        "get" => string.IsNullOrWhiteSpace(automationId) ? "Read automation details." : $"Read automation {automationId}.",
                        _ => "List automations."
                    }
                };
            }

            if (toolName.Equals("todo", StringComparison.OrdinalIgnoreCase))
            {
                var action = GetString(root, "action") ?? "list";
                return new ToolActionDescriptor
                {
                    Action = action,
                    IsMutation = action is not "list",
                    Summary = action switch
                    {
                        "add" => "Add a todo item.",
                        "update" => "Update a todo item.",
                        "complete" => "Complete a todo item.",
                        "remove" => "Remove a todo item.",
                        "clear" => "Clear all todo items.",
                        _ => "List todo items."
                    }
                };
            }
        }
        catch
        {
        }

        return new ToolActionDescriptor
        {
            Summary = $"Execute tool '{toolName}'."
        };
    }

    public static bool SupportsActionAwareApproval(string toolName)
        => toolName.Equals("process", StringComparison.OrdinalIgnoreCase)
           || toolName.Equals("automation", StringComparison.OrdinalIgnoreCase);

    public static bool IsMutationCapable(string toolName, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        if (AlwaysMutatingTools.Contains(toolName))
            return true;

        return Resolve(toolName, argumentsJson).IsMutation;
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
