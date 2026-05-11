using OpenClaw.Core.Models;

namespace OpenClaw.Core.Governance;

public static class ToolGovernanceDescriptorCatalog
{
    private static readonly IReadOnlyDictionary<string, ToolGovernanceDescriptor> Descriptors =
        BuildDescriptors().ToDictionary(static item => item.Name, StringComparer.Ordinal);

    public static IReadOnlyCollection<string> BuiltInToolNames => Descriptors.Keys.ToArray();

    public static ToolGovernanceDescriptor Resolve(
        string toolName,
        string description,
        ToolActionDescriptor actionDescriptor)
    {
        var descriptor = Descriptors.TryGetValue(toolName, out var known)
            ? known with { Description = string.IsNullOrWhiteSpace(known.Description) ? description : known.Description }
            : CreateFallback(toolName, description);

        if (actionDescriptor.RequiresApproval || actionDescriptor.IsMutation)
        {
            descriptor = descriptor with
            {
                RequiresApproval = descriptor.RequiresApproval || actionDescriptor.RequiresApproval,
                ReadOnly = !actionDescriptor.IsMutation && descriptor.ReadOnly,
                RiskLevel = MaxRisk(descriptor.RiskLevel, ParseRisk(actionDescriptor.RiskLevel) ?? ToolGovernanceRiskLevel.Medium)
            };
        }

        return descriptor;
    }

    public static bool Contains(string toolName)
        => Descriptors.ContainsKey(toolName);

    private static ToolGovernanceDescriptor CreateFallback(string toolName, string description)
        => new()
        {
            Name = toolName,
            Description = description,
            Category = "plugin",
            RiskLevel = ToolGovernanceRiskLevel.Medium,
            ReadOnly = false,
            Capabilities = ["plugin.invoke"]
        };

    private static ToolGovernanceRiskLevel MaxRisk(ToolGovernanceRiskLevel left, ToolGovernanceRiskLevel right)
        => (int)left >= (int)right ? left : right;

    private static ToolGovernanceRiskLevel? ParseRisk(string? riskLevel)
    {
        if (string.IsNullOrWhiteSpace(riskLevel))
            return null;

        return riskLevel.Trim().ToLowerInvariant() switch
        {
            "low" => ToolGovernanceRiskLevel.Low,
            "medium" => ToolGovernanceRiskLevel.Medium,
            "high" => ToolGovernanceRiskLevel.High,
            "critical" => ToolGovernanceRiskLevel.Critical,
            _ => null
        };
    }

