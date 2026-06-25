namespace OpenClaw.Core.Skills;

public sealed class MetaExecutionContext
{
    public MetaExecutionContext(
        string? input,
        IReadOnlyDictionary<string, string>? outputs = null,
        IReadOnlyDictionary<string, object>? inputs = null,
        IReadOnlyDictionary<string, object>? steps = null,
        IReadOnlyList<ToolDescriptor>? tools = null)
    {
        Input = input ?? string.Empty;
        Outputs = outputs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalizedInputs = inputs is null
            ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(inputs, StringComparer.OrdinalIgnoreCase);

        if (!normalizedInputs.ContainsKey("user_message"))
        {
            normalizedInputs["user_message"] = Input;
        }

        Inputs = normalizedInputs;
        Steps = steps ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        Tools = tools ?? [];
    }

    public string Input { get; }

    public IReadOnlyDictionary<string, string> Outputs { get; }

    public IReadOnlyDictionary<string, object> Inputs { get; }

    public IReadOnlyDictionary<string, object> Steps { get; }

    /// <summary>
    /// Available tools for the current execution context. Populated by the runtime
    /// so that templates can reference <c>{{ tools | json }}</c> for dynamic discovery.
    /// </summary>
    public IReadOnlyList<ToolDescriptor> Tools { get; }
}