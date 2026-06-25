using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Core.Skills;

/// <summary>
/// Shared fan_out execution logic consumed by both <c>AgentRuntime</c>
/// and <c>MafAgentRuntime</c>.  Accepts two callbacks so each runtime
/// plugs in its own LLM / tool infrastructure without duplicating the
/// iteration, batching, concurrency, and merge logic.
/// </summary>
public static class MetaFanOutExecutor
{
    /// <summary>
    /// Executes a single fan_out child step.  Returns (output, failureCode).
    /// </summary>
    public delegate Task<(string Output, string? FailureCode)> FanOutChildExecutor(
        MetaSkillStepDefinition template,
        string childId,
        string childInput,
        MetaExecutionContext childContext,
        Session session,
        TurnContext turnCtx,
        CancellationToken ct);

    /// <summary>
    /// Attempts to find and execute one ready fan_out step from the pending set.
    /// Returns <c>true</c> when a fan_out step was processed (even if it failed);
    /// <c>false</c> when no ready fan_out step exists.
    /// </summary>
    public static async Task<bool> TryExecuteFanOutStepAsync(
        Session session,
        SkillDefinition metaSkill,
        IReadOnlyList<MetaSkillStepDefinition> steps,
        IReadOnlyDictionary<string, MetaSkillStepDefinition> stepById,
        Dictionary<string, List<string>> dependentsByStep,
        HashSet<string> pending,
        HashSet<string> blocked,
        Dictionary<string, string> outputs,
        Dictionary<string, string> failureAliases,
        List<MetaStepExecutionResult> stepResults,
        string? input,
        TurnContext turnCtx,
        MetaTemplateRenderer templateRenderer,
        MetaConditionEvaluator conditionEvaluator,
        MetaToolArgumentResolver toolArgumentResolver,
        MetaRoutePlanner routePlanner,
        FanOutChildExecutor childExecutor,
        Action<string, Exception?>? logger,
        CancellationToken ct)
    {
        // Find the first ready fan_out step.
        MetaSkillStepDefinition? fanOutStep = null;
        foreach (var step in steps)
        {
            if (!pending.Contains(step.Id) || blocked.Contains(step.Id))
                continue;

            if (!string.Equals(NormalizeMetaStepKind(step.Kind), "fan_out", StringComparison.Ordinal))
                continue;

            var waiting = false;
            foreach (var dep in step.DependsOn)
            {
                if (blocked.Contains(dep)) { waiting = true; break; }
                if (!outputs.ContainsKey(dep)) { waiting = true; break; }
            }

            if (waiting) continue;

            fanOutStep = step;
            break;
        }

        if (fanOutStep is null)
            return false;

        var metaContext = new MetaExecutionContext(input, outputs);
        var stepArgs = DeserializeStepArgs(fanOutStep.WithJson);
        var continueOnError = GetOptionalBoolean(stepArgs, "continue_on_error") ?? false;

        if (!string.IsNullOrWhiteSpace(fanOutStep.When) && !conditionEvaluator.Evaluate(fanOutStep.When, metaContext))
        {
            BlockStepAndDependents(fanOutStep.Id, blocked, pending, dependentsByStep);
            stepResults.Add(new MetaStepExecutionResult(fanOutStep.Id, fanOutStep.Kind, ToolResultStatuses.Blocked, "condition_false", 0, Continued: false));
            return true;
        }

        // 1. Evaluate iterable expression.
        List<string> items;
        try
        {
            var iterableJson = templateRenderer.Render(fanOutStep.Iterable!, metaContext);
            items = System.Text.Json.JsonSerializer.Deserialize(iterableJson, CoreJsonContext.Default.ListString) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.Invoke($"Fan-out step '{fanOutStep.Id}' iterable evaluation failed.", ex);
            stepResults.Add(new MetaStepExecutionResult(fanOutStep.Id, fanOutStep.Kind, ToolResultStatuses.Failed, "iterable_eval_failed", 0, Continued: false));
            pending.Remove(fanOutStep.Id);
            return true;
        }

        if (items.Count == 0)
        {
            CompleteMetaStepOutput(fanOutStep, fanOutStep.FanOutMergeMode == "json_array" ? "[]" : "", pending, outputs, failureAliases);
            routePlanner.ApplyCompletionRouting(fanOutStep, metaContext, stepById, blocked, pending, dependentsByStep);
            stepResults.Add(new MetaStepExecutionResult(fanOutStep.Id, fanOutStep.Kind, ToolResultStatuses.Completed, null, 0, Continued: false));
            return true;
        }

        // 2. Clone template for each item.
        var template = fanOutStep.FanOutTemplate!;
        var childOutputs = new Dictionary<string, string>(items.Count);
        var maxConcurrency = Math.Max(1, fanOutStep.FanOutMaxConcurrency);

        var fanSw = Stopwatch.StartNew();

        // Batch execution with concurrency control.
        for (var batch = 0; batch < items.Count; batch += maxConcurrency)
        {
            var batchEnd = Math.Min(batch + maxConcurrency, items.Count);
            var batchTasks = new List<Task<(string id, string output, string status, string? failureCode, double durationMs)>>(batchEnd - batch);

            for (var i = batch; i < batchEnd; i++)
            {
                var index = i;
                var item = items[i];
                var childId = $"{fanOutStep.Id}_{index}";
                var childInput = item;

                batchTasks.Add(Task.Run(async () =>
                {
                    var childSw = Stopwatch.StartNew();
                    try
                    {
                        var childContext = new MetaExecutionContext(childInput, outputs);
                        var (childOutput, failureCode) = await childExecutor(
                            template, childId, childInput, childContext,
                            session, turnCtx, ct);
                        childSw.Stop();
                        if (failureCode is not null)
                        {
                            logger?.Invoke($"Fan-out child step '{childId}' failed: {failureCode}", null);
                            return (childId, childOutput, ToolResultStatuses.Failed, failureCode, childSw.Elapsed.TotalMilliseconds);
                        }
                        return (childId, childOutput, ToolResultStatuses.Completed, null, childSw.Elapsed.TotalMilliseconds);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        childSw.Stop();
                        logger?.Invoke($"Fan-out child step '{childId}' threw unhandled exception", ex);
                        return (childId, ex.Message, ToolResultStatuses.Failed, (string?)"child_step_failed", childSw.Elapsed.TotalMilliseconds);
                    }
                }, ct));
            }

            var batchResults = await Task.WhenAll(batchTasks);
            foreach (var (id, output, status, failureCode, durationMs) in batchResults)
            {
                childOutputs[id] = output;
                stepResults.Add(new MetaStepExecutionResult(id, template.Kind, status, failureCode, durationMs, Continued: continueOnError));
            }
        }

        fanSw.Stop();

        // 3. Merge outputs.
        var mergedOutput = fanOutStep.FanOutMergeMode switch
        {
            "json_array" => System.Text.Json.JsonSerializer.Serialize(childOutputs.Values.ToList(), CoreJsonContext.Default.ListString),
            "first" => childOutputs.Values.FirstOrDefault() ?? "",
            "last" => childOutputs.Values.LastOrDefault() ?? "",
            _ => string.Join("\n", childOutputs.Values)
        };

        CompleteMetaStepOutput(fanOutStep, mergedOutput, pending, outputs, failureAliases);
        routePlanner.ApplyCompletionRouting(fanOutStep, new MetaExecutionContext(input, outputs), stepById, blocked, pending, dependentsByStep);
        stepResults.Add(new MetaStepExecutionResult(fanOutStep.Id, fanOutStep.Kind, ToolResultStatuses.Completed, null, fanSw.Elapsed.TotalMilliseconds, Continued: false));

        return true;
    }

