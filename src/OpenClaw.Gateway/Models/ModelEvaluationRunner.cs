using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Models;

internal sealed class ModelEvaluationRunner
{
    private readonly ConfiguredModelProfileRegistry _registry;
    private readonly GatewayConfig _config;
    private readonly ILogger<ModelEvaluationRunner> _logger;
    private readonly IReadOnlyList<IModelEvaluationScenario> _scenarios;

    public ModelEvaluationRunner(
        ConfiguredModelProfileRegistry registry,
        GatewayConfig config,
        ILogger<ModelEvaluationRunner> logger)
    {
        _registry = registry;
        _config = config;
        _logger = logger;
        _scenarios =
        [
            new PlainChatScenario(),
            new JsonExtractionScenario(),
            new ToolInvocationScenario(),
            new MultiTurnContinuityScenario(),
            new CompactionRecoveryScenario(),
            new StreamingScenario(),
            new VisionPromptScenario()
        ];
    }

    public IReadOnlyList<string> ListScenarioIds()
        => _scenarios.Select(static scenario => scenario.Id).ToArray();

    public ModelSelectionDoctorResponse BuildDoctor()
    {
        var statuses = _registry.ListStatuses();
        var warnings = new List<string>();
        var errors = new List<string>();
        if (statuses.Count == 0)
            errors.Add("No model profiles are registered.");
        if (string.IsNullOrWhiteSpace(_registry.DefaultProfileId))
            errors.Add("No default model profile is configured.");

        foreach (var status in statuses)
        {
            if (status.ValidationIssues.Length > 0)
                warnings.Add($"Profile '{status.Id}' has validation issues: {string.Join("; ", status.ValidationIssues)}");
        }

        return new ModelSelectionDoctorResponse
        {
            DefaultProfileId = _registry.DefaultProfileId,
            Errors = errors,
            Warnings = warnings,
            Profiles = statuses
        };
    }

    public async Task<ModelEvaluationReport> RunAsync(ModelEvaluationRequest request, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var runId = $"meval_{Guid.NewGuid():N}"[..18];
        var scenarioSet = ResolveScenarios(request.ScenarioIds);
        var profileIds = ResolveProfiles(request);
        var profileReports = new List<ModelEvaluationProfileReport>(profileIds.Length);

        foreach (var profileId in profileIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!_registry.TryGetRegistration(profileId, out var registration) || registration is null)
            {
                profileReports.Add(new ModelEvaluationProfileReport
                {
                    ProfileId = profileId,
                    ProviderId = "unknown",
                    ModelId = "unknown",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Scenarios =
                    [
                        new ModelEvaluationScenarioResult
                        {
                            ScenarioId = "profile_lookup",
                            Name = "Profile lookup",
                            Status = "failed",
                            Error = $"Profile '{profileId}' is not registered."
                        }
                    ]
                });
                continue;
            }

            if (registration.Client is null)
            {
                profileReports.Add(new ModelEvaluationProfileReport
                {
                    ProfileId = registration.Profile.Id,
                    ProviderId = registration.Profile.ProviderId,
                    ModelId = registration.Profile.ModelId,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Scenarios =
                    [
                        new ModelEvaluationScenarioResult
                        {
                            ScenarioId = "profile_availability",
                            Name = "Profile availability",
                            Status = "failed",
                            Error = string.Join("; ", registration.ValidationIssues)
                        }
                    ]
                });
                continue;
            }

            var profileStart = DateTimeOffset.UtcNow;
            var scenarioResults = new List<ModelEvaluationScenarioResult>(scenarioSet.Count);
            foreach (var scenario in scenarioSet)
            {
                ct.ThrowIfCancellationRequested();
                scenarioResults.Add(await scenario.RunAsync(registration.Client, registration.Profile, ct));
            }

            profileReports.Add(new ModelEvaluationProfileReport
            {
                ProfileId = registration.Profile.Id,
                ProviderId = registration.Profile.ProviderId,
                ModelId = registration.Profile.ModelId,
                StartedAtUtc = profileStart,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Scenarios = scenarioResults
            });
        }

        var completedAt = DateTimeOffset.UtcNow;
        var evaluationDirectory = Path.Combine(Path.GetFullPath(_config.Memory.StoragePath), "admin", "model-evaluations");
        Directory.CreateDirectory(evaluationDirectory);
        var jsonPath = Path.Combine(evaluationDirectory, $"{runId}.json");
        var markdownPath = Path.Combine(evaluationDirectory, $"{runId}.md");

        var report = new ModelEvaluationReport
        {
            RunId = runId,
            StartedAtUtc = startedAt,
            CompletedAtUtc = completedAt,
            ScenarioIds = scenarioSet.Select(static scenario => scenario.Id).ToArray(),
            Profiles = profileReports,
            JsonPath = jsonPath,
            MarkdownPath = request.IncludeMarkdown ? markdownPath : null
        };

