using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

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
        var (media, _) = MediaMarkerProtocol.Extract(_fixture.Prompt);
        if (media.Any(m => m.Kind is MediaMarkerKind.FilePath or MediaMarkerKind.ImagePath ||
            !Uri.TryCreate(m.Value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
            throw new InvalidDataException("Replay media must use HTTP(S) references; no local files, inline binary, or unresolved channel IDs.");
        foreach (var response in _fixture.Responses)
        {
            if (response is null || response.Text is null || response.ToolCalls is null)
                throw new InvalidDataException("Invalid replay response.");
            foreach (var call in response.ToolCalls)
            {
                if (call is null || string.IsNullOrWhiteSpace(call.ToolName) || call.Result is null)
                    throw new InvalidDataException("Invalid replay tool call.");
                if (call.ResultStatus is not ("completed" or "failed" or "blocked") ||
                    (call.ResultStatus == "completed" && (call.FailureCode is not null || call.FailureMessage is not null)))
                    throw new InvalidDataException("Invalid replay outcome.");
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
            if (_nextResponse == 0 && !PromptMatches(history.LastOrDefault(m => m.Role == ChatRole.User)))
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
            if (match.Call.ResultStatus != "completed")
                throw new ToolOutcomeException(match.Call.Result, match.Call.ResultStatus, match.Call.FailureCode, match.Call.FailureMessage);
            return match.Call.Result;
        }
    }

    private bool PromptMatches(ChatMessage? message)
    {
        if (message is null) return false;
        var (markers, text) = MediaMarkerProtocol.Extract(_fixture.Prompt);
        if (markers.Count == 0) return message.Text == (string.IsNullOrWhiteSpace(text) ? _fixture.Prompt : text) && message.Contents.All(c => c is TextContent);
        var uris = message.Contents.OfType<UriContent>().ToArray();
        return message.Text == text && uris.Length == markers.Count &&
            message.Contents.All(c => c is TextContent or UriContent) &&
            markers.Select((marker, i) => uris[i].Uri == new Uri(marker.Value) && uris[i].MediaType == (marker.Kind switch
            {
                MediaMarkerKind.ImageUrl => "image/*", MediaMarkerKind.AudioUrl => "audio/*", MediaMarkerKind.VideoUrl => "video/*",
                _ => "application/octet-stream"
            })).All(equal => equal);
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
