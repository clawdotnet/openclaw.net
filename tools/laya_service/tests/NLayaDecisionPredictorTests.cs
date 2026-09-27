using System.Text.Json;
using OpenClaw.LayaService.Inference;
using OpenClaw.LayaService.Protocol;
using Xunit;

namespace OpenClaw.LayaService.Tests;

public sealed class NLayaDecisionPredictorTests
{
    private const string Model = "laya@1c5edc17a7acd8701df6fc341c0d179f1c62c982";
    private const string QuestionsJson = """
        {"tier":{"type":"choice","instructions":"Choose a tier","criteria":{"small":"simple","large":"complex"}},"severity":{"type":"score","instructions":"Rate severity","criteria":["low","high"]},"urgent":{"type":"noul","instructions":"Is it urgent?","criteria":{"false":"no","true":"yes"}}}
        """;

    [Theory]
    [InlineData("A short English message.", null, "english")]
    [InlineData("你好世界，这是一条包含 English tokens 的混合文本", null, "multilingual")]
    [InlineData("Bonjour merci. Je suis avec une demande pour un remboursement.", "fr", "multilingual")]
    [InlineData("中文", "en", "language_checkpoint_conflict")]
    [InlineData("Hello", "fr", "multilingual")]
    public void ResolveCheckpoint_UsesLanguageAndRejectsConflictingHints(string state, string? language, string expected)
    {
        using var stateDocument = JsonDocument.Parse(JsonSerializer.Serialize(state));
        using var questionsDocument = JsonDocument.Parse(QuestionsJson);
        var request = Request(stateDocument.RootElement.Clone(), questionsDocument.RootElement.Clone(), language);

        if (expected == "language_checkpoint_conflict")
        {
            var exception = Assert.Throws<ProtocolRejectionException>(() =>
                NLayaDecisionPredictor.ResolveCheckpoint(request, "auto", Installed("english", "multilingual")));
            Assert.Equal(expected, exception.ReasonCode);
        }
        else
        {
            Assert.Equal(expected, NLayaDecisionPredictor.ResolveCheckpoint(request, "auto", Installed("english", "multilingual")));
        }
    }

    [Fact]
    public void ResolveCheckpoint_DoesNotAutoSelectTypedDecisionsAndRequiresExplicitInstallation()
    {
        using var stateDocument = JsonDocument.Parse("\"A typed invoice decision\"");
        using var questionsDocument = JsonDocument.Parse("""
            {"action":{"type":"choice","instructions":"Choose action","criteria":{"act":"act","review":"review"}},"needs_review":{"type":"noul","instructions":"Needs review?","criteria":{"false":"no","true":"yes"}},"outcome":{"type":"choice","instructions":"Choose outcome","criteria":{"ok":"ok","fail":"fail"}},"risk":{"type":"score","instructions":"Rate risk","criteria":["low","high"]},"urgency":{"type":"score","instructions":"Rate urgency","criteria":["low","high"]}}
            """);
        var request = Request(stateDocument.RootElement.Clone(), questionsDocument.RootElement.Clone());
        var questions = NLaya.Questions.Parse(questionsDocument.RootElement.GetRawText());

        Assert.Equal("agent_trace_observability", NLaya.Routing.Router.MatchTypedDecisionsWorkflow(questions.Keys));
        Assert.Equal("english", NLayaDecisionPredictor.ResolveCheckpoint(request, "auto", Installed("english", "typed-decisions")));
        Assert.Equal("typed-decisions", NLayaDecisionPredictor.ResolveCheckpoint(request, "typed-decisions", Installed("typed-decisions")));
        Assert.Throws<InvalidOperationException>(() =>
            NLayaDecisionPredictor.ResolveCheckpoint(request, "unknown", Installed("english")));
        using var nonEnglishState = JsonDocument.Parse("\"你好世界\"");
        var multilingualRequest = Request(nonEnglishState.RootElement.Clone(), questionsDocument.RootElement.Clone());
        var unavailable = Assert.Throws<ProtocolRejectionException>(() =>
            NLayaDecisionPredictor.ResolveCheckpoint(multilingualRequest, "auto", Installed("english")));
        Assert.Equal("checkpoint_not_installed", unavailable.ReasonCode);
    }

    [Fact]
    public void DeviceFallbackAndTokenTruncationAreRejected()
    {
        NLayaDecisionPredictor.EnsureDevice("torchsharp:cpu", "cpu");
        Assert.Throws<InvalidOperationException>(() => NLayaDecisionPredictor.EnsureDevice("torchsharp:cpu", "cuda"));
        NLayaDecisionPredictor.EnsureTokenBudget(512, 512);
        var tooLong = Assert.Throws<ProtocolRejectionException>(() => NLayaDecisionPredictor.EnsureTokenBudget(513, 512));
        Assert.Equal("input_exceeds_token_budget", tooLong.ReasonCode);
        Assert.Throws<ProtocolRejectionException>(() => NLayaDecisionPredictor.EnsureReservedTokenAbsent("safe [MASK] input", "[MASK]"));
        Assert.Throws<ProtocolRejectionException>(() => NLayaDecisionPredictor.EnsureOptionTokenBudget([49, 50]));
        Assert.Throws<ProtocolRejectionException>(() => NLayaDecisionPredictor.EnsureQuestionHeadBudget(97, [48, 48], 192));
    }

