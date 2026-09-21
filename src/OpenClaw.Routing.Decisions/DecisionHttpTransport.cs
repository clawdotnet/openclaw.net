using System.Text.Json;

namespace OpenClaw.Routing.Decisions;

internal static class DecisionHttpTransport
{
    public static async Task<DecisionResponse> SendAsync(HttpClient httpClient, HttpRequestMessage message,
        DecisionRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new DecisionException($"http_{(int)response.StatusCode}");

        // Never include upstream bodies in diagnostics. Bound even a chunked response before deserializing.
        const int maxResponseBytes = 256 * 1024;
        if (response.Content.Headers.ContentLength > maxResponseBytes)
            throw new DecisionException("response_too_large");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + read > maxResponseBytes)
                throw new DecisionException("response_too_large");
            buffer.Write(chunk, 0, read);
        }

        DecisionResponse result;
        try
        {
            result = JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length),
                DecisionJsonContext.Default.DecisionResponse) ?? throw new DecisionException("invalid_response");
        }
        catch (JsonException)
        {
            throw new DecisionException("invalid_response");
        }
        Validate(request, result);
        return result;
    }

    private static void Validate(DecisionRequest request, DecisionResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Model) || response.Model.Length > 128 ||
            response.Answers is null || response.Usage is null ||
            response.Usage.InputTokens < 0 || response.Usage.OutputTokens < 0 ||
            response.Answers.Count != request.Questions.Count)
            throw new DecisionException("invalid_response");

        foreach (var (id, question) in request.Questions)
        {
            if (!response.Answers.TryGetValue(id, out var answer) || answer is null || answer.Type != question.Type)
                throw new DecisionException("invalid_answer");
            if (question.Type == "noul")
            {
                if (!IsProbability(answer.Noul))
                    throw new DecisionException("invalid_answer");
                continue;
            }

            if (!IsProbability(answer.Confidence) || answer.Probabilities is not { Count: > 0 } probabilities ||
                probabilities.Values.Any(value => !IsProbability(value)) || Math.Abs(probabilities.Values.Sum() - 1) > 0.001)
                throw new DecisionException("invalid_probabilities");

            if (question.Type == "choice")
            {
                if (question.Criteria is not JsonElement criteria || criteria.ValueKind != JsonValueKind.Object)
                    throw new DecisionException("invalid_choice");
                if (probabilities.Count != criteria.EnumerateObject().Count() ||
                    probabilities.Keys.Any(key => !criteria.TryGetProperty(key, out _)) ||
                    answer.Choice is null || !probabilities.TryGetValue(answer.Choice, out var chosen) ||
                    chosen < probabilities.Values.Max())
                    throw new DecisionException("invalid_choice");
            }
            else if (question.Type == "score")
            {
                var levels = question.Criteria is { ValueKind: JsonValueKind.Array } criteria ? criteria.GetArrayLength() : 0;
                if (levels is < 2 or > 10 || answer.Score is not { } score || !double.IsFinite(score) ||
                    score < 0 || score > levels - 1 || probabilities.Count != levels ||
                    answer.Legend is null || answer.Legend.Count != levels ||
                    probabilities.Keys.Any(key => !answer.Legend.ContainsKey(key)) ||
                    Enumerable.Range(0, levels).Any(index => !probabilities.ContainsKey(index.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                    throw new DecisionException("invalid_score");
            }
            else
            {
                throw new DecisionException("unsupported_question");
            }
        }
    }

    private static bool IsProbability(double? value) => value is >= 0 and <= 1;

}
