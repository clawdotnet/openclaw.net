using System.Text.Json;
using OpenClaw.Agent;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Testing;

if (args.Length != 4 || !int.TryParse(args[2], out var turn) || turn < 0 || string.IsNullOrWhiteSpace(args[3]))
{
    Console.Error.WriteLine("Usage: <trajectory.jsonl> <exported-session-id> <prompt-turn-index> <expected-answer-text>");
    return 2;
}

// Add project-specific redactors before using captures containing private data.
var redaction = new RedactionPipeline([]);
using var reader = File.OpenText(args[0]);
var fixture = await TrajectoryReplayImporter.ImportAsync(reader, args[1], turn, redaction);
var memoryPath = Path.Join(Path.GetTempPath(), "openclaw-replay-" + Guid.NewGuid().ToString("N"));
try
{
    await using var memory = new FileMemoryStore(memoryPath);
    var result = await RuntimeScenarioRunner.RunReplayAsync(fixture,
        [new() { Type = ScenarioOracleTypes.FinalAnswerContains, Value = JsonSerializer.SerializeToElement(args[3]) }],
        (provider, tools) => new AgentRuntime(provider, tools, memory,
            new LlmProviderConfig { Provider = "deterministic", Model = "trajectory-replay", RetryCount = 0 },
            maxHistoryTurns: 20, maxIterations: fixture.Responses.Count + 1));
    Console.WriteLine(result.Passed ? "Replay passed." : "Replay failed: " + result.FailureSummary);
    return result.Passed ? 0 : 1;
}
finally
{
    if (Directory.Exists(memoryPath)) Directory.Delete(memoryPath, recursive: true);
}
