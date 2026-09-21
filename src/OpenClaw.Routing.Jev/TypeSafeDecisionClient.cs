using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenClaw.Routing.Jev;

/// <summary>AOT-safe HTTP transport. The caller owns deadlines and fallback, so requests are not retried.</summary>
public sealed class TypeSafeDecisionClient(
    HttpClient httpClient,
    Uri endpoint,
    Func<CancellationToken, ValueTask<string?>> resolveApiKey) : ITypeSafeDecisionClient, IDisposable
{
    public async Task<TypeSafeResponse> EvaluateAsync(TypeSafeRequest request, CancellationToken cancellationToken)
    {
        var apiKey = await resolveApiKey(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new TypeSafeException("missing_api_key");

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(request, JevJsonContext.Default.TypeSafeRequest);
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new TypeSafeException($"http_{(int)response.StatusCode}");

        // Never include upstream bodies in diagnostics. Bound even a chunked response before deserializing.
        const int maxResponseBytes = 256 * 1024;
        if (response.Content.Headers.ContentLength > maxResponseBytes)
            throw new TypeSafeException("response_too_large");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + read > maxResponseBytes)
                throw new TypeSafeException("response_too_large");
            buffer.Write(chunk, 0, read);
        }

        TypeSafeResponse result;
        try
        {
            result = JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length),
                JevJsonContext.Default.TypeSafeResponse) ?? throw new TypeSafeException("invalid_response");
        }
        catch (JsonException)
        {
            throw new TypeSafeException("invalid_response");
        }
        Validate(request, result);
        return result;
    }

    private static void Validate(TypeSafeRequest request, TypeSafeResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Model) || response.Model.Length > 128 ||
            response.Answers is null || response.Usage is null ||
            response.Usage.InputTokens < 0 || response.Usage.OutputTokens < 0 ||
            response.Answers.Count != request.Questions.Count)
            throw new TypeSafeException("invalid_response");

        foreach (var (id, question) in request.Questions)
        {
            if (!response.Answers.TryGetValue(id, out var answer) || answer is null || answer.Type != question.Type)
                throw new TypeSafeException("invalid_answer");
            if (question.Type == "noul")
            {
                if (!IsProbability(answer.Noul))
                    throw new TypeSafeException("invalid_answer");
                continue;
            }

            if (!IsProbability(answer.Confidence) || answer.Probabilities is not { Count: > 0 } probabilities ||
                probabilities.Values.Any(value => !IsProbability(value)) || Math.Abs(probabilities.Values.Sum() - 1) > 0.001)
                throw new TypeSafeException("invalid_probabilities");

            if (question.Type == "choice")
            {
                var criteria = question.Criteria;
                if (criteria is not { ValueKind: JsonValueKind.Object } ||
                    probabilities.Count != criteria.Value.EnumerateObject().Count() ||
                    probabilities.Keys.Any(key => !criteria.Value.TryGetProperty(key, out _)) ||
                    answer.Choice is null || !probabilities.TryGetValue(answer.Choice, out var chosen) ||
                    chosen < probabilities.Values.Max())
                    throw new TypeSafeException("invalid_choice");
            }
            else if (question.Type == "score")
            {
                var levels = question.Criteria is { ValueKind: JsonValueKind.Array } criteria ? criteria.GetArrayLength() : 0;
                if (levels is < 2 or > 10 || answer.Score is not { } score || !double.IsFinite(score) ||
                    score < 0 || score > levels - 1 || probabilities.Count != levels ||
                    answer.Legend is null || answer.Legend.Count != levels ||
                    probabilities.Keys.Any(key => !answer.Legend.ContainsKey(key)) ||
                    Enumerable.Range(0, levels).Any(index => !probabilities.ContainsKey(index.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                    throw new TypeSafeException("invalid_score");
            }
            else
            {
                throw new TypeSafeException("unsupported_question");
            }
        }
    }

    private static bool IsProbability(double? value) => value is >= 0 and <= 1;

    public void Dispose() => httpClient.Dispose();
}
