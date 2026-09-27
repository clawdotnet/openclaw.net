using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.LayaService.Protocol;

public static class RequestValidator
{
    private static readonly HashSet<string> Checkpoints = ["english", "multilingual", "typed-decisions"];
    private static readonly HashSet<string> QuestionProperties = ["type", "instructions", "criteria"];

    public static void Validate(DecisionWireRequest request, string configuredModel)
    {
        if (!string.Equals(request.Model, configuredModel, StringComparison.Ordinal))
        {
            Reject("model_version_mismatch");
        }

        if (request.State.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array) ||
            StrictJson.Canonicalize(request.State).Length > 32000)
        {
            Reject("invalid_state");
        }

        if (request.RubricVersion is null || !Regex.IsMatch(request.RubricVersion, "^[a-zA-Z0-9_.-]{1,80}$", RegexOptions.CultureInvariant))
        {
            Reject("invalid_rubric");
        }

        if (request.Language is not null && !Regex.IsMatch(request.Language, "^[a-zA-Z0-9-]{1,35}$", RegexOptions.CultureInvariant))
        {
            Reject("invalid_language");
        }

        if (request.Questions.ValueKind != JsonValueKind.Object || request.Questions.GetPropertyCount() is < 1 or > 16)
        {
            Reject("invalid_questions");
        }

        foreach (var question in request.Questions.EnumerateObject())
        {
            ValidateQuestion(question.Name, question.Value);
        }
    }

    private static void ValidateQuestion(string name, JsonElement question)
    {
        if (!Regex.IsMatch(name, "^[a-zA-Z0-9_.-]{1,80}$", RegexOptions.CultureInvariant) || question.ValueKind != JsonValueKind.Object)
        {
            Reject("invalid_question");
        }

        if (question.EnumerateObject().Any(property => !QuestionProperties.Contains(property.Name)))
        {
            Reject("invalid_question");
        }

        if (!question.TryGetProperty("instructions", out var instructions) || instructions.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(instructions.GetString()) || instructions.GetString()!.Length > 2000)
        {
            Reject("invalid_instructions");
        }

        if (!question.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            Reject("invalid_question_type");
        }

        var criteria = question.TryGetProperty("criteria", out var criteriaValue) ? criteriaValue : default;

        switch (type.GetString())
        {
            case "choice":
                ValidateChoice(criteria);
                break;
            case "score":
                ValidateScore(criteria);
                break;
            case "noul":
                ValidateNoul(criteria);
                break;
            default:
                Reject("invalid_question_type");
                break;
        }
    }

    private static void ValidateChoice(JsonElement criteria)
    {
        if (criteria.ValueKind != JsonValueKind.Object || criteria.GetPropertyCount() is < 2 or > 20)
        {
            Reject("invalid_choices");
        }

        foreach (var item in criteria.EnumerateObject())
        {
            if (item.Name.Length is < 1 or > 80 || !IsOptionalText(item.Value, 2000))
            {
                Reject("invalid_criteria");
            }
        }
    }

    private static void ValidateScore(JsonElement criteria)
    {
        if (criteria.ValueKind != JsonValueKind.Array || criteria.GetArrayLength() is < 2 or > 10)
        {
            Reject("invalid_score");
        }

        foreach (var item in criteria.EnumerateArray())
        {
            if (!IsOptionalText(item, 2000))
            {
                Reject("invalid_criteria");
            }
        }
    }

    private static void ValidateNoul(JsonElement criteria)
    {
        if (criteria.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
        if (criteria.ValueKind != JsonValueKind.Object || criteria.GetPropertyCount() != 2)
        {
            Reject("invalid_noul");
        }

        if (!criteria.TryGetProperty("false", out var falseValue))
        {
            Reject("invalid_noul");
        }

        if (!criteria.TryGetProperty("true", out var trueValue))
        {
            Reject("invalid_noul");
        }

        if (!IsOptionalText(falseValue, 2000) || !IsOptionalText(trueValue, 2000))
        {
            Reject("invalid_criteria");
        }
    }

    private static bool IsOptionalText(JsonElement value, int maximumLength)
        => value.ValueKind == JsonValueKind.Null ||
           (value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= maximumLength);

    [DoesNotReturn]
    private static void Reject(string reasonCode) => throw new ProtocolRejectionException(reasonCode);
}