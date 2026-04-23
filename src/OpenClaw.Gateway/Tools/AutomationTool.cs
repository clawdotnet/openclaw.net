using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway.Tools;

internal sealed class AutomationTool : IToolWithContext
{
    private readonly GatewayAutomationService _automations;
    private readonly MessagePipeline _pipeline;

    public AutomationTool(GatewayAutomationService automations, MessagePipeline pipeline)
    {
        _automations = automations;
        _pipeline = pipeline;
    }

    public string Name => "automation";
    public string Description => "Inspect and manage scheduled automations. Supports list, get, runs, preview, create, update, pause, resume, run, replay, and clear_quarantine.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "action":{"type":"string","enum":["list","get","runs","preview","create","update","pause","resume","run","replay","clear_quarantine"],"default":"list"},
        "automation_id":{"type":"string"},
        "run_id":{"type":"string"},
        "name":{"type":"string"},
        "schedule":{"type":"string"},
        "timezone":{"type":"string"},
        "prompt":{"type":"string"},
        "model_id":{"type":"string"},
        "delivery_channel_id":{"type":"string"},
        "delivery_recipient_id":{"type":"string"},
        "delivery_subject":{"type":"string"},
        "session_id":{"type":"string"},
        "run_on_startup":{"type":"boolean"},
        "enabled":{"type":"boolean"},
        "tags":{"type":"array","items":{"type":"string"}},
        "verification":{"type":"object"},
        "retry_policy":{"type":"object"}
      },
      "required":["action"]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: automation requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var action = GetString(root, "action") ?? "list";

        return action switch
        {
            "list" => await ListAsync(ct),
            "get" => await GetAsync(root, ct),
            "runs" => await RunsAsync(root, ct),
            "preview" => Preview(root, context),
            "create" => await SaveAsync(root, context, isUpdate: false, ct),
            "update" => await SaveAsync(root, context, isUpdate: true, ct),
            "pause" => await SetEnabledAsync(root, enabled: false, ct),
            "resume" => await SetEnabledAsync(root, enabled: true, ct),
            "run" => await RunAsync(root, ct),
            "replay" => await ReplayAsync(root, ct),
            "clear_quarantine" => await ClearQuarantineAsync(root, ct),
            _ => "Error: Unknown action. Valid actions are list, get, runs, preview, create, update, pause, resume, run, replay, and clear_quarantine."
        };
    }

    private async Task<string> ListAsync(CancellationToken ct)
    {
        var items = await _automations.ListAsync(ct);
        if (items.Count == 0)
            return "No automations found.";

        var sb = new StringBuilder();
        foreach (var item in items)
            sb.AppendLine($"{item.Id} [{(item.Enabled ? "enabled" : "paused")}] {item.Schedule} {item.Name}");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> GetAsync(JsonElement root, CancellationToken ct)
    {
        var automationId = GetString(root, "automation_id");
        if (string.IsNullOrWhiteSpace(automationId))
            return "Error: automation_id is required.";

        var automation = await _automations.GetAsync(automationId, ct);
        if (automation is null)
            return $"Error: automation '{automationId}' was not found.";

        var state = await _automations.GetRunStateAsync(automationId, ct);
        return $"id: {automation.Id}\nname: {automation.Name}\nenabled: {automation.Enabled}\nschedule: {automation.Schedule}\nprompt:\n{automation.Prompt}\nlast_outcome: {state?.Outcome ?? "never"}\nlifecycle: {state?.LifecycleState ?? AutomationLifecycleStates.Never}\nverification: {state?.VerificationStatus ?? AutomationVerificationStatuses.NotRun}\nhealth: {state?.HealthState ?? AutomationHealthStates.Unknown}";
    }

    private async Task<string> RunsAsync(JsonElement root, CancellationToken ct)
    {
        var automationId = GetString(root, "automation_id");
        if (string.IsNullOrWhiteSpace(automationId))
            return "Error: automation_id is required.";

        var runs = await _automations.ListRunRecordsAsync(automationId, 20, ct);
        if (runs.Count == 0)
            return $"No stored runs found for automation '{automationId}'.";

        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            sb.AppendLine($"{run.RunId} [{run.TriggerSource}] {run.LifecycleState} / {run.VerificationStatus}");
        }

        return sb.ToString().TrimEnd();
    }

    private string Preview(JsonElement root, ToolExecutionContext context)
    {
        var definition = BuildDefinition(root, context, keepExistingId: false);
        var preview = _automations.BuildPreview(definition);
        var issues = preview.Issues.Count == 0
            ? "none"
            : string.Join(", ", preview.Issues.Select(static item => item.Message));
        return $"name: {preview.Definition.Name}\nschedule: {preview.Definition.Schedule}\nestimated_runs_per_month: {preview.EstimatedRunsPerMonth}\nissues: {issues}\nprompt:\n{preview.PromptPreview}";
    }

    private async Task<string> SaveAsync(JsonElement root, ToolExecutionContext context, bool isUpdate, CancellationToken ct)
    {
        var definition = BuildDefinition(root, context, keepExistingId: isUpdate);
        if (isUpdate && string.IsNullOrWhiteSpace(definition.Id))
            return "Error: automation_id is required for update.";

        var saved = await _automations.SaveAsync(definition, ct);
        return $"{(isUpdate ? "Updated" : "Created")} automation {saved.Id}.";
    }

    private async Task<string> SetEnabledAsync(JsonElement root, bool enabled, CancellationToken ct)
    {
        var automationId = GetString(root, "automation_id");
        if (string.IsNullOrWhiteSpace(automationId))
            return "Error: automation_id is required.";

        var existing = await _automations.GetAsync(automationId, ct);
        if (existing is null)
            return $"Error: automation '{automationId}' was not found.";

        await _automations.SaveAsync(new AutomationDefinition
        {
            Id = existing.Id,
            Name = existing.Name,
            Enabled = enabled,
            Schedule = existing.Schedule,
            Timezone = existing.Timezone,
            Prompt = existing.Prompt,
            ModelId = existing.ModelId,
            RunOnStartup = existing.RunOnStartup,
            SessionId = existing.SessionId,
            DeliveryChannelId = existing.DeliveryChannelId,
            DeliveryRecipientId = existing.DeliveryRecipientId,
            DeliverySubject = existing.DeliverySubject,
            Tags = existing.Tags,
            IsDraft = existing.IsDraft,
            Source = existing.Source,
            TemplateKey = existing.TemplateKey,
            Verification = existing.Verification,
            RetryPolicy = existing.RetryPolicy,
            CreatedAtUtc = existing.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, ct);

        return $"{(enabled ? "Resumed" : "Paused")} automation {automationId}.";
    }

    private async Task<string> RunAsync(JsonElement root, CancellationToken ct)
    {
        var automationId = GetString(root, "automation_id");
        if (string.IsNullOrWhiteSpace(automationId))
            return "Error: automation_id is required.";

        var result = await _automations.RunNowAsync(automationId, _pipeline, ct);
        return result.Status switch
        {
            "queued" => string.IsNullOrWhiteSpace(result.RunId)
                ? $"Automation {automationId} queued."
                : $"Automation {automationId} queued as run {result.RunId}.",
            "already_running" => $"Error: automation '{automationId}' is already running.",
            "quarantined" => $"Error: automation '{automationId}' is quarantined.",
            _ => $"Error: automation '{automationId}' was not found."
        };
    }

    private async Task<string> ReplayAsync(JsonElement root, CancellationToken ct)
    {
        var automationId = GetString(root, "automation_id");
        var runId = GetString(root, "run_id");
        if (string.IsNullOrWhiteSpace(automationId) || string.IsNullOrWhiteSpace(runId))
            return "Error: automation_id and run_id are required.";

        var result = await _automations.ReplayAsync(automationId, runId, _pipeline, ct);
        return result.Status switch
        {
            "queued" => string.IsNullOrWhiteSpace(result.RunId)
                ? $"Replay queued for automation {automationId}."
                : $"Replay queued for automation {automationId} as run {result.RunId}.",
            "already_running" => $"Error: automation '{automationId}' is already running.",
            _ => $"Error: replay for automation '{automationId}' could not be queued."
        };
    }

    private async Task<string> ClearQuarantineAsync(JsonElement root, CancellationToken ct)
    {
        var automationId = GetString(root, "automation_id");
        if (string.IsNullOrWhiteSpace(automationId))
            return "Error: automation_id is required.";

        await _automations.ClearQuarantineAsync(automationId, ct);
        return $"Cleared quarantine for automation {automationId}.";
    }

    private static AutomationDefinition BuildDefinition(JsonElement root, ToolExecutionContext context, bool keepExistingId)
        => new()
        {
            Id = keepExistingId ? (GetString(root, "automation_id") ?? "") : GetString(root, "automation_id") ?? "",
            Name = GetString(root, "name") ?? "Automation",
            Enabled = !root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False,
            Schedule = GetString(root, "schedule") ?? "@hourly",
            Timezone = GetString(root, "timezone"),
            Prompt = GetString(root, "prompt") ?? "",
            ModelId = GetString(root, "model_id"),
            RunOnStartup = root.TryGetProperty("run_on_startup", out var runOnStartup) && runOnStartup.ValueKind == JsonValueKind.True,
            SessionId = GetString(root, "session_id") ?? context.Session.Id,
            DeliveryChannelId = GetString(root, "delivery_channel_id") ?? context.Session.ChannelId,
            DeliveryRecipientId = GetString(root, "delivery_recipient_id") ?? context.Session.SenderId,
            DeliverySubject = GetString(root, "delivery_subject"),
            Tags = root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString() ?? "").Where(static item => !string.IsNullOrWhiteSpace(item)).ToArray()
                : [],
            IsDraft = false,
            Source = "agent",
            TemplateKey = null,
            Verification = root.TryGetProperty("verification", out var verification) && verification.ValueKind == JsonValueKind.Object
                ? verification.Deserialize(CoreJsonContext.Default.VerificationPolicy)
                : null,
            RetryPolicy = root.TryGetProperty("retry_policy", out var retryPolicy) && retryPolicy.ValueKind == JsonValueKind.Object
                ? retryPolicy.Deserialize(CoreJsonContext.Default.AutomationRetryPolicy) ?? new AutomationRetryPolicy()
                : new AutomationRetryPolicy()
        };

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