        var reportWithMarkdown = new ModelEvaluationReport
        {
            RunId = report.RunId,
            StartedAtUtc = report.StartedAtUtc,
            CompletedAtUtc = report.CompletedAtUtc,
            ScenarioIds = report.ScenarioIds,
            Profiles = report.Profiles,
            JsonPath = report.JsonPath,
            MarkdownPath = report.MarkdownPath,
            Markdown = request.IncludeMarkdown ? BuildMarkdown(report) : null
        };

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(reportWithMarkdown, CoreJsonContext.Default.ModelEvaluationReport), ct);
        if (request.IncludeMarkdown && reportWithMarkdown.Markdown is not null)
            await File.WriteAllTextAsync(markdownPath, reportWithMarkdown.Markdown, ct);

        return reportWithMarkdown;
    }

    private IReadOnlyList<IModelEvaluationScenario> ResolveScenarios(IReadOnlyList<string> scenarioIds)
    {
        if (scenarioIds.Count == 0)
            return _scenarios;

        var requested = new HashSet<string>(scenarioIds, StringComparer.OrdinalIgnoreCase);
        return _scenarios.Where(scenario => requested.Contains(scenario.Id)).ToArray();
    }

    private string[] ResolveProfiles(ModelEvaluationRequest request)
    {
        var explicitProfiles = request.ProfileIds
            .Concat(string.IsNullOrWhiteSpace(request.ProfileId) ? [] : [request.ProfileId])
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (explicitProfiles.Length > 0)
            return explicitProfiles;

        if (!string.IsNullOrWhiteSpace(_registry.DefaultProfileId))
            return [_registry.DefaultProfileId];

        return _registry.ListStatuses().Select(static status => status.Id).ToArray();
    }

    private string BuildMarkdown(ModelEvaluationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Model Evaluation Report");
        sb.AppendLine();
        sb.AppendLine($"- Run ID: `{report.RunId}`");
        sb.AppendLine($"- Started: `{report.StartedAtUtc:O}`");
        sb.AppendLine($"- Completed: `{report.CompletedAtUtc:O}`");
        sb.AppendLine($"- Scenarios: {string.Join(", ", report.ScenarioIds)}");
        sb.AppendLine();

        foreach (var profile in report.Profiles)
        {
            sb.AppendLine($"## {profile.ProfileId}");
            sb.AppendLine();
            sb.AppendLine($"- Provider/model: `{profile.ProviderId}/{profile.ModelId}`");
            sb.AppendLine($"- Started: `{profile.StartedAtUtc:O}`");
            sb.AppendLine($"- Completed: `{profile.CompletedAtUtc:O}`");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Status | Latency (ms) | Summary |");
            sb.AppendLine("| --- | --- | ---: | --- |");
            foreach (var scenario in profile.Scenarios)
                sb.AppendLine($"| {scenario.Name} | {scenario.Status} | {scenario.LatencyMs} | {EscapePipe(scenario.Summary ?? scenario.Error ?? "")} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapePipe(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal);

    private interface IModelEvaluationScenario
    {
        string Id { get; }
        string Name { get; }
        Task<ModelEvaluationScenarioResult> RunAsync(IChatClient client, ModelProfile profile, CancellationToken ct);
    }

    private abstract class ModelEvaluationScenarioBase : IModelEvaluationScenario
    {
        public abstract string Id { get; }
        public abstract string Name { get; }

        public async Task<ModelEvaluationScenarioResult> RunAsync(IChatClient client, ModelProfile profile, CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            try
            {
                return await ExecuteAsync(client, profile, started, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ModelEvaluationScenarioResult
                {
                    ScenarioId = Id,
                    Name = Name,
                    Status = "failed",
                    LatencyMs = started.ElapsedMilliseconds,
                    Error = ex.Message
                };
            }
        }

        protected static ModelEvaluationScenarioResult Unsupported(string id, string name, Stopwatch stopwatch, string summary)
            => new()
            {
                ScenarioId = id,
                Name = name,
                Status = "unsupported",
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Summary = summary
            };

        protected abstract Task<ModelEvaluationScenarioResult> ExecuteAsync(IChatClient client, ModelProfile profile, Stopwatch stopwatch, CancellationToken ct);
    }

    private sealed class PlainChatScenario : ModelEvaluationScenarioBase
    {
        public override string Id => "plain-chat";
        public override string Name => "Plain chat response";

        protected override async Task<ModelEvaluationScenarioResult> ExecuteAsync(IChatClient client, ModelProfile profile, Stopwatch stopwatch, CancellationToken ct)
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with exactly READY.")],
                new ChatOptions { ModelId = profile.ModelId, MaxOutputTokens = 64, Temperature = 0 },
                ct);
            return new ModelEvaluationScenarioResult
            {
                ScenarioId = Id,
                Name = Name,
                Status = response.Text?.Contains("READY", StringComparison.OrdinalIgnoreCase) == true ? "passed" : "warning",
                LatencyMs = stopwatch.ElapsedMilliseconds,
                InputTokens = response.Usage?.InputTokenCount ?? 0,
                OutputTokens = response.Usage?.OutputTokenCount ?? 0,
                Summary = response.Text
            };
        }
    }

    private sealed class JsonExtractionScenario : ModelEvaluationScenarioBase
    {
        public override string Id => "json-extraction";
        public override string Name => "Structured JSON extraction";

        protected override async Task<ModelEvaluationScenarioResult> ExecuteAsync(IChatClient client, ModelProfile profile, Stopwatch stopwatch, CancellationToken ct)
        {
            if (!profile.Capabilities.SupportsJsonSchema && !profile.Capabilities.SupportsStructuredOutputs)
                return Unsupported(Id, Name, stopwatch, $"Profile '{profile.Id}' does not advertise JSON schema or structured outputs.");

            using var schema = JsonDocument.Parse("""{"type":"object","properties":{"animal":{"type":"string"},"count":{"type":"integer"}},"required":["animal","count"],"additionalProperties":false}""");
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Extract JSON from this sentence: I saw 3 foxes in the garden.")],
                new ChatOptions
                {
                    ModelId = profile.ModelId,
                    MaxOutputTokens = 128,
                    Temperature = 0,
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), "extraction")
                },
                ct);

            var malformed = false;
            try
            {
                using var doc = JsonDocument.Parse(response.Text ?? "{}");
                malformed = !(doc.RootElement.TryGetProperty("animal", out _) && doc.RootElement.TryGetProperty("count", out _));
            }
            catch
            {
                malformed = true;
            }

            return new ModelEvaluationScenarioResult
            {
                ScenarioId = Id,
                Name = Name,
                Status = malformed ? "failed" : "passed",
                LatencyMs = stopwatch.ElapsedMilliseconds,
                InputTokens = response.Usage?.InputTokenCount ?? 0,
                OutputTokens = response.Usage?.OutputTokenCount ?? 0,
                MalformedJson = malformed,
                Summary = response.Text
            };
        }
    }

    private sealed class ToolInvocationScenario : ModelEvaluationScenarioBase
    {
        public override string Id => "tool-invocation";
        public override string Name => "Tool selection correctness";

        protected override async Task<ModelEvaluationScenarioResult> ExecuteAsync(IChatClient client, ModelProfile profile, Stopwatch stopwatch, CancellationToken ct)
        {
            if (!profile.Capabilities.SupportsTools)
                return Unsupported(Id, Name, stopwatch, $"Profile '{profile.Id}' does not advertise tool support.");

            using var schema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""");
            var tool = AIFunctionFactory.CreateDeclaration("record_observation", "Record the extracted observation.", schema.RootElement.Clone(), returnJsonSchema: null);
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Use the tool to record the value 'gemma4'.")],
                new ChatOptions
                {
                    ModelId = profile.ModelId,
                    Temperature = 0,
                    MaxOutputTokens = 128,
                    Tools = [tool]
                },
                ct);

            var call = response.Messages
                .SelectMany(static message => message.Contents)
                .OfType<FunctionCallContent>()
                .FirstOrDefault();

            return new ModelEvaluationScenarioResult
            {
                ScenarioId = Id,
                Name = Name,
                Status = call is null ? "failed" : "passed",
                LatencyMs = stopwatch.ElapsedMilliseconds,
                InputTokens = response.Usage?.InputTokenCount ?? 0,
                OutputTokens = response.Usage?.OutputTokenCount ?? 0,
                ToolCalls = call is null ? 0 : 1,
                Summary = call is null ? response.Text : $"tool={call.Name}"
            };
        }
    }

    private sealed class MultiTurnContinuityScenario : ModelEvaluationScenarioBase
    {
        public override string Id => "multi-turn";
        public override string Name => "Multi-turn continuity";

        protected override async Task<ModelEvaluationScenarioResult> ExecuteAsync(IChatClient client, ModelProfile profile, Stopwatch stopwatch, CancellationToken ct)
        {
            var first = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Remember the code word maple-42 and reply with STORED.")],
                new ChatOptions { ModelId = profile.ModelId, Temperature = 0, MaxOutputTokens = 64 },
                ct);
            var second = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.User, "Remember the code word maple-42 and reply with STORED."),
                    new ChatMessage(ChatRole.Assistant, first.Text ?? "STORED"),
                    new ChatMessage(ChatRole.User, "What code word did I ask you to remember?")
                ],
                new ChatOptions { ModelId = profile.ModelId, Temperature = 0, MaxOutputTokens = 64 },
                ct);

            return new ModelEvaluationScenarioResult
            {
                ScenarioId = Id,
                Name = Name,
                Status = second.Text?.Contains("maple-42", StringComparison.OrdinalIgnoreCase) == true ? "passed" : "warning",
                LatencyMs = stopwatch.ElapsedMilliseconds,
                InputTokens = (first.Usage?.InputTokenCount ?? 0) + (second.Usage?.InputTokenCount ?? 0),
                OutputTokens = (first.Usage?.OutputTokenCount ?? 0) + (second.Usage?.OutputTokenCount ?? 0),
                Summary = second.Text
            };
        }
    }

    private sealed class CompactionRecoveryScenario : ModelEvaluationScenarioBase
    {
        public override string Id => "compaction-recovery";
        public override string Name => "Compaction recovery";

        protected override async Task<ModelEvaluationScenarioResult> ExecuteAsync(IChatClient client, ModelProfile profile, Stopwatch stopwatch, CancellationToken ct)
        {
            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, "Conversation summary: The user named the migration branch gemma4-rollout."),
                    new ChatMessage(ChatRole.User, "What branch name was mentioned in the summary?")
                ],
                new ChatOptions { ModelId = profile.ModelId, Temperature = 0, MaxOutputTokens = 64 },
                ct);

            return new ModelEvaluationScenarioResult
            {
                ScenarioId = Id,
                Name = Name,
                Status = response.Text?.Contains("gemma4-rollout", StringComparison.OrdinalIgnoreCase) == true ? "passed" : "warning",
                LatencyMs = stopwatch.ElapsedMilliseconds,
                InputTokens = response.Usage?.InputTokenCount ?? 0,
                OutputTokens = response.Usage?.OutputTokenCount ?? 0,
                Summary = response.Text
            };
        }
    }

    private sealed class StreamingScenario : ModelEvaluationScenarioBase
    {
        public override string Id => "streaming";
        public override string Name => "Streaming behavior";

        protected override async Task<ModelEvaluationScenarioResult> ExecuteAsync(IChatClient client, ModelProfile profile, Stopwatch stopwatch, CancellationToken ct)
        {
            if (!profile.Capabilities.SupportsStreaming)
                return Unsupported(Id, Name, stopwatch, $"Profile '{profile.Id}' does not advertise streaming support.");

            var deltas = new List<string>();
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply in one short sentence about Gemma.")],
                new ChatOptions { ModelId = profile.ModelId, Temperature = 0, MaxOutputTokens = 96 },
                ct))
            {
                if (!string.IsNullOrWhiteSpace(update.Text))
                    deltas.Add(update.Text);
            }

            return new ModelEvaluationScenarioResult
            {
                ScenarioId = Id,
                Name = Name,
                Status = deltas.Count > 0 ? "passed" : "warning",
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Summary = string.Concat(deltas)
            };
        }
    }

    private sealed class VisionPromptScenario : ModelEvaluationScenarioBase
    {
        private const string TinyRedPngDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR42mP8z8BQDwAFgwJ/l38DSQAAAABJRU5ErkJggg==";

        public override string Id => "vision";
        public override string Name => "Vision input behavior";

        protected override async Task<ModelEvaluationScenarioResult> ExecuteAsync(IChatClient client, ModelProfile profile, Stopwatch stopwatch, CancellationToken ct)
        {
            if (!profile.Capabilities.SupportsVision || !profile.Capabilities.SupportsImageInput)
                return Unsupported(Id, Name, stopwatch, $"Profile '{profile.Id}' does not advertise vision/image input support.");

            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.User, new AIContent[]
                    {
                        new TextContent("The image is a single-color square. Answer with one color word."),
                        new UriContent(new Uri(TinyRedPngDataUri), "image/png")
                    })
                ],
                new ChatOptions { ModelId = profile.ModelId, Temperature = 0, MaxOutputTokens = 32 },
                ct);

            return new ModelEvaluationScenarioResult
            {
                ScenarioId = Id,
                Name = Name,
                Status = !string.IsNullOrWhiteSpace(response.Text) ? "passed" : "warning",
                LatencyMs = stopwatch.ElapsedMilliseconds,
                InputTokens = response.Usage?.InputTokenCount ?? 0,
                OutputTokens = response.Usage?.OutputTokenCount ?? 0,
                Summary = response.Text
            };
        }
    }
}
