using System.Text.Json;
using System.Text.Json.Nodes;
using NLaya;
using NLaya.Lang;
using NLaya.Routing;
using NLaya.TorchSharp;
using OpenClaw.LayaService.Hosting;
using OpenClaw.LayaService.Models;
using OpenClaw.LayaService.Protocol;
using TorchSharp;

namespace OpenClaw.LayaService.Inference;

public sealed class NLayaDecisionPredictor : IDecisionPredictor, IAsyncDisposable
{
    public const string SdkVersion = "1.0.0";
    private readonly Router _router;
    private readonly IReadOnlyDictionary<string, LayaAgent> _agents;
    private readonly CalibrationStore _calibration;
    private readonly string _configuredCheckpoint;
    private readonly string _device;
    private readonly string _revision;
    private readonly IReadOnlyDictionary<string, string> _backendNames;

    private NLayaDecisionPredictor(
        string model,
        string configuredCheckpoint,
        string device,
        Router router,
        IReadOnlyDictionary<string, LayaAgent> agents,
        CalibrationStore calibration,
        string revision)
    {
        Model = model;
        _configuredCheckpoint = configuredCheckpoint;
        _device = device;
        _router = router;
        _agents = agents;
        _calibration = calibration;
        _revision = revision;
        _backendNames = agents.ToDictionary(pair => pair.Key, pair => pair.Value.Backend.Name, StringComparer.Ordinal);
    }

    public string Model { get; }

    public static async Task<NLayaDecisionPredictor> LoadAsync(
        VerifiedManifest manifest,
        ServeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);
        if (!ModelManifest.IsRevision(manifest.Revision) || options.Threads < 1 || options.Port is < 0 or > 65535 ||
            options.Device is not ("cpu" or "cuda" or "mps"))
        {
            throw new ArgumentException("Invalid serving options.");
        }

        var configuredCheckpoint = NormalizeConfiguredCheckpoint(options.Checkpoint);
        if (configuredCheckpoint != "auto" && !manifest.Checkpoints.ContainsKey(configuredCheckpoint))
        {
            throw new InvalidOperationException("checkpoint_not_installed");
        }

        var model = "laya@" + manifest.Revision;
        var calibration = CalibrationStore.Load(options.CalibrationPath, model);
        var router = new Router(new RouterOptions { AutoTaskDetection = false });
        var names = configuredCheckpoint == "auto"
            ? manifest.Checkpoints.Keys.Where(name => name is "english" or "multilingual").ToArray()
            : [configuredCheckpoint];
        if (names.Length == 0)
        {
            router.Dispose();
            throw new InvalidOperationException("checkpoint_not_installed");
        }

        var agents = new Dictionary<string, LayaAgent>(StringComparer.Ordinal);
        try
        {
            torch.set_num_threads(options.Threads);
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var checkpoint = manifest.Checkpoints[name];
                var agent = await Laya.LoadAsync(checkpoint.AbsolutePath,
                    configure => configure.UseTorchSharp(options.Device), cancellationToken);
                agents.Add(name, agent);
                EnsureDevice(agent.Backend.Name, options.Device);
                agent.Warmup();
            }

