using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.LayaService.Evaluation;
using OpenClaw.LayaService.Protocol;
using Xunit;

namespace OpenClaw.LayaService.Tests;

public sealed class CaseEvaluatorTests
{
    private const string Model = "laya@1c5edc17a7acd8701df6fc341c0d179f1c62c982";

    [Fact]
    public async Task EvaluateAsync_UsesDefaultsAndWritesStateFreeObservations()
    {
        var directory = CreateTempDirectory();
        var dataset = Path.Combine(directory, "cases.jsonl");
        var output = Path.Combine(directory, "observations.jsonl");
        await File.WriteAllTextAsync(dataset, """
            {"case_id":"case-one","state":"private state text","labels":{"tier":"T0","high_risk":false,"requires_tools":true}}
            """ + "\n");
        var handler = new RespondingHandler();
        using var http = new HttpClient(handler);
        try
        {
            var count = await CaseEvaluator.EvaluateAsync(dataset, new Uri("http://127.0.0.1:8765/v1/decisions"), output, http, CancellationToken.None);

            Assert.Equal(3, count);
            Assert.Equal(Model, handler.Request!.Model);
            Assert.Equal("openclaw-laya-tiers-v1", handler.Request.RubricVersion);
            var observations = await File.ReadAllLinesAsync(output);
            Assert.Equal(3, observations.Length);
            foreach (var line in observations)
            {
                using var observation = JsonDocument.Parse(line);
                Assert.False(observation.RootElement.TryGetProperty("state", out _));
                Assert.Equal("raw", observation.RootElement.GetProperty("source_calibration").GetString());
                Assert.Equal("NLaya", observation.RootElement.GetProperty("runtime").GetString());
                Assert.Equal("1.0.0", observation.RootElement.GetProperty("sdk_version").GetString());
                Assert.Equal(64, observation.RootElement.GetProperty("case_fingerprint").GetString()!.Length);
                Assert.False(observation.RootElement.GetProperty("answer").ValueKind == JsonValueKind.Undefined);
            }
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateAsync_RejectsRemoteEndpointBeforeSending()
    {
        var directory = CreateTempDirectory();
        var dataset = Path.Combine(directory, "cases.jsonl");
        await File.WriteAllTextAsync(dataset, "{}");
        var handler = new RespondingHandler();
        using var http = new HttpClient(handler);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => CaseEvaluator.EvaluateAsync(
                dataset, new Uri("https://example.com/v1/decisions"), Path.Combine(directory, "out.jsonl"), http, CancellationToken.None));
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateAsync_MetadataFailureDoesNotReplacePreviousOutput()
    {
        var directory = CreateTempDirectory();
        var dataset = Path.Combine(directory, "cases.jsonl");
        var output = Path.Combine(directory, "observations.jsonl");
        await File.WriteAllTextAsync(dataset, """
            {"case_id":"case-one","model":"laya@1c5edc17a7acd8701df6fc341c0d179f1c62c982","state":"hello","rubric_version":"v1","questions":{"tier":{"type":"choice","instructions":"Tier?","criteria":{"T0":"small","T1":"large"}}},"labels":{"tier":"T0"}}
            """ + "\n");
        await File.WriteAllTextAsync(output, "previous observations\n");
        var handler = new RespondingHandler { Runtime = "wrong" };
        using var http = new HttpClient(handler);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => CaseEvaluator.EvaluateAsync(
                dataset, new Uri("http://127.0.0.1:8765/v1/decisions"), output, http, CancellationToken.None));
            Assert.Equal("previous observations\n", await File.ReadAllTextAsync(output));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RespondingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Runtime { get; init; } = "NLaya";
        public DecisionWireRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Request = StrictJson.ParseRequest(Encoding.UTF8.GetBytes(body.RootElement.GetRawText()));
            var answers = new JsonObject();
            foreach (var question in Request.Questions.EnumerateObject())
            {
                if (question.Value.GetProperty("type").GetString() == "choice")
                {
                    var probabilities = new JsonObject();
                    foreach (var candidate in question.Value.GetProperty("criteria").EnumerateObject())
                        probabilities[candidate.Name] = candidate.Name == "T0" ? 1 : 0;
                    answers[question.Name] = new JsonObject
                    {
                        ["type"] = "choice", ["choice"] = probabilities.First().Key,
                        ["confidence"] = 1, ["answer_confidence"] = 1, ["probabilities"] = probabilities
                    };
                }
                else
                {
                    answers[question.Name] = new JsonObject
                    {
                        ["type"] = "noul", ["noul"] = 0.8, ["value"] = true,
                        ["confidence"] = 0.8, ["answer_confidence"] = 0.8
                    };
                }
            }

            var response = new JsonObject
            {
                ["model"] = Request.Model,
                ["answers"] = answers,
                ["raw_answers"] = answers.DeepClone(),
                ["usage"] = new JsonObject { ["input_tokens"] = 8, ["output_tokens"] = 0 },
                ["metadata"] = new JsonObject
                {
                    ["checkpoint"] = "english", ["revision"] = Request.Model[5..],
                    ["calibration_id"] = "uncalibrated", ["schema_hash"] = StrictJson.SchemaHash(Request.Questions),
                    ["rubric_version"] = Request.RubricVersion, ["device"] = "cpu", ["sdk_version"] = "1.0.0",
                    ["runtime"] = Runtime, ["truncated"] = false
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(response.ToJsonString())
            };
        }
    }
}