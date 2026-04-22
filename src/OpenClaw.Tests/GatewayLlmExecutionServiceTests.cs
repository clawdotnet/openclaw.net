using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayLlmExecutionServiceTests
{
    [Fact]
    public void ClassifyProviderFailure_InvalidApiKey_SanitizesMessage()
    {
        var classification = GatewayLlmExecutionService.ClassifyProviderFailure(
            new InvalidOperationException("HTTP 401 (invalid_request_error: invalid_api_key) Incorrect API key provided."),
            "openai");

        Assert.NotNull(classification);
        Assert.Equal("invalid-key", classification!.Code);
        Assert.Contains("OpenAI credentials were rejected.", classification.UserMessage, StringComparison.Ordinal);
        Assert.Contains("MODEL_PROVIDER_KEY or OPENAI_API_KEY", classification.OperatorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid_request_error", classification.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyProviderFailure_MissingEndpoint_HasActionableGuidance()
    {
        var classification = GatewayLlmExecutionService.ClassifyProviderFailure(
            new InvalidOperationException("Endpoint must be set for provider 'azure-openai'."),
            "azure-openai");

        Assert.NotNull(classification);
        Assert.Equal("missing-endpoint", classification!.Code);
        Assert.Contains("MODEL_PROVIDER_ENDPOINT", classification.UserMessage, StringComparison.Ordinal);
        Assert.Contains("OpenClaw:Llm:Endpoint", classification.OperatorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyProviderFailure_UnsupportedProvider_SanitizesMessage()
    {
        var classification = GatewayLlmExecutionService.ClassifyProviderFailure(
            new InvalidOperationException("Unsupported LLM provider: custom-provider"),
            "custom-provider");

        Assert.NotNull(classification);
        Assert.Equal("unsupported-provider", classification!.Code);
        Assert.Contains("OpenClaw:Llm:Provider", classification.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsupported LLM provider: custom-provider", classification.UserMessage, StringComparison.Ordinal);
    }
}