            return new NLayaDecisionPredictor(model, configuredCheckpoint, options.Device, router, agents, calibration, manifest.Revision);
        }
        catch
        {
            foreach (var agent in agents.Values) await agent.DisposeAsync();
            router.Dispose();
            throw;
        }
    }

    public JsonElement GetHealth()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            ready = true,
            model = Model,
            calibration_id = _calibration.Identifier,
            sdk_version = SdkVersion,
            runtime = "NLaya",
            checkpoints = _backendNames
        });
        return payload.Clone();
    }

    public async Task<JsonElement> PredictAsync(DecisionWireRequest request, CancellationToken cancellationToken)
    {
        var state = LayaState.ParseJson(request.State.GetRawText());
        var questions = Questions.Parse(request.Questions.GetRawText());
        var checkpoint = ResolveCheckpoint(request, _configuredCheckpoint, _agents.Keys.ToHashSet(StringComparer.Ordinal), _router, state, questions);
        var agent = _agents[checkpoint];
        EnsureWithinTokenBudget(agent, state, questions);
        _calibration.ValidateRequest(request, checkpoint, questions);

        var prediction = await agent.PredictAsync(state, questions,
            new PredictOptions { Lang = request.Language }, cancellationToken);
        using var predictionDocument = JsonDocument.Parse(prediction.ToJsonString());
        var result = predictionDocument.RootElement.Clone();
        var rawAnswers = result.GetProperty("answers");
        var calibratedAnswers = _calibration.Apply(request, checkpoint, rawAnswers);
        var response = MapPrediction(request, result, checkpoint, GetDevice(agent.Backend.Name), _calibration.Identifier, calibratedAnswers);
        return response;
    }

    public async ValueTask DisposeAsync()
    {
        _router.Dispose();
        foreach (var agent in _agents.Values)
        {
            await agent.DisposeAsync();
        }
    }

    public static string ResolveCheckpoint(DecisionWireRequest request, string configuredCheckpoint, IReadOnlySet<string> installedCheckpoints)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(installedCheckpoints);
        var router = new Router(new RouterOptions { AutoTaskDetection = false });
        try
        {
            var state = LayaState.ParseJson(request.State.GetRawText());
            var questions = Questions.Parse(request.Questions.GetRawText());
            return ResolveCheckpoint(request, NormalizeConfiguredCheckpoint(configuredCheckpoint), installedCheckpoints, router, state, questions);
        }
        finally
        {
            router.Dispose();
        }
    }

    public static JsonElement MapPrediction(
        DecisionWireRequest request,
        JsonElement prediction,
        string checkpoint,
        string device,
        string calibrationId,
        JsonElement? calibratedAnswers = null)
    {
        if (!ModelManifest.CheckpointNames.Contains(checkpoint) || prediction.ValueKind != JsonValueKind.Object ||
            !prediction.TryGetProperty("answers", out var rawAnswers) || rawAnswers.ValueKind != JsonValueKind.Object ||
            !prediction.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Invalid NLaya prediction.");
        }

        var result = new JsonObject
        {
            ["model"] = request.Model,
            ["answers"] = JsonNode.Parse((calibratedAnswers ?? rawAnswers).GetRawText()),
            ["raw_answers"] = JsonNode.Parse(rawAnswers.GetRawText()),
            ["usage"] = JsonNode.Parse(usage.GetRawText()),
            ["metadata"] = new JsonObject
            {
                ["checkpoint"] = checkpoint,
                ["revision"] = request.Model[5..],
                ["calibration_id"] = calibrationId,
                ["schema_hash"] = StrictJson.SchemaHash(request.Questions),
                ["rubric_version"] = request.RubricVersion,
                ["device"] = device,
                ["sdk_version"] = SdkVersion,
                ["runtime"] = "NLaya",
                ["truncated"] = false
            }
        };
        using var resultDocument = JsonDocument.Parse(result.ToJsonString());
        return resultDocument.RootElement.Clone();
    }

    public static void EnsureDevice(string backendName, string requestedDevice)
    {
        if (!backendName.EndsWith(":" + requestedDevice, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("requested_device_unavailable");
        }
    }

    private static string ResolveCheckpoint(
        DecisionWireRequest request,
        string configuredCheckpoint,
        IReadOnlySet<string> installedCheckpoints,
        Router router,
        LayaState state,
        Questions questions)
    {
        var routeOptions = new RouteOptions
        {
            Lang = request.Language
        };

        var detected = Checkpoints.Name(router.Route(state, questions, routeOptions).Checkpoint);
        if (IsEnglishLanguage(request.Language) && !LanguageDetector.IsEnglish(state))
        {
            throw new ProtocolRejectionException("language_checkpoint_conflict");
        }

        var selected = configuredCheckpoint == "auto" ? detected : configuredCheckpoint;
        if (configuredCheckpoint == "english" && detected != "english" ||
            configuredCheckpoint == "typed-decisions" && IsEnglishLanguage(request.Language) && !LanguageDetector.IsEnglish(state))
        {
            throw new ProtocolRejectionException("language_checkpoint_conflict");
        }
        if (!installedCheckpoints.Contains(selected)) throw new ProtocolRejectionException("checkpoint_not_installed");
        return selected;
    }

    private static string NormalizeConfiguredCheckpoint(string? checkpoint)
    {
        if (string.Equals(checkpoint, "auto", StringComparison.Ordinal)) return "auto";
        if (checkpoint is not null && Checkpoints.TryParse(checkpoint, out var parsed) && parsed is not null)
        {
            return Checkpoints.Name(parsed.Value);
        }
        throw new InvalidOperationException("unknown_checkpoint");
    }

    private static void EnsureWithinTokenBudget(LayaAgent agent, LayaState state, Questions questions)
    {
        var maskToken = agent.Tokenizer.MaskToken;
        var serializedState = state.Serialize();
        EnsureReservedTokenAbsent(serializedState, maskToken);
        var stateTokenCount = agent.Tokenizer.Encode(serializedState).Length;
        foreach (var question in questions.Values)
        {
            var instruction = question.InstructionText;
            EnsureReservedTokenAbsent(instruction, maskToken);
            var options = question.RenderOptions();
            var optionLengths = options.Select(option =>
            {
                EnsureReservedTokenAbsent(option, maskToken);
                return 1 + agent.Tokenizer.Encode(" " + option).Length;
            }).ToArray();
            EnsureOptionTokenBudget(optionLengths);
            var headTokenCount = agent.Tokenizer.Encode(question.TypeName + " question: " + instruction).Length;
            EnsureQuestionHeadBudget(headTokenCount, optionLengths, agent.Config.HeadMaxLen);
            EnsureTokenBudget(4 + headTokenCount + optionLengths.Sum() + stateTokenCount, agent.Config.MaxLen);
        }
    }

    public static void EnsureReservedTokenAbsent(string text, string maskToken)
    {
        if (text.Contains(maskToken, StringComparison.Ordinal))
        {
            throw new ProtocolRejectionException("reserved_token_in_input");
        }
    }

    public static void EnsureOptionTokenBudget(IReadOnlyList<int> optionLengths)
    {
        if (optionLengths.Any(length => length > 49))
        {
            throw new ProtocolRejectionException("option_too_long");
        }
    }

    public static void EnsureQuestionHeadBudget(int headTokenCount, IReadOnlyList<int> optionLengths, int headMaximumLength)
    {
        var available = headMaximumLength - optionLengths.Sum();
        if (available < 16 || headTokenCount > Math.Max(8, available))
        {
            throw new ProtocolRejectionException("question_too_long");
        }
    }

    public static void EnsureTokenBudget(int sequenceLength, int maximumLength)
    {
        if (sequenceLength > maximumLength)
        {
            throw new ProtocolRejectionException("input_exceeds_token_budget");
        }
    }

    private static string GetDevice(string backendName)
        => backendName[(backendName.LastIndexOf(':') + 1)..].ToLowerInvariant();

    private static bool IsEnglishLanguage(string? language)
        => language is not null && language.Split(['-', '_', '.'], 2)[0].Equals("en", StringComparison.OrdinalIgnoreCase);
}
