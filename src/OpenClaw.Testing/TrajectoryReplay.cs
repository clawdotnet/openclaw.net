using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Testing;

/// <summary>One-use offline provider and tools. No calls are forwarded to real implementations.</summary>
public sealed class TrajectoryReplay : IChatClient
{
    private readonly TrajectoryReplayFixture _fixture;
    private readonly object _gate = new();
    private readonly List<PendingCall> _pending = [];
    private int _nextResponse;
    private bool _diverged;
    public IReadOnlyList<ITool> Tools { get; }

    public TrajectoryReplay(TrajectoryReplayFixture fixture)
    {
        // Keep a private snapshot so edits to a fixture cannot change a running replay.
        _fixture = JsonSerializer.Deserialize(JsonSerializer.Serialize(fixture, ScenarioJsonContext.Default.TrajectoryReplayFixture),
            ScenarioJsonContext.Default.TrajectoryReplayFixture) ?? throw new InvalidDataException("Missing replay fixture.");
        if (_fixture.SchemaVersion != 1 || _fixture.Prompt is null || _fixture.Responses is not { Count: > 0 })
            throw new InvalidDataException("Invalid replay fixture schema or input.");
        foreach (var response in _fixture.Responses)
        {
            if (response is null || response.Text is null || response.ToolCalls is null)
                throw new InvalidDataException("Invalid replay response.");
            foreach (var call in response.ToolCalls)
            {
                if (call is null || string.IsNullOrWhiteSpace(call.ToolName) || call.Result is null)
                    throw new InvalidDataException("Invalid replay tool call.");
                using var args = JsonDocument.Parse(call.ArgumentsJson);
                if (args.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Replay arguments must be JSON objects.");
                if (response.ToolCalls.Count(other => other.ToolName == call.ToolName && ArgumentsEqual(other.ArgumentsJson, call.ArgumentsJson)) > 1)
                    throw new InvalidDataException("Identical calls within a batch are ambiguous; split the fixture into ordered batches.");
            }
        }
        if (_fixture.Responses[^1].ToolCalls.Count != 0 || _fixture.Responses.Take(_fixture.Responses.Count - 1).Any(r => r.ToolCalls.Count == 0))
            throw new InvalidDataException("A replay needs tool batches followed by one final response.");
        Tools = _fixture.Responses.SelectMany(r => r.ToolCalls).Select(c => c.ToolName).Distinct(StringComparer.Ordinal)
            .Select(name => (ITool)new ReplayTool(this, name)).ToArray();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var history = messages.ToList();
            if (_nextResponse == 0 && history.LastOrDefault(m => m.Role == ChatRole.User)?.Text != _fixture.Prompt)
                throw Divergence("Replay prompt differs from the recorded prompt.");
            if (_pending.Any(call => !call.Consumed)) throw Divergence("Runtime skipped a recorded tool call.");
            var results = history.Where(m => m.Role == ChatRole.Tool).SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToArray();
            foreach (var call in _pending)
                if (results.Count(result => result.CallId == call.Id) != 1 || !results.Any(result => result.CallId == call.Id && result.Result?.ToString() == call.Call.Result))
                    throw Divergence("Runtime did not return the recorded tool result to the provider.");
            if (_nextResponse >= _fixture.Responses.Count) throw Divergence("Runtime requested an unrecorded provider response.");
            var response = _fixture.Responses[_nextResponse++];
            _pending.Clear();
            var content = new List<AIContent>();
            if (response.Text.Length > 0) content.Add(new TextContent(response.Text));
            for (var i = 0; i < response.ToolCalls.Count; i++)
            {
                var call = response.ToolCalls[i];
                var id = $"replay_{_nextResponse}_{i}";
                _pending.Add(new PendingCall(id, call));
                using var args = JsonDocument.Parse(call.ArgumentsJson);
                var values = args.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
                content.Add(new FunctionCallContent(id, call.ToolName, values));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, content)));
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
            yield return new ChatResponseUpdate { Role = message.Role, Contents = message.Contents };
    }

    public void VerifyComplete()
    {
        lock (_gate)
            if (_diverged || _nextResponse != _fixture.Responses.Count || _pending.Any(call => !call.Consumed))
                throw new InvalidOperationException("Replay did not consume the complete fixture without divergence.");
    }

    private string Execute(string tool, string arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var match = _pending.FirstOrDefault(call => !call.Consumed && call.Call.ToolName == tool && ArgumentsEqual(call.Call.ArgumentsJson, arguments));
            if (match is null) throw Divergence("Tool name, arguments, or execution count differs from the fixture.");
            match.Consumed = true;
            return match.Call.Result;
        }
    }

    private InvalidOperationException Divergence(string message) { _diverged = true; return new(message); }

    private static bool ArgumentsEqual(string expected, string actual)
    {
        try
        {
            using var left = JsonDocument.Parse(expected);
            using var right = JsonDocument.Parse(actual);
            return JsonElement.DeepEquals(left.RootElement, right.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private sealed class PendingCall(string id, ReplayToolCall call)
    {
        public string Id { get; } = id;
        public ReplayToolCall Call { get; } = call;
        public bool Consumed { get; set; }
    }

    private sealed class ReplayTool(TrajectoryReplay owner, string name) : ITool
    {
        public string Name => name;
        public string Description => "Offline replay stub; returns a recorded result without performing external actions.";
        public string ParameterSchema => """{"type":"object","additionalProperties":true}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct) => ValueTask.FromResult(owner.Execute(Name, argumentsJson, ct));
    }
}
