namespace OpenClaw.Testing;

public sealed class ScenarioGateResult
{
    public bool Passed => Issues.All(static issue => !issue.IsError);
    public List<ScenarioGateIssue> Issues { get; init; } = [];
}

public sealed class ScenarioGateIssue
{
    public string ScenarioId { get; init; } = "";
    public bool IsError { get; init; } = true;
    public string Message { get; init; } = "";
}

public sealed class ScenarioQualityGate
{
    private static readonly HashSet<string> ApprovalOrSafetyOracleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ScenarioOracleTypes.ApprovalRequired,
        ScenarioOracleTypes.ApprovalNotRequired,
        ScenarioOracleTypes.NoUnsafeTool
    };

    public ScenarioGateResult Evaluate(IEnumerable<AgentScenario> scenarios)
    {
        var list = scenarios.ToList();
        var issues = new List<ScenarioGateIssue>();

        if (list.Count == 0)
        {
            issues.Add(new ScenarioGateIssue { ScenarioId = "<none>", Message = "No scenario JSON files were found." });
            return new ScenarioGateResult { Issues = issues };
        }

        foreach (var scenario in list)
        {
            if (string.IsNullOrWhiteSpace(scenario.Id))
                issues.Add(Error(scenario, "Scenario is missing an id."));

            if (string.IsNullOrWhiteSpace(scenario.Title))
                issues.Add(Error(scenario, "Scenario is missing a title."));

            if (!HasExplicitExpectedBehavior(scenario.Expected))
                issues.Add(Error(scenario, "Scenario has no explicit expected behavior."));

            if (scenario.Risk is ScenarioRisk.High or ScenarioRisk.Critical && scenario.Oracles.Count == 0)
                issues.Add(Error(scenario, "High-risk and critical scenarios must declare at least one oracle."));

            if (IsToolUseScenario(scenario) &&
                scenario.Expected.MustCallTools.Count == 0 &&
                scenario.Expected.MustNotCallTools.Count == 0)
            {
                issues.Add(Error(scenario, "Tool-use scenarios must declare mustCallTools or mustNotCallTools."));
            }

            if (IsApprovalOrSafetyScenario(scenario) && !HasApprovalOrSafetyOracle(scenario))
                issues.Add(Error(scenario, "Approval or safety scenarios must declare an approval or safety oracle."));
        }

        foreach (var duplicate in list
            .Where(static scenario => !string.IsNullOrWhiteSpace(scenario.Id))
            .GroupBy(static scenario => scenario.Id, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1))
        {
            issues.Add(new ScenarioGateIssue
            {
                ScenarioId = duplicate.Key,
                Message = $"Duplicate scenario id '{duplicate.Key}'."
            });
        }

        return new ScenarioGateResult { Issues = issues };
    }

    private static ScenarioGateIssue Error(AgentScenario scenario, string message)
        => new()
        {
            ScenarioId = string.IsNullOrWhiteSpace(scenario.Id) ? "<missing>" : scenario.Id,
            Message = message
        };

    private static bool HasExplicitExpectedBehavior(ScenarioExpected expected)
        => expected.MustCallTools.Count > 0 ||
           expected.MustNotCallTools.Count > 0 ||
           expected.FinalAnswerContains.Count > 0 ||
           expected.FinalAnswerMustNotContain.Count > 0 ||
           expected.MaxToolCalls.HasValue ||
           expected.RequiresApproval.HasValue ||
           expected.ExpectedState.Count > 0 ||
           expected.ExpectedEvents.Count > 0 ||
           expected.ForbiddenEvents.Count > 0;

    private static bool IsToolUseScenario(AgentScenario scenario)
        => HasToken(scenario, "tool-use") || HasToken(scenario, "tool") || HasToken(scenario, "tools");

    private static bool IsApprovalOrSafetyScenario(AgentScenario scenario)
        => scenario.Expected.RequiresApproval.HasValue ||
           HasToken(scenario, "approval") ||
           HasToken(scenario, "safety") ||
           HasToken(scenario, "security");

    private static bool HasToken(AgentScenario scenario, string token)
        => string.Equals(scenario.Type, token, StringComparison.OrdinalIgnoreCase) ||
           scenario.Tags.Any(tag => string.Equals(tag, token, StringComparison.OrdinalIgnoreCase));

    private static bool HasApprovalOrSafetyOracle(AgentScenario scenario)
        => scenario.Oracles.Any(oracle => ApprovalOrSafetyOracleTypes.Contains(oracle.Type));
}
