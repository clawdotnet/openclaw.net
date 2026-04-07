namespace OpenClaw.Gateway;

internal sealed class RuntimeOperationsState
{
    public OpenClaw.Core.Abstractions.IModelProfileRegistry ModelProfiles { get; init; } = EmptyModelProfileRegistry.Instance;
    public required ProviderPolicyService ProviderPolicies { get; init; }
    public required LlmProviderRegistry ProviderRegistry { get; init; }
    public required GatewayLlmExecutionService LlmExecution { get; init; }
    public required PluginHealthService PluginHealth { get; init; }
    public required ToolApprovalGrantStore ApprovalGrants { get; init; }
    public required RuntimeEventStore RuntimeEvents { get; init; }
    public required OperatorAuditStore OperatorAudit { get; init; }
    public required WebhookDeliveryStore WebhookDeliveries { get; init; }
    public required ActorRateLimitService ActorRateLimits { get; init; }
    public required SessionMetadataStore SessionMetadata { get; init; }

    private sealed class EmptyModelProfileRegistry : OpenClaw.Core.Abstractions.IModelProfileRegistry
    {
        public static EmptyModelProfileRegistry Instance { get; } = new();

        public string? DefaultProfileId => null;

        public IReadOnlyList<OpenClaw.Core.Models.ModelProfileStatus> ListStatuses() => [];

        public bool TryGet(string profileId, out OpenClaw.Core.Models.ModelProfile? profile)
        {
            profile = null;
            return false;
        }
    }
}