    private static IReadOnlyList<ToolGovernanceDescriptor> BuildDescriptors() =>
    [
        Read("read_file", "filesystem", ["filesystem.read"], fileSystem: true),
        Write("write_file", "filesystem", ToolGovernanceRiskLevel.High, ["filesystem.write"], fileSystem: true),
        Write("edit_file", "filesystem", ToolGovernanceRiskLevel.High, ["filesystem.read", "filesystem.write"], fileSystem: true),
        Write("apply_patch", "filesystem", ToolGovernanceRiskLevel.High, ["filesystem.read", "filesystem.write"], fileSystem: true),
        Write("shell", "execution", ToolGovernanceRiskLevel.Critical, ["process.execute", "filesystem.read", "filesystem.write"], fileSystem: true, executeCode: true, approval: true),
        Write("process", "execution", ToolGovernanceRiskLevel.Critical, ["process.execute", "process.control", "filesystem.read", "filesystem.write"], fileSystem: true, executeCode: true, approval: true),
        Write("code_exec", "execution", ToolGovernanceRiskLevel.Critical, ["code.execute", "filesystem.read", "filesystem.write"], fileSystem: true, executeCode: true, approval: true),
        Write("git", "source-control", ToolGovernanceRiskLevel.High, ["source.read", "source.write", "network.write"], network: true, fileSystem: true, external: true, approval: true),

        Write("memory", "memory", ToolGovernanceRiskLevel.Medium, ["memory.read", "memory.write"]),
        Read("memory_get", "memory", ["memory.read"]),
        Read("memory_search", "memory", ["memory.search"]),
        Write("project_memory", "memory", ToolGovernanceRiskLevel.Medium, ["memory.read", "memory.write"]),
        Read("session_search", "session", ["session.search"]),
        Read("sessions", "session", ["session.read", "session.message"]),
        Read("sessions_history", "session", ["session.read"]),
        Write("sessions_send", "session", ToolGovernanceRiskLevel.Medium, ["session.message", "message.send"], external: true),
        Write("sessions_spawn", "session", ToolGovernanceRiskLevel.Medium, ["session.create"]),
        Read("session_status", "session", ["session.read"]),
        Write("sessions_yield", "session", ToolGovernanceRiskLevel.Medium, ["session.message", "message.send"], external: true),
        Read("agents_list", "delegation", ["agent.list"]),
        Write("delegate_agent", "delegation", ToolGovernanceRiskLevel.High, ["agent.delegate", "tool.invoke"], approval: true),

        Read("profile_read", "profile", ["profile.read"]),
        Write("profile_write", "profile", ToolGovernanceRiskLevel.Medium, ["profile.write"]),
        Read("todo", "productivity", ["todo.read", "todo.write"]),
        Write("automation", "automation", ToolGovernanceRiskLevel.High, ["automation.read", "automation.write", "automation.run"], approval: true),
        Read("cron", "automation", ["automation.read", "automation.run"]),
        Read("gateway", "runtime", ["runtime.read"]),

        Write("message", "messaging", ToolGovernanceRiskLevel.High, ["message.send", "data.export"], external: true, approval: true),
        Read("web_search", "network", ["network.read", "external.http"], network: true, external: true),
        Read("web_fetch", "network", ["network.read", "external.http"], network: true, external: true),
        Read("x_search", "network", ["network.read", "external.http"], network: true, external: true),
        Write("browser", "browser", ToolGovernanceRiskLevel.High, ["browser.navigate", "browser.evaluate", "network.read", "external.http"], network: true, executeCode: true, external: true, approval: true),

        Read("vision_analyze", "multimodal", ["vision.analyze", "data.export"], external: true),
        Write("text_to_speech", "multimodal", ToolGovernanceRiskLevel.Medium, ["audio.generate", "data.export"], external: true),
        Write("image_gen", "multimodal", ToolGovernanceRiskLevel.Medium, ["image.generate", "data.export"], network: true, external: true),
        Read("pdf_read", "document", ["filesystem.read", "document.read"], fileSystem: true),

        Write("canvas_present", "canvas", ToolGovernanceRiskLevel.Low, ["ui.present"]),
        Write("canvas_hide", "canvas", ToolGovernanceRiskLevel.Low, ["ui.present"]),
        Write("canvas_navigate", "canvas", ToolGovernanceRiskLevel.Medium, ["ui.navigate", "network.read"], network: true),
        Read("canvas_snapshot", "canvas", ["ui.snapshot"]),
        Write("a2ui_push", "canvas", ToolGovernanceRiskLevel.Medium, ["ui.write"]),
        Write("a2ui_reset", "canvas", ToolGovernanceRiskLevel.Medium, ["ui.write"]),
        Write("a2ui_eval", "canvas", ToolGovernanceRiskLevel.High, ["ui.evaluate", "code.execute"], executeCode: true, approval: true),

        Write("external_cli", "external-cli", ToolGovernanceRiskLevel.High, ["process.execute", "external.cli"], fileSystem: true, executeCode: true, approval: true),
        Write("payment", "payment", ToolGovernanceRiskLevel.Critical, ["payment.execute", "data.export"], external: true, approval: true),
        Read("stream_echo", "diagnostic", ["diagnostic.echo"]),

        Write("calendar", "calendar", ToolGovernanceRiskLevel.High, ["calendar.read", "calendar.write", "data.export"], network: true, external: true, approval: true),
        Write("email", "email", ToolGovernanceRiskLevel.High, ["email.read", "email.send", "data.export"], network: true, external: true, approval: true),
        Write("inbox_zero", "email", ToolGovernanceRiskLevel.High, ["email.read", "email.write", "data.export"], network: true, external: true, approval: true),
        Write("database", "database", ToolGovernanceRiskLevel.High, ["database.read", "database.write"], fileSystem: true, approval: true),
        Read("home_assistant", "home-automation", ["home_assistant.read", "network.read"], network: true),
        Write("home_assistant_write", "home-automation", ToolGovernanceRiskLevel.High, ["home_assistant.write", "network.write"], network: true, external: true, approval: true),
        Read("mqtt", "iot", ["mqtt.read", "network.read"], network: true),
        Write("mqtt_publish", "iot", ToolGovernanceRiskLevel.High, ["mqtt.publish", "network.write"], network: true, external: true, approval: true),
        Read("notion", "notion", ["notion.read", "network.read"], network: true, external: true),
        Write("notion_write", "notion", ToolGovernanceRiskLevel.High, ["notion.write", "network.write", "data.export"], network: true, external: true, approval: true)
    ];

    private static ToolGovernanceDescriptor Read(
        string name,
        string category,
        string[] capabilities,
        bool network = false,
        bool fileSystem = false,
        bool external = false)
        => new()
        {
            Name = name,
            Category = category,
            RiskLevel = ToolGovernanceRiskLevel.Low,
            ReadOnly = true,
            CanAccessNetwork = network,
            CanAccessFileSystem = fileSystem,
            CanSendDataExternally = external,
            Capabilities = capabilities
        };

    private static ToolGovernanceDescriptor Write(
        string name,
        string category,
        ToolGovernanceRiskLevel risk,
        string[] capabilities,
        bool network = false,
        bool fileSystem = false,
        bool executeCode = false,
        bool external = false,
        bool approval = false)
        => new()
        {
            Name = name,
            Category = category,
            RiskLevel = risk,
            RequiresApproval = approval,
            ReadOnly = false,
            CanAccessNetwork = network,
            CanAccessFileSystem = fileSystem,
            CanExecuteCode = executeCode,
            CanSendDataExternally = external,
            Capabilities = capabilities
        };
}