    [Fact]
    public void MapPrediction_PreservesTypedAnswersAndAddsPinnedMetadata()
    {
        using var stateDocument = JsonDocument.Parse("\"A short English message.\"");
        using var questionsDocument = JsonDocument.Parse(QuestionsJson);
        using var predictionDocument = JsonDocument.Parse("""
            {"answers":{"tier":{"type":"choice","choice":"small","confidence":0.5,"answer_confidence":0.7,"probabilities":{"small":0.7,"large":0.3}},"severity":{"type":"score","score":0.3,"probabilities":{"0":0.7,"1":0.3}},"urgent":{"type":"noul","noul":0.8,"value":true}},"usage":{"input_tokens":12,"output_tokens":0}}
            """);
        var request = Request(stateDocument.RootElement.Clone(), questionsDocument.RootElement.Clone(), rubricVersion: "rubric-v1");

        var root = NLayaDecisionPredictor.MapPrediction(
            request, predictionDocument.RootElement, "english", "cpu", "uncalibrated");

        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.Equal(root.GetProperty("answers").GetRawText(), root.GetProperty("raw_answers").GetRawText());
        Assert.Equal("small", root.GetProperty("answers").GetProperty("tier").GetProperty("choice").GetString());
        Assert.Equal(0.3, root.GetProperty("answers").GetProperty("severity").GetProperty("probabilities").GetProperty("1").GetDouble());
        Assert.True(root.GetProperty("answers").GetProperty("urgent").GetProperty("value").GetBoolean());
        var metadata = root.GetProperty("metadata");
        Assert.Equal("1.0.0", metadata.GetProperty("sdk_version").GetString());
        Assert.Equal("NLaya", metadata.GetProperty("runtime").GetString());
        Assert.Equal(Model[5..], metadata.GetProperty("revision").GetString());
        Assert.Equal("english", metadata.GetProperty("checkpoint").GetString());
        Assert.Equal("rubric-v1", metadata.GetProperty("rubric_version").GetString());
        Assert.Equal(StrictJson.SchemaHash(request.Questions), metadata.GetProperty("schema_hash").GetString());
        Assert.False(metadata.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void CalibrationV2TransformsAnswersAndRetainsRawProbabilities()
    {
        using var stateDocument = JsonDocument.Parse("\"A short English message.\"");
        using var questionsDocument = JsonDocument.Parse(QuestionsJson);
        using var predictionDocument = JsonDocument.Parse("""
            {"answers":{"tier":{"type":"choice","choice":"small","confidence":0.5,"answer_confidence":0.7,"probabilities":{"small":0.7,"large":0.3}},"severity":{"type":"score","score":0.3,"probabilities":{"0":0.7,"1":0.3}},"urgent":{"type":"noul","noul":0.8,"value":true}},"usage":{"input_tokens":12,"output_tokens":0}}
            """);
        var request = Request(stateDocument.RootElement.Clone(), questionsDocument.RootElement.Clone());
        var artifact = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 2,
            model = Model,
            schema_hash = StrictJson.SchemaHash(request.Questions),
            sdk_version = "1.0.0",
            runtime = "NLaya",
            temperatures = new Dictionary<string, Dictionary<string, double>>
            {
                ["english"] = new() { ["choice:2"] = 2, ["score:2"] = 2, ["noul:2"] = 2 }
            },
            validation = new { }
        });
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, artifact);
        try
        {
            var calibration = CalibrationStore.Load(path, Model);
            var calibrated = calibration.Apply(request, "english", predictionDocument.RootElement.GetProperty("answers"));
            var response = NLayaDecisionPredictor.MapPrediction(
                request, predictionDocument.RootElement, "english", "cpu", calibration.Identifier, calibrated);

            Assert.Equal(predictionDocument.RootElement.GetProperty("answers").GetRawText(), response.GetProperty("raw_answers").GetRawText());
            Assert.NotEqual(0.7, response.GetProperty("answers").GetProperty("tier").GetProperty("probabilities").GetProperty("small").GetDouble());
            Assert.NotEqual(0.8, response.GetProperty("answers").GetProperty("urgent").GetProperty("noul").GetDouble());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static DecisionWireRequest Request(JsonElement state, JsonElement questions, string? language = null, string rubricVersion = "rubric-v1")
        => new() { Model = Model, State = state, Questions = questions, RubricVersion = rubricVersion, Language = language };

    private static IReadOnlySet<string> Installed(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);
}