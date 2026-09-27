using System.Text.Json;

namespace OpenClaw.LayaService.Evaluation;

internal sealed record AnswerDistribution(string Type, IReadOnlyList<string> Keys, IReadOnlyList<double> Probabilities)
{
    public static AnswerDistribution Parse(JsonElement answer, JsonElement question)
    {
        if (answer.ValueKind != JsonValueKind.Object || question.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid answer distribution.");
        var type = question.GetProperty("type").GetString();
        if (answer.GetProperty("type").GetString() != type)
            throw new InvalidDataException("Answer type mismatch.");

        if (type == "noul")
        {
            var value = answer.GetProperty("noul");
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var probability) || !double.IsFinite(probability) || probability is < 0 or > 1)
                throw new InvalidDataException("Invalid answer probability.");
            return new AnswerDistribution(type, ["false", "true"], [1 - probability, probability]);
        }

        if (type is not ("choice" or "score") || !answer.TryGetProperty("probabilities", out var distribution) || distribution.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid answer distribution.");
        var keys = type == "choice"
            ? question.GetProperty("criteria").EnumerateObject().Select(candidate => candidate.Name).ToArray()
            : Enumerable.Range(0, question.GetProperty("criteria").GetArrayLength()).Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (keys.Length < 2 || distribution.GetPropertyCount() != keys.Length)
            throw new InvalidDataException("Invalid answer distribution.");
        var probabilities = new double[keys.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            if (!distribution.TryGetProperty(keys[index], out var item) || item.ValueKind != JsonValueKind.Number ||
                !item.TryGetDouble(out probabilities[index]) || !double.IsFinite(probabilities[index]) || probabilities[index] is < 0 or > 1)
                throw new InvalidDataException("Invalid answer probability.");
        }
        var sum = probabilities.Sum();
        if (Math.Abs(sum - 1) > 0.002) throw new InvalidDataException("Invalid answer probability sum.");
        for (var index = 0; index < probabilities.Length; index++) probabilities[index] /= sum;
        return new AnswerDistribution(type, keys, probabilities);
    }
}