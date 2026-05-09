using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.ExternalCli;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Agent.Tools;

public sealed class ExternalCliTool : IToolWithContext, IToolActionDescriptorProvider
{
    private readonly IExternalCliConnectorRegistry _registry;
    private readonly IExternalCliRunner _runner;
    private readonly IExternalCliAuditSink _audit;
    private readonly IExternalCliEventSink _events;

    public ExternalCliTool(
        IExternalCliConnectorRegistry registry,
        IExternalCliRunner runner,
        IExternalCliAuditSink? audit = null,
        IExternalCliEventSink? events = null)
    {
        _registry = registry;
        _runner = runner;
        _audit = audit ?? new NoopExternalCliAuditSink();
        _events = events ?? new NoopExternalCliEventSink();
    }

    public string Name => "external_cli";
    public string Description => "Run governed allowlisted external CLI commands by connector and command name. This is not a free-form shell.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "action":{"type":"string","enum":["list_connectors","connector_status","list_commands","command_schema","preview","execute"],"default":"list_connectors"},
        "connector":{"type":"string","description":"Configured connector name, such as gh, az, kubectl, stripe, or lark."},
        "command":{"type":"string","description":"Allowlisted command name inside the connector."},
        "parameters":{"type":"object","additionalProperties":true,"description":"Named command parameters. Unknown parameters are rejected unless the command explicitly allows them."},
        "execute_dry_run":{"type":"boolean","default":false,"description":"For preview only: execute the explicit dry-run template when the command supports it."},
        "approved_fingerprint":{"type":"string","description":"Optional approval fingerprint for direct operator-approved execution."},
        "approval_reason":{"type":"string","description":"Optional operator approval reason."}
      },
      "required":["action"]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: external_cli requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        ExternalCliToolRequest request;
        try
        {
            request = JsonSerializer.Deserialize(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                CoreJsonContext.Default.ExternalCliToolRequest) ?? new ExternalCliToolRequest();
        }
        catch (JsonException ex)
        {
            return $"Error: Invalid external_cli arguments: {ex.Message}";
        }

        try
        {
            return request.Action switch
            {
                "list_connectors" => Serialize(new ExternalCliConnectorListResponse
                {
                    Items = _registry.ListConnectors()
                }, CoreJsonContext.Default.ExternalCliConnectorListResponse),
                "connector_status" => Serialize(
                    await _registry.GetStatusAsync(Require(request.Connector, "connector"), ct),
                    CoreJsonContext.Default.ExternalCliConnectorStatus),
                "list_commands" => Serialize(
                    _registry.ListCommands(Require(request.Connector, "connector")),
                    CoreJsonContext.Default.ExternalCliCommandListResponse),
                "command_schema" => Serialize(
                    _registry.GetCommandSchema(Require(request.Connector, "connector"), Require(request.Command, "command")),
                    CoreJsonContext.Default.ExternalCliCommandSchemaResponse),
                "preview" => await PreviewAsync(request, context, ct),
                "execute" => await ExecuteCommandAsync(request, context, ct),
                _ => "Error: Unknown action. Valid actions are list_connectors, connector_status, list_commands, command_schema, preview, and execute."
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            RecordEvent(context, "blocked_by_policy", "warning", ex.Message, request);
            return $"Error: {ex.Message}";
        }
    }

    public ToolActionDescriptor ResolveActionDescriptor(string argumentsJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                CoreJsonContext.Default.ExternalCliToolRequest) ?? new ExternalCliToolRequest();
            if (!string.Equals(request.Action, "execute", StringComparison.OrdinalIgnoreCase))
            {
                return new ToolActionDescriptor
                {
                    Action = request.Action,
                    IsMutation = false,
                    RequiresApproval = false,
                    Summary = BuildSummary(request)
                };
            }

            var prepared = _registry.BuildPreview(ToPreviewRequest(request), dryRun: false);
            return new ToolActionDescriptor
            {
                Action = request.Action,
                IsMutation = !prepared.Preview.ReadOnly,
                RequiresApproval = prepared.Preview.RequiresApproval,
                Summary = BuildSummary(request),
                ApprovalFingerprint = prepared.Preview.Fingerprint,
                RiskLevel = prepared.Preview.RiskLevel,
                ReadOnly = prepared.Preview.ReadOnly
            };
        }
        catch
        {
            return ToolActionPolicyResolver.Resolve(Name, argumentsJson);
        }
    }

    private async Task<string> PreviewAsync(ExternalCliToolRequest request, ToolExecutionContext context, CancellationToken ct)
    {
        var prepared = _registry.BuildPreview(ToPreviewRequest(request), dryRun: request.ExecuteDryRun);
        RecordEvent(context, request.ExecuteDryRun ? "dry_run_previewed" : "previewed", "info", $"Previewed external CLI command {prepared.ConnectorName}/{prepared.CommandName}.", request, prepared.Preview);

        ExternalCliExecutionResult? dryRunResult = null;
        if (request.ExecuteDryRun)
        {
            dryRunResult = await _runner.ExecuteAsync(prepared, ct);
            RecordEvent(context, "dry_run_executed", dryRunResult.Success ? "info" : "warning", $"Dry-run executed for external CLI command {prepared.ConnectorName}/{prepared.CommandName}.", request, prepared.Preview);
            RecordAudit(context, dryRunResult, request);
        }

        return Serialize(new ExternalCliPreviewResponse
        {
            Preview = prepared.Preview,
            DryRunResult = dryRunResult
        }, CoreJsonContext.Default.ExternalCliPreviewResponse);
    }

    private async Task<string> ExecuteCommandAsync(ExternalCliToolRequest request, ToolExecutionContext context, CancellationToken ct)
    {
        var prepared = _registry.BuildPreview(ToPreviewRequest(request), dryRun: false);
        var result = await _runner.ExecuteAsync(prepared, ct);
        RecordEvent(
            context,
            result.TimedOut ? "command_timed_out" : result.Success ? "command_executed" : "command_failed",
            result.Success ? "info" : "warning",
            $"External CLI command {prepared.ConnectorName}/{prepared.CommandName} completed with exit code {result.ExitCode}.",
            request,
            prepared.Preview);
        RecordAudit(context, result, request);
        return Serialize(result, CoreJsonContext.Default.ExternalCliExecutionResult);
    }

    private void RecordAudit(ToolExecutionContext context, ExternalCliExecutionResult result, ExternalCliToolRequest request)
    {
        _audit.Record(new ExternalCliAuditEntry
        {
            Id = $"ecli_{Guid.NewGuid():N}"[..21],
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = context.Session.Id,
            ChannelId = context.Session.ChannelId,
            SenderId = context.Session.SenderId,
            Connector = result.Preview.Connector,
            Command = result.Preview.Command,
            Executable = result.Preview.Executable,
            ArgsHash = ExternalCliConnectorRegistry.ComputeArgsHash(result.Preview.Arguments.ToArray()),
            RedactedArgsPreview = result.Preview.RedactedCommandLine,
            ParametersHash = result.Preview.ParametersHash,
            ApprovalFingerprint = request.ApprovedFingerprint,
            ExitCode = result.ExitCode,
            DurationMs = result.DurationMs,
            TimedOut = result.TimedOut,
            Failed = !result.Success,
            StdoutTruncated = result.StdoutTruncated,
            StderrTruncated = result.StderrTruncated,
            RiskLevel = result.Preview.RiskLevel,
            ReadOnly = result.Preview.ReadOnly,
            WorkingDirectory = result.Preview.WorkingDirectory
        });
    }

    private void RecordEvent(
        ToolExecutionContext context,
        string action,
        string severity,
        string summary,
        ExternalCliToolRequest request,
        ExternalCliInvocationPreview? preview = null)
    {
        _events.Record(new ExternalCliRuntimeEvent
        {
            SessionId = context.Session.Id,
            ChannelId = context.Session.ChannelId,
            SenderId = context.Session.SenderId,
            Action = action,
            Severity = severity,
            Summary = summary,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["connector"] = preview?.Connector ?? request.Connector ?? "",
                ["command"] = preview?.Command ?? request.Command ?? "",
                ["riskLevel"] = preview?.RiskLevel ?? "",
                ["readOnly"] = preview is null ? "" : preview.ReadOnly.ToString().ToLowerInvariant(),
                ["fingerprint"] = preview?.Fingerprint ?? ""
            }
        });
    }

    private static ExternalCliPreviewRequest ToPreviewRequest(ExternalCliToolRequest request)
        => new()
        {
            Connector = request.Connector,
            Command = request.Command,
            Parameters = request.Parameters,
            ExecuteDryRun = request.ExecuteDryRun
        };

    private static string BuildSummary(ExternalCliToolRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Connector) || string.IsNullOrWhiteSpace(request.Command))
            return "Use an external CLI connector.";
        return request.Action switch
        {
            "execute" => $"Execute external CLI command {request.Connector}/{request.Command}.",
            "preview" => $"Preview external CLI command {request.Connector}/{request.Command}.",
            _ => $"Inspect external CLI connector {request.Connector}."
        };
    }

    private static string Require(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} is required.") : value.Trim();

    private static string Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Serialize(value, typeInfo);
}
