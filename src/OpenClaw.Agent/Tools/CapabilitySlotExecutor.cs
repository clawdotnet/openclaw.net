using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Skills.Meta;
namespace OpenClaw.Agent.Tools;

public sealed class CapabilitySlotExecutor(CapabilityProviderRegistry providers, CapabilityBindingCache cache, TimeProvider? clock = null)
{
    private readonly SemaphoreSlim _bindingGate = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private sealed record Cached(CapabilityTarget Target, ResolveCapabilityBinding Binding, IReadOnlyList<CapabilityCandidate> Candidates);
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Until)> _circuits = new();
    internal int AddedServerCount => cache.Count; // Retained diagnostic; bindings now share the bounded, generation-aware cache.
    public void ClearRuntimeCache() { cache.Clear(); _circuits.Clear(); }
    public async Task<ToolExecutionResult> ExecuteGovernedAsync(MetaCapabilityRefDefinition reference, string arguments,
        Session session, OpenClaw.Core.Observability.TurnContext turn, OpenClawToolExecutor executor, string callId, CancellationToken ct)
    {
        ToolExecutionResult? captured = null;
        var stepTool = new GovernedStepTool(async token =>
        {
            captured = await ExecuteAsync(reference, arguments, session.Id, token,
                (tool, args, innerToken) => executor.ExecuteAsync(tool.Name, args, callId + ":target", session, turn,
                    false, null, innerToken, boundCapabilityTool: tool),
                securityScope: Scope(session.ChannelId, session.AuthenticatedUserId ?? session.SenderId));
            if (captured.ResultStatus != ToolResultStatuses.Completed)
                throw new ToolOutcomeException(captured.ResultText, captured.ResultStatus, captured.FailureCode, captured.FailureMessage);
            return captured.ResultText;
        });
        var result = await executor.ExecuteAsync(stepTool.Name, arguments, callId + ":resolve", session, turn,
            false, null, ct, boundCapabilityTool: stepTool);
        result.BindingTrajectory = captured?.BindingTrajectory;
        result.RetrySafe = captured?.RetrySafe ?? false;
        return result;
    }
    private sealed class GovernedStepTool(Func<CancellationToken, Task<string>> run) : ITool
    {
        public string Name => "resolve_capability";
        public string Description => "Resolve an authorized capability workflow step";
        public string ParameterSchema => "{\"type\":\"object\"}";
        public async ValueTask<string> ExecuteAsync(string args, CancellationToken ct) => await run(ct);
    }
    private static string Scope(string channel, string user) => $"{channel.Length}:{channel}{user.Length}:{user}";

    internal async Task<ToolExecutionResult> ExecuteAsync(MetaCapabilityRefDefinition capabilityRef, string arguments, string sessionId, CancellationToken ct,
        Func<ITool, string, CancellationToken, Task<ToolExecutionResult>>? invoke = null, string securityScope = "")
    {
        var sw = Stopwatch.StartNew();
        var provider = providers.Get(capabilityRef.Provider);
        var trajectory = new CapabilityBindingTrajectory { Binding = capabilityRef.Binding, Provider = provider?.Id ?? capabilityRef.Provider, Revision = cache.Generation };
        if (provider is null) return Fail(CapabilitySlotFailureCodes.ProviderUnavailable, "Capability provider is not configured", arguments, trajectory);
        var generation = cache.Generation;
        var intent = capabilityRef.Intent;
        var intentKey = intent is null
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Scope(capabilityRef.Static!.Target, capabilityRef.Static.ToolName))))
            : CapabilityBindingCache.ComputeIntentKey(intent.TaskDescription, string.Join(",", intent.Keywords), capabilityRef.SelectionPolicy);
        if (intent?.Type is { } type)
            intentKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Scope(intentKey, type))));
        var scope = $"{securityScope.Length}:{securityScope}" + (capabilityRef.Binding == "static" ? ":static" : $"{sessionId.Length}:{sessionId}");
        var key = $"{provider.Id}:{generation}:{capabilityRef.Binding}:{intentKey}";
        trajectory.TaskDescription = intent?.TaskDescription;
        trajectory.KeyWords = intent is null ? null : string.Join(",", intent.Keywords);
        trajectory.SelectionPolicy = capabilityRef.SelectionPolicy;
        trajectory.IntentKey = intentKey;
        Cached? found;
        await _bindingGate.WaitAsync(ct);
        try
        {
            if ((capabilityRef.Binding == "static" || !string.IsNullOrEmpty(sessionId)) && cache.TryGetValue<Cached>(scope, key, out found)) trajectory.CacheHit = true;
            else
            {
                try
                {
                    if (capabilityRef.Binding == "static")
                    {
                        var pinned = capabilityRef.Static!;
                        var target = await provider.BindAsync(pinned.Target, pinned.ToolName, ct);
                        if (target is null) return Fail(CapabilitySlotFailureCodes.BindingFailed, "Pinned capability is unavailable", arguments, trajectory);
                        found = new(target, new(target.Server, target.ToolId, target.Tool.ParameterSchema, []) { Provider = provider.Id }, []);
                    }
                    else
                    {
                        var result = await providers.ResolveAsync(new(intent!.TaskDescription, string.Join(",", intent.Keywords),
                            capabilityRef.SelectionPolicy == "exact_name" ? ResolveCapabilitySelectionPolicy.ExactName : ResolveCapabilitySelectionPolicy.First)
                        { Provider = provider.Id, CapabilityType = intent.Type }, ct);
                        trajectory.Candidates = result.Candidates.Select(c => new CapabilityBindingCandidate { Name = c.Name, Rank = c.Rank }).ToList();
                        trajectory.Attempted = (result.Failure?.TriedCandidates ?? []).Select(c => new CapabilityBindingCandidate { Name = c.Name, Rank = c.Rank }).ToList();
                        if (result.Target is null) return Fail(result.Failure?.FailureCode == ResolveCapabilityFailureCodes.ProviderUnavailable ? CapabilitySlotFailureCodes.ProviderUnavailable : CapabilitySlotFailureCodes.ResolveFailed,
                            result.Failure?.FailureCode ?? "Resolution failed", arguments, trajectory);
                        found = new(result.Target, result.Binding!, result.Candidates);
                    }
                    if (capabilityRef.Binding == "static" || !string.IsNullOrEmpty(sessionId)) cache.Store(scope, key, found, generation, persistent: capabilityRef.Binding == "static");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (ToolOutcomeException ex) { return Fail(ex.FailureCode ?? CapabilitySlotFailureCodes.BindingFailed, ex.Message, arguments, trajectory); }
                catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException)
                { return Fail(CapabilitySlotFailureCodes.ProviderUnavailable, "Capability provider transport unavailable", arguments, trajectory); }
            }
        }
        finally { _bindingGate.Release(); }
        if (found is null || generation != cache.Generation) return Fail("capability_stale_binding", "Provider configuration changed during resolution; resolve again", arguments, trajectory);
        trajectory.Server = found.Target.Server;
        trajectory.Tool = found.Target.ToolId;
        trajectory.Schema = found.Target.Tool.ParameterSchema;
        trajectory.SchemaFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(found.Target.Tool.ParameterSchema)));
        trajectory.Candidates = found.Candidates.Select(c => new CapabilityBindingCandidate { Name = c.Name, Rank = c.Rank }).ToList();
        trajectory.Attempted = found.Binding.TriedCandidates.Select(c => new CapabilityBindingCandidate { Name = c.Name, Rank = c.Rank }).ToList();
        trajectory.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        var circuitKey = $"{scope}:{provider.Id}:{generation}:{found.Target.Server}:{found.Target.Tool.Name}";
        if (_circuits.TryGetValue(circuitKey, out var circuit) && circuit.Until > _clock.GetUtcNow())
            return Fail("capability_circuit_open", "Capability circuit is open; wait before trying again", arguments, trajectory);
        ToolExecutionResult resultText;
        if (invoke is null)
        {
            // Direct callers retain a test/embedding path; production runtimes always pass their policy executor.
            try { resultText = Result(await found.Target.Tool.ExecuteAsync(arguments, ct), arguments); }
            catch (ToolOutcomeException ex) { resultText = Fail(ex.FailureCode ?? CapabilitySlotFailureCodes.ExecutionFailed, ex.Message, arguments, trajectory); }
        }
        else resultText = await invoke(found.Target.Tool, arguments, ct);
        if (resultText.ResultStatus == ToolResultStatuses.Failed)
        {
            if (_circuits.Count > 1024) _circuits.Clear();
            _circuits.AddOrUpdate(circuitKey, (1, DateTimeOffset.MinValue), (_, old) => (old.Failures + 1, old.Failures + 1 >= 3 ? _clock.GetUtcNow().AddSeconds(30) : DateTimeOffset.MinValue));
        }
        else if (resultText.ResultStatus == ToolResultStatuses.Completed) _circuits.TryRemove(circuitKey, out _);
        resultText.BindingTrajectory = trajectory;
        resultText.RetrySafe = found.Target.RetrySafe && resultText.ResultStatus == ToolResultStatuses.Failed;
        return resultText;
    }
    private static ToolExecutionResult Result(string text, string args) => new()
    { Invocation = new() { ToolName = "capability", Arguments = args, Result = text, ResultStatus = ToolResultStatuses.Completed }, ResultText = text, ResultStatus = ToolResultStatuses.Completed };
    private static ToolExecutionResult Fail(string code, string message, string args, CapabilityBindingTrajectory trace) => new()
    {
        Invocation = new() { ToolName = "capability", Arguments = args, Result = message, ResultStatus = ToolResultStatuses.Failed, FailureCode = code, FailureMessage = message },
        ResultText = message,
        ResultStatus = ToolResultStatuses.Failed,
        FailureCode = code,
        FailureMessage = message,
        BindingTrajectory = trace
    };
}
