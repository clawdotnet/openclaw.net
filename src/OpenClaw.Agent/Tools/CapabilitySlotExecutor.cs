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
    private sealed class BindingGate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }
    private readonly Dictionary<(string Scope, string Key), BindingGate> _bindingGates = new();
    private readonly object _gateLock = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private sealed record Cached(CapabilityTarget Target, ResolveCapabilityBinding Binding, IReadOnlyList<CapabilityCandidate> Candidates);
    private readonly Dictionary<string, (int Failures, DateTimeOffset Until)> _circuits = new();
    private readonly object _circuitLock = new();
    internal int AddedServerCount => cache.Count; // Retained diagnostic; bindings now share the bounded, generation-aware cache.
    public void ClearRuntimeCache() { cache.Clear(); lock (_circuitLock) _circuits.Clear(); }
    public async Task<ToolExecutionResult> ExecuteGovernedAsync(MetaCapabilityRefDefinition reference, string arguments,
        Session session, OpenClaw.Core.Observability.TurnContext turn, OpenClawToolExecutor executor, string callId, CancellationToken ct, Func<string, bool>? isSkillToolAllowed = null)
    {
        ToolExecutionResult? captured = null;
        var stepTool = new GovernedStepTool(async token =>
        {
            captured = await ExecuteAsync(reference, arguments, session.Id, token,
                (tool, args, innerToken) => isSkillToolAllowed?.Invoke(tool.Name) == false
                    ? Task.FromResult(Fail("metadata_capability_denied", "Resolved tool is not permitted by skill metadata capabilities", args, new(), ToolResultStatuses.Blocked))
                    : executor.ExecuteAsync(tool.Name, args, callId + ":target", session, turn,
                        false, null, innerToken, boundCapabilityTool: providers.Get(reference.Provider) is LocalCapabilityProvider ? null : tool),
                securityScope: Scope(session.ChannelId, session.AuthenticatedUserId ?? session.SenderId),
                isToolAllowed: isSkillToolAllowed);
            if (captured.ResultStatus != ToolResultStatuses.Completed)
                throw new ToolOutcomeException(captured.ResultText, captured.ResultStatus, captured.FailureCode, captured.FailureMessage);
            return captured.ResultText;
        });
        var result = await executor.ExecuteAsync(stepTool.Name, arguments, callId + ":resolve", session, turn,
            false, null, ct, boundCapabilityTool: stepTool);
        result.CapabilityInvocation = captured?.Invocation;
        result.BindingTrajectory = captured?.BindingTrajectory;
        result.RetrySafe = captured?.RetrySafe ?? false;
        return result;
    }
    internal sealed class GovernedStepTool(Func<CancellationToken, Task<string>> run) : ITool
    {
        public string Name => "resolve_capability";
        public string Description => "Resolve an authorized capability workflow step";
        public string ParameterSchema => "{\"type\":\"object\"}";
        public async ValueTask<string> ExecuteAsync(string args, CancellationToken ct) => await run(ct);
    }
    private static string Scope(string channel, string user) => $"{channel.Length}:{channel}{user.Length}:{user}";

    internal async Task<ToolExecutionResult> ExecuteAsync(MetaCapabilityRefDefinition capabilityRef, string arguments, string sessionId, CancellationToken ct,
        Func<ITool, string, CancellationToken, Task<ToolExecutionResult>>? invoke = null, string securityScope = "", Func<string, bool>? isToolAllowed = null)
    {
        var sw = Stopwatch.StartNew();
        var provider = providers.Get(capabilityRef.Provider);
        var trajectory = new CapabilityBindingTrajectory { Binding = capabilityRef.Binding, Provider = provider?.Id ?? capabilityRef.Provider, Revision = cache.Generation };
        if (provider is null) return Fail(CapabilitySlotFailureCodes.ProviderUnavailable, "Capability provider is not configured", arguments, trajectory);
        var generation = cache.Generation;
        var intent = capabilityRef.Intent;
        if (capabilityRef.Binding is not ("static" or "dynamic") ||
            (capabilityRef.Binding == "static" && (capabilityRef.Static is null || intent is not null)) ||
            (capabilityRef.Binding == "dynamic" && (intent is null || capabilityRef.Static is not null)))
            return Fail("invalid_capability_ref", "Capability binding mode and payload must agree", arguments, trajectory);
        var intentKey = intent is null
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Scope(capabilityRef.Static!.Target, capabilityRef.Static.ToolName))))
            : CapabilityBindingCache.ComputeIntentKey(intent.TaskDescription, string.Join(",", intent.Keywords), capabilityRef.SelectionPolicy);
        if (intent?.Type is { } type)
            intentKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Scope(intentKey, type))));
        var scope = $"{securityScope.Length}:{securityScope}" + (capabilityRef.Binding == "static" ? ":static" : $"{sessionId.Length}:{sessionId}");
        var key = $"{provider.Id}:{generation}:{capabilityRef.Binding}:{intentKey}";
        trajectory.CapabilityType = intent?.Type;
        trajectory.TaskDescription = intent?.TaskDescription;
        trajectory.KeyWords = intent is null ? null : string.Join(",", intent.Keywords);
        trajectory.SelectionPolicy = capabilityRef.SelectionPolicy;
        trajectory.IntentKey = intentKey;
        Cached? found;
        var gateKey = (scope, key);
        BindingGate bindingGate;
        lock (_gateLock)
        {
            if (!_bindingGates.TryGetValue(gateKey, out bindingGate!))
                _bindingGates[gateKey] = bindingGate = new();
            bindingGate.Users++;
        }
        var entered = false;
        try
        {
            await bindingGate.Semaphore.WaitAsync(ct);
            entered = true;
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
                        if (intent is null)
                            return Fail("invalid_capability_ref", "Dynamic binding requires an intent", arguments, trajectory);
                        var result = await providers.ResolveAsync(new(intent.TaskDescription, string.Join(",", intent.Keywords),
                            capabilityRef.SelectionPolicy == "exact_name" ? ResolveCapabilitySelectionPolicy.ExactName : ResolveCapabilitySelectionPolicy.First)
                        { Provider = provider.Id, CapabilityType = intent.Type }, ct, isToolAllowed);
                        trajectory.Candidates = result.Candidates.Select(c => new CapabilityBindingCandidate { Name = c.Name, Rank = c.Rank }).ToList();
                        trajectory.Attempted = (result.Failure?.TriedCandidates ?? []).Select(c => new CapabilityBindingCandidate { Name = c.Name, Rank = c.Rank }).ToList();
                        if (result.Target is null)
                        {
                            if (result.Failure?.FailureCode == ResolveCapabilityFailureCodes.ToolPolicyDenied)
                                return Fail("metadata_capability_denied", "Resolved tools are not permitted by skill metadata capabilities", arguments, trajectory, ToolResultStatuses.Blocked);
                            return Fail(result.Failure?.FailureCode == ResolveCapabilityFailureCodes.ProviderUnavailable ? CapabilitySlotFailureCodes.ProviderUnavailable : CapabilitySlotFailureCodes.ResolveFailed,
                                result.Failure?.FailureCode ?? "Resolution failed", arguments, trajectory);
                        }
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
        finally
        {
            if (entered) bindingGate.Semaphore.Release();
            lock (_gateLock)
            {
                if (--bindingGate.Users == 0)
                {
                    _bindingGates.Remove(gateKey);
                    bindingGate.Semaphore.Dispose();
                }
            }
        }
        if (found is null || generation != cache.Generation) return Fail("capability_stale_binding", "Provider configuration changed during resolution; resolve again", arguments, trajectory);
        trajectory.Server = found.Target.Server;
        trajectory.Tool = found.Target.ToolId;
        trajectory.Schema = found.Target.Tool.ParameterSchema;
        trajectory.SchemaFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(found.Target.Tool.ParameterSchema)));
        trajectory.Candidates = found.Candidates.Select(c => new CapabilityBindingCandidate { Name = c.Name, Rank = c.Rank }).ToList();
        trajectory.Attempted = found.Binding.TriedCandidates.Select(c => new CapabilityBindingCandidate { Name = c.Name, Rank = c.Rank }).ToList();
        trajectory.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        var circuitKey = $"{scope}:{provider.Id}:{generation}:{found.Target.Server}:{found.Target.Tool.Name}";
        lock (_circuitLock)
            if (_circuits.TryGetValue(circuitKey, out var circuit) && circuit.Failures >= 3 && circuit.Until > _clock.GetUtcNow())
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
            lock (_circuitLock)
            {
                var now = _clock.GetUtcNow();
                foreach (var expired in _circuits.Where(c => c.Value.Until <= now).Select(c => c.Key).ToArray())
                    _circuits.Remove(expired);
                if (_circuits.TryGetValue(circuitKey, out var old))
                    _circuits[circuitKey] = (old.Failures + 1, now.AddSeconds(30));
                else
                {
                    if (_circuits.Count >= 1024)
                    {
                        var closed = _circuits.Where(c => c.Value.Failures < 3).MinBy(c => c.Value.Until);
                        if (closed.Key is not null) _circuits.Remove(closed.Key);
                    }
                    // Never evict an active cooldown to admit a new circuit.
                    if (_circuits.Count < 1024) _circuits[circuitKey] = (1, now.AddSeconds(30));
                }
            }
        }
        else if (resultText.ResultStatus == ToolResultStatuses.Completed) { lock (_circuitLock) _circuits.Remove(circuitKey); }
        resultText.BindingTrajectory = trajectory;
        resultText.RetrySafe = found.Target.RetrySafe && resultText.ResultStatus == ToolResultStatuses.Failed;
        return resultText;
    }
    private static ToolExecutionResult Result(string text, string args) => new()
    { Invocation = new() { ToolName = "capability", Arguments = args, Result = text, ResultStatus = ToolResultStatuses.Completed }, ResultText = text, ResultStatus = ToolResultStatuses.Completed };
    private static ToolExecutionResult Fail(string code, string message, string args, CapabilityBindingTrajectory trace, string status = ToolResultStatuses.Failed) => new()
    {
        Invocation = new() { ToolName = "capability", Arguments = args, Result = message, ResultStatus = status, FailureCode = code, FailureMessage = message },
        ResultText = message,
        ResultStatus = status,
        FailureCode = code,
        FailureMessage = message,
        BindingTrajectory = trace
    };
}
