using Microsoft.Extensions.AI;
using OpenClaw.StrategosWorkflowHost.Steps;

namespace OpenClaw.StrategosWorkflowHost.Configuration;

// LlmMode controls which IChatClient the Strategos agent steps consume. Switches between
// real-LLM (DirectOpenAI), real-LLM-via-gateway (BackThroughGateway), and offline (Mock).
// BackThroughGateway is the production target: the sidecar reaches the OpenClaw gateway's
// LLM endpoint over HTTP and re-uses the gateway's auth/key/retries, so the sidecar stays
// stateless w.r.t. model credentials.
//
// P0 scope: Mock is wired; DirectOpenAI and BackThroughGateway throw at startup with a clear
// message so the operator knows what to add. The configuration surface is in place so the
// P1 work only needs to fill in the factory methods.
public enum LlmMode
{
    Mock,
    DirectOpenAI,
    BackThroughGateway,
}

public sealed class LlmOptions
{
    public LlmMode Mode { get; set; } = LlmMode.Mock;
    public string? OpenAIApiKey { get; set; }
    public string? OpenAIModel { get; set; } = "gpt-4o-mini";
    public string? GatewayBaseUrl { get; set; }
    public string? GatewayApiToken { get; set; }
}

public static class LlmClientFactory
{
    // Resolves the IChatClient for the configured LlmMode. The Strategos agent-step DI
    // container pulls this from the host's service collection.
    public static IChatClient Create(LlmOptions options, ILogger<LlmMode> logger)
    {
        ArgumentNullException.ThrowIfNull(options, nameof(options));
        ArgumentNullException.ThrowIfNull(logger, nameof(logger));

        return options.Mode switch
        {
            LlmMode.Mock => new MockReviewChatClient(),
            LlmMode.DirectOpenAI => throw new NotImplementedException(
                "LlmMode.DirectOpenAI is reserved for a follow-up that adds the OpenAI MEAI integration package. " +
                "P0 ships Mock mode only."),
            LlmMode.BackThroughGateway => throw new NotImplementedException(
                "LlmMode.BackThroughGateway is reserved for a follow-up that wires the gateway's " +
                "/v1/chat/completions endpoint into the sidecar. P0 ships Mock mode only."),
            _ => throw new InvalidOperationException($"Unknown LlmMode '{options.Mode}'.")
        };
    }
}