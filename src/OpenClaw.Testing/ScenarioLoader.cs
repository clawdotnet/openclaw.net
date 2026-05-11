using System.Text.Json;

namespace OpenClaw.Testing;

public sealed class JsonScenarioLoader : IScenarioLoader
{
    public async ValueTask<IReadOnlyList<AgentScenario>> LoadAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directoryPath))
            return [];

        var scenarios = new List<AgentScenario>();
        var files = Directory
            .EnumerateFiles(directoryPath, "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var loaded = document.RootElement.Deserialize(ScenarioJsonContext.Default.ListAgentScenario);
                if (loaded is not null)
                    scenarios.AddRange(loaded.Select(Normalize));
            }
            else
            {
                var loaded = document.RootElement.Deserialize(ScenarioJsonContext.Default.AgentScenario);
                if (loaded is not null)
                    scenarios.Add(Normalize(loaded));
            }
        }

        return scenarios;
    }

    private static AgentScenario Normalize(AgentScenario scenario)
    {
        List<string>? tags = scenario.Tags;
        List<ScenarioOracleDefinition>? oracles = scenario.Oracles;
        Dictionary<string, string>? metadata = scenario.Metadata;
        return new AgentScenario
        {
            Id = scenario.Id ?? "",
            Title = scenario.Title ?? "",
            Description = scenario.Description,
            Risk = scenario.Risk,
            Type = scenario.Type ?? "agent",
            Tags = tags ?? [],
            Input = NormalizeInput(scenario.Input),
            Expected = NormalizeExpected(scenario.Expected),
            Oracles = oracles?.Select(NormalizeOracle).ToList() ?? [],
            ScriptedTrace = NormalizeScriptedTrace(scenario.ScriptedTrace),
            Metadata = CopyMetadata(metadata)
        };
    }

    private static ScenarioInput NormalizeInput(ScenarioInput? input)
    {
        List<string>? context = input?.Context;
        Dictionary<string, string>? variables = input?.Variables;
        return new ScenarioInput
        {
            UserMessage = input?.UserMessage ?? "",
            Context = context ?? [],
            Variables = CopyMetadata(variables)
        };
    }

    private static ScenarioExpected NormalizeExpected(ScenarioExpected? expected)
    {
        List<string>? mustCallTools = expected?.MustCallTools;
        List<string>? mustNotCallTools = expected?.MustNotCallTools;
        List<string>? finalAnswerContains = expected?.FinalAnswerContains;
        List<string>? finalAnswerMustNotContain = expected?.FinalAnswerMustNotContain;
        Dictionary<string, string>? expectedState = expected?.ExpectedState;
        List<string>? expectedEvents = expected?.ExpectedEvents;
        List<string>? forbiddenEvents = expected?.ForbiddenEvents;
        return new ScenarioExpected
        {
            MustCallTools = mustCallTools ?? [],
            MustNotCallTools = mustNotCallTools ?? [],
            FinalAnswerContains = finalAnswerContains ?? [],
            FinalAnswerMustNotContain = finalAnswerMustNotContain ?? [],
            MaxToolCalls = expected?.MaxToolCalls,
            RequiresApproval = expected?.RequiresApproval,
            ExpectedState = CopyMetadata(expectedState),
            ExpectedEvents = expectedEvents ?? [],
            ForbiddenEvents = forbiddenEvents ?? []
        };
    }

    private static ScenarioOracleDefinition NormalizeOracle(ScenarioOracleDefinition oracle)
    {
        List<string>? tools = oracle.Tools;
        Dictionary<string, string>? metadata = oracle.Metadata;
        return new ScenarioOracleDefinition
        {
            Type = oracle.Type ?? "",
            Tool = oracle.Tool,
            Value = oracle.Value,
            Limit = oracle.Limit,
            Tools = tools ?? [],
            Metadata = CopyMetadata(metadata)
        };
    }

    private static ScriptedTrace? NormalizeScriptedTrace(ScriptedTrace? trace)
    {
        if (trace is null)
            return null;

        List<TraceStep>? steps = trace.Steps;
        Dictionary<string, string>? metadata = trace.Metadata;
        return new ScriptedTrace
        {
            FinalAnswer = trace.FinalAnswer,
            Status = trace.Status ?? ScenarioRunStatuses.Completed,
            Steps = steps?.Select(NormalizeStep).ToList() ?? [],
            Metadata = CopyMetadata(metadata)
        };
    }

    private static TraceStep NormalizeStep(TraceStep step)
    {
        Dictionary<string, string>? metadata = step.Metadata;
        return new TraceStep
        {
            Kind = step.Kind ?? TraceStepKinds.Event,
            TimestampUtc = step.TimestampUtc,
            Message = step.Message,
            ToolName = step.ToolName,
            ArgumentsJson = step.ArgumentsJson,
            Result = step.Result,
            Error = step.Error,
            StatePath = step.StatePath,
            From = step.From,
            To = step.To,
            ApprovalId = step.ApprovalId,
            Metadata = CopyMetadata(metadata)
        };
    }

    private static Dictionary<string, string> CopyMetadata(Dictionary<string, string>? metadata)
        => metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);
}
