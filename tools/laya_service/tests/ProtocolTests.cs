using System.Text;
using System.Text.Json;
using OpenClaw.LayaService.Protocol;
using Xunit;

namespace OpenClaw.LayaService.Tests;

public sealed class ProtocolTests
{
    private const string Model = "laya@1c5edc17a7acd8701df6fc341c0d179f1c62c982";
    private const string GoldenRequest = "{\"model\":\"" + Model + "\",\"state\":\"hello\",\"questions\":{\"tier\":{\"type\":\"choice\",\"instructions\":\"Which task?\",\"criteria\":{\"small\":\"simple\",\"large\":\"complex\"}}},\"rubric_version\":\"test-v1\"}";

    [Fact]
    public void ParseAndValidate_AcceptsGoldenRequestAndPreservesQuestionOrder()
    {
        var request = StrictJson.ParseRequest(Encoding.UTF8.GetBytes(GoldenRequest));

        RequestValidator.Validate(request, Model);
        Assert.Equal("hello", request.State.GetString());
        Assert.Equal("small", request.Questions.GetProperty("tier").GetProperty("criteria").EnumerateObject().First().Name);
        Assert.Equal("{\"tier\":{\"type\":\"choice\",\"instructions\":\"Which task?\",\"criteria\":{\"small\":\"simple\",\"large\":\"complex\"}}}",
            StrictJson.Canonicalize(request.Questions));
    }

    [Fact]
    public void ParseRequest_RejectsDuplicateKeysNonFiniteNumbersAndInvalidUtf8()
    {
        foreach (var json in new[]
                 {
                     "{\"model\":\"a\",\"model\":\"b\"}",
                     "{\"x\":NaN}",
                     "{\"questions\":{\"tier\":{\"criteria\":{},\"criteria\":{}}}}"
                 })
        {
            var error = Assert.Throws<ProtocolRejectionException>(() => StrictJson.ParseRequest(Encoding.UTF8.GetBytes(json)));
            Assert.Equal("invalid_json", error.ReasonCode);
        }

        var invalidUtf8 = new byte[] { 0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D };
        Assert.Throws<ProtocolRejectionException>(() => StrictJson.ParseRequest(invalidUtf8));
    }

    [Fact]
    public void SchemaHash_PreservesCandidateOrder()
    {
        const string questions = """{"tier":{"type":"choice","instructions":"Which task?","criteria":{"small":"simple","large":"complex"}}}""";
        const string reversed = """{"tier":{"type":"choice","instructions":"Which task?","criteria":{"large":"complex","small":"simple"}}}""";
        var first = StrictJson.ParseRequest(Encoding.UTF8.GetBytes(GoldenRequest)).Questions;
        var second = StrictJson.ParseRequest(Encoding.UTF8.GetBytes(GoldenRequest.Replace(questions, reversed, StringComparison.Ordinal))).Questions;

        Assert.NotEqual(StrictJson.SchemaHash(first), StrictJson.SchemaHash(second));
    }

    [Fact]
    public void Validate_RejectsUnknownFieldsAndMismatchedModelWithSafeReasons()
    {
        var request = StrictJson.ParseRequest(Encoding.UTF8.GetBytes(GoldenRequest));

        var modelError = Assert.Throws<ProtocolRejectionException>(() => RequestValidator.Validate(
            request with { Model = "SECRET" }, Model));
        Assert.Equal("model_version_mismatch", modelError.ReasonCode);

        var unknown = GoldenRequest[..^1] + ",\"secret\":\"not echoed\"}";
        var fieldError = Assert.Throws<ProtocolRejectionException>(() => StrictJson.ParseRequest(Encoding.UTF8.GetBytes(unknown)));
        Assert.Equal("invalid_request", fieldError.ReasonCode);
    }

    [Fact]
    public void Validate_EnforcesStateQuestionAndRubricBounds()
    {
        var request = StrictJson.ParseRequest(Encoding.UTF8.GetBytes(GoldenRequest));
        var longState = request with { State = System.Text.Json.JsonDocument.Parse("\"" + new string('x', 32001) + "\"").RootElement.Clone() };
        Assert.Equal("invalid_state", Assert.Throws<ProtocolRejectionException>(() => RequestValidator.Validate(longState, Model)).ReasonCode);

        var invalidRubric = request with { RubricVersion = "../secret" };
        Assert.Equal("invalid_rubric", Assert.Throws<ProtocolRejectionException>(() => RequestValidator.Validate(invalidRubric, Model)).ReasonCode);
    }

    [Fact]
    public void Validate_RejectsInvalidQuestionTypesAndChoiceBounds()
    {
        var request = StrictJson.ParseRequest(Encoding.UTF8.GetBytes(GoldenRequest));
        var invalidType = JsonDocument.Parse("""{"tier":{"type":"text","instructions":"Choose","criteria":null}}""").RootElement.Clone();
        var typeError = Assert.Throws<ProtocolRejectionException>(() => RequestValidator.Validate(
            request with { Questions = invalidType }, Model));
        Assert.Equal("invalid_question_type", typeError.ReasonCode);

        var tooManyChoices = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["tier"] = new
            {
                type = "choice",
                instructions = "Choose",
                criteria = Enumerable.Range(0, 21).ToDictionary(index => $"choice{index}", _ => "label")
            }
        });
        var choicesError = Assert.Throws<ProtocolRejectionException>(() => RequestValidator.Validate(
            request with { Questions = tooManyChoices }, Model));
        Assert.Equal("invalid_choices", choicesError.ReasonCode);
    }

    [Fact]
    public void Validate_AcceptsNoulQuestionsWithoutOptionalCriteria()
    {
        var request = StrictJson.ParseRequest(Encoding.UTF8.GetBytes(GoldenRequest));
        var questions = JsonDocument.Parse("""
            {"high_risk":{"type":"noul","instructions":"Is this high risk?"}}
            """).RootElement.Clone();

        RequestValidator.Validate(request with { Questions = questions }, Model);
    }
}