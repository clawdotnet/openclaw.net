using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenClaw.LayaService.Inference;
using OpenClaw.LayaService.Models;
using OpenClaw.LayaService.Protocol;

namespace OpenClaw.LayaService.Evaluation;

public static class CaseEvaluator
{
    private const int MaximumResponseBytes = 262144;
    private static readonly JsonSerializerOptions ObservationJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> EvaluateAsync(
        string datasetPath,
        Uri endpoint,
        string outputPath,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(http);
        ValidateEndpoint(endpoint);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath) ?? throw new ArgumentException("Invalid output path.");
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(outputDirectory, "." + Path.GetFileName(fullOutputPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        var observationsWritten = 0;
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true))
            using (var input = new StreamReader(datasetPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: false))
            {
                while (await input.ReadLineAsync(cancellationToken) is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var caseDocument = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                    RejectDuplicateProperties(caseDocument.RootElement);
                    var (caseId, labels, request, requestElement) = BuildRequest(caseDocument.RootElement);
                    if (caseId.Length == 0 || !caseIds.Add(caseId)) throw new InvalidDataException("Case IDs must be nonempty and unique.");
                    var response = await SendAsync(http, endpoint, request, cancellationToken);
                    var caseFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(StrictJson.Canonicalize(requestElement))));
                    foreach (var answerProperty in response.RawAnswers.EnumerateObject())
                    {
                        var questionId = answerProperty.Name;
                        var answer = answerProperty.Value;
                        var label = NormalizeLabel(labels.GetProperty(questionId), answer, request.Questions.GetProperty(questionId));
                        var observation = new Observation(caseId, caseFingerprint, questionId, response.Model,
                            response.Checkpoint, response.SchemaHash, response.SdkVersion, answer.Clone(), label);
                        var observationJson = JsonSerializer.SerializeToElement(observation, ObservationJson);
                        await writer.WriteAsync(StrictJson.Canonicalize(observationJson));
                        await writer.WriteAsync("\n");
                        observationsWritten++;
                    }
                }
                await writer.FlushAsync(cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            if (observationsWritten == 0) throw new InvalidDataException("Empty evaluation dataset.");
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return observationsWritten;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Host != "127.0.0.1" ||
            endpoint.Port is < 1 or > 65535 || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            endpoint.AbsolutePath != "/v1/decisions")
        {
            throw new ArgumentException("Use the local http://127.0.0.1:PORT/v1/decisions endpoint.", nameof(endpoint));
        }
    }

    private static (string CaseId, JsonElement Labels, DecisionWireRequest Request, JsonElement RequestElement) BuildRequest(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("case_id", out var caseIdElement) ||
            caseIdElement.ValueKind != JsonValueKind.String || !input.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Invalid evaluation case.");
        }

        var requestNode = JsonNode.Parse(input.GetRawText())!.AsObject();
        requestNode.Remove("case_id");
        requestNode.Remove("labels");
        if (!requestNode.ContainsKey("questions"))
        {
            using var rubric = typeof(CaseEvaluator).Assembly.GetManifestResourceStream("OpenClaw.LayaService.Rubric.openclaw-laya-tiers-v1.json") is { } stream
                ? JsonDocument.Parse(stream)
                : throw new InvalidDataException("Default rubric is missing.");
            requestNode["rubric_version"] = rubric.RootElement.GetProperty("rubric_version").GetString();
            requestNode["questions"] = JsonNode.Parse(rubric.RootElement.GetProperty("questions").GetRawText());
        }
        if (!requestNode.ContainsKey("model")) requestNode["model"] = "laya@" + ModelManifest.DefaultRevision;
        var requestElement = JsonDocument.Parse(requestNode.ToJsonString()).RootElement.Clone();
        var request = StrictJson.ParseRequest(Encoding.UTF8.GetBytes(StrictJson.Canonicalize(requestElement)));
        RequestValidator.Validate(request, request.Model);
        if (labels.GetPropertyCount() != request.Questions.GetPropertyCount() ||
            request.Questions.EnumerateObject().Any(question => !labels.TryGetProperty(question.Name, out _)))
        {
            throw new InvalidDataException("Labels must cover every question exactly once.");
        }
        return (caseIdElement.GetString()!, labels.Clone(), request, requestElement);
    }

    private static async Task<EvaluationResponse> SendAsync(HttpClient http, Uri endpoint, DecisionWireRequest request, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(StrictJson.Canonicalize(JsonSerializer.SerializeToElement(request)));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(payload)
        };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest || !response.IsSuccessStatusCode ||
            response.RequestMessage?.RequestUri != endpoint)
        {
            throw new InvalidDataException("Evaluation endpoint returned an unusable response.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await ReadBoundedAsync(body, cancellationToken);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        var metadata = root.GetProperty("metadata");
        var model = root.GetProperty("model").GetString();
        var checkpoint = metadata.GetProperty("checkpoint").GetString();
        var schemaHash = metadata.GetProperty("schema_hash").GetString();
        var sdkVersion = metadata.GetProperty("sdk_version").GetString();
        if (model != request.Model || checkpoint is null || !ModelManifest.CheckpointNames.Contains(checkpoint) ||
            metadata.GetProperty("revision").GetString() != request.Model[5..] ||
            metadata.GetProperty("schema_hash").GetString() != StrictJson.SchemaHash(request.Questions) ||
            metadata.GetProperty("rubric_version").GetString() != request.RubricVersion ||
            sdkVersion != NLayaDecisionPredictor.SdkVersion || metadata.GetProperty("runtime").GetString() != "NLaya" ||
            metadata.GetProperty("truncated").GetBoolean() || !root.TryGetProperty("raw_answers", out var rawAnswers) ||
            rawAnswers.ValueKind != JsonValueKind.Object || rawAnswers.GetPropertyCount() != request.Questions.GetPropertyCount())
        {
            throw new InvalidDataException("Evaluation response metadata mismatch.");
        }
        foreach (var question in request.Questions.EnumerateObject())
        {
            if (!rawAnswers.TryGetProperty(question.Name, out var answer)) throw new InvalidDataException("Evaluation answer missing.");
            _ = AnswerDistribution.Parse(answer, question.Value);
        }
        return new EvaluationResponse(model, checkpoint, schemaHash!, sdkVersion!, rawAnswers.Clone());
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("Evaluation response too large.");
            output.Write(buffer, 0, read);
        }
    }

    private static string NormalizeLabel(JsonElement label, JsonElement answer, JsonElement question)
    {
        var type = question.GetProperty("type").GetString();
        if (type == "noul")
        {
            if (label.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Noul labels must be JSON booleans.");
            var normalized = label.GetBoolean() ? "true" : "false";
            if (!AnswerDistribution.Parse(answer, question).Keys.Contains(normalized, StringComparer.Ordinal))
                throw new InvalidDataException("Label is outside the question options.");
            return normalized;
        }

        var value = label.ValueKind switch
        {
            JsonValueKind.String => label.GetString(),
            JsonValueKind.Number => label.GetRawText(),
            _ => null
        };
        if (value is null || !AnswerDistribution.Parse(answer, question).Keys.Contains(value, StringComparer.Ordinal))
            throw new InvalidDataException("Label is outside the question options.");
        return value;
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private sealed record EvaluationResponse(string Model, string Checkpoint, string SchemaHash, string SdkVersion, JsonElement RawAnswers);
}