    // ── Helpers (extracted from the runtimes so the executor is self-contained) ──

    private static string NormalizeMetaStepKind(string kind)
        => kind.Trim().ToLowerInvariant();

    private static void CompleteMetaStepOutput(
        MetaSkillStepDefinition step,
        string output,
        HashSet<string> pending,
        Dictionary<string, string> outputs,
        Dictionary<string, string> failureAliases)
    {
        outputs[step.Id] = output;
        if (failureAliases.TryGetValue(step.Id, out var primaryStepId))
            outputs[primaryStepId] = output;
        pending.Remove(step.Id);
    }

    private static void BlockStepAndDependents(
        string stepId,
        HashSet<string> blocked,
        HashSet<string> pending,
        IReadOnlyDictionary<string, List<string>> dependentsByStep)
    {
        var stack = new Stack<string>();
        stack.Push(stepId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!blocked.Add(current))
                continue;
            pending.Remove(current);
            if (!dependentsByStep.TryGetValue(current, out var dependents))
                continue;
            foreach (var dependent in dependents)
                stack.Push(dependent);
        }
    }

    private static Dictionary<string, object?> DeserializeStepArgs(string? withJson)
    {
        if (string.IsNullOrWhiteSpace(withJson))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize(withJson, CoreJsonContext.Default.DictionaryStringObject);
            if (parsed is not null)
                return new Dictionary<string, object?>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { }
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool? GetOptionalBoolean(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;
        if (value is bool b) return b;
        if (value is string s && bool.TryParse(s, out var ps)) return ps;
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.True) return true;
            if (json.ValueKind == JsonValueKind.False) return false;
            if (json.ValueKind == JsonValueKind.String && bool.TryParse(json.GetString(), out var pjs)) return pjs;
        }
        return null;
    }
}
