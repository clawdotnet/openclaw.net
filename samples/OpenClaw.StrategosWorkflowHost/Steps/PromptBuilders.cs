namespace OpenClaw.StrategosWorkflowHost.Steps;

// Per-role prompt construction for the three reviewers. Kept as plain string templates so
// real LLM prompts can be swapped in without touching step wiring.
public static class PromptBuilders
{
    public static string Security(string plan, string request) =>
        $"You are a security reviewer. Plan: {plan}. Request: {request}. Return JSON {{role,verdict,summary,confidence}}.";

    public static string Architecture(string plan, string request) =>
        $"You are an architecture reviewer. Plan: {plan}. Request: {request}. Return JSON {{role,verdict,summary,confidence}}.";

    public static string Cost(string plan, string request) =>
        $"You are a cost reviewer. Plan: {plan}. Request: {request}. Return JSON {{role,verdict,summary,confidence}}.";
}