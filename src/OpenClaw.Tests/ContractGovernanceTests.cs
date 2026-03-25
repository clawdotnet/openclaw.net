using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ContractGovernanceTests
{
    private static GatewayStartupContext CreateStartup(
        GatewayRuntimeMode mode = GatewayRuntimeMode.Jit,
        Dictionary<string, decimal>? tokenCostRates = null)
    {
        var config = new GatewayConfig();
        if (tokenCostRates is not null)
            config.TokenCostRates = tokenCostRates;

        return new GatewayStartupContext
        {
            Config = config,
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = mode == GatewayRuntimeMode.Aot ? "aot" : "jit",
                EffectiveMode = mode,
                DynamicCodeSupported = mode == GatewayRuntimeMode.Jit
            },
            IsNonLoopbackBind = false
        };
    }

    private static ContractGovernanceService CreateService(
        GatewayStartupContext? startup = null,
        ProviderUsageTracker? tracker = null)
    {
        startup ??= CreateStartup();
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var contractStore = new ContractStore(tempDir, NullLogger<ContractStore>.Instance);
        var runtimeEvents = new RuntimeEventStore(tempDir, NullLogger<RuntimeEventStore>.Instance);
        tracker ??= new ProviderUsageTracker();

        return new ContractGovernanceService(
            startup,
            contractStore,
            runtimeEvents,
            tracker,
            NullLogger<ContractGovernanceService>.Instance);
    }

    // === Pre-flight Validation ===

    [Fact]
    public void ValidatePreFlight_AllToolsRegistered_ReturnsValid()
    {
        var service = CreateService();
        var policy = new ContractPolicy
        {
            Id = "ctr_test",
            RequestedTools = ["web_search", "file_read"]
        };

        var result = service.ValidatePreFlight(policy, new HashSet<string> { "web_search", "file_read", "shell" });

        Assert.True(result.IsValid);
        Assert.Equal(2, result.GrantedTools.Length);
        Assert.Empty(result.DeniedTools);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidatePreFlight_UnknownTool_ReturnsDenied()
    {
        var service = CreateService();
        var policy = new ContractPolicy
        {
            Id = "ctr_test",
            RequestedTools = ["web_search", "nonexistent_tool"]
        };

        var result = service.ValidatePreFlight(policy, new HashSet<string> { "web_search" });

        Assert.True(result.IsValid); // unknown tool is a warning, not a hard error
        Assert.Single(result.GrantedTools);
        Assert.Single(result.DeniedTools);
        Assert.Contains("nonexistent_tool", result.DeniedTools);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void ValidatePreFlight_JitOnlyToolInAot_ReturnsDenied()
    {
        var startup = CreateStartup(GatewayRuntimeMode.Aot);
        var service = CreateService(startup);
        var policy = new ContractPolicy
        {
            Id = "ctr_test",
            RequestedTools = ["web_search", "delegate_agent"]
        };

        var result = service.ValidatePreFlight(policy, new HashSet<string> { "web_search", "delegate_agent" });

        Assert.True(result.IsValid);
        Assert.Contains("web_search", result.GrantedTools);
        Assert.Contains("delegate_agent", result.DeniedTools);
        Assert.Contains(result.Warnings, w => w.Contains("JIT"));
    }

    [Fact]
    public void ValidatePreFlight_WrongRuntimeMode_ReturnsError()
    {
        var startup = CreateStartup(GatewayRuntimeMode.Aot);
        var service = CreateService(startup);
        var policy = new ContractPolicy
        {
            Id = "ctr_test",
            RequiredRuntimeMode = "jit"
        };

        var result = service.ValidatePreFlight(policy, new HashSet<string>());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("jit") && e.Contains("aot"));
    }

    [Fact]
    public void ValidatePreFlight_NegativeBudget_ReturnsError()
    {
        var service = CreateService();
        var policy = new ContractPolicy
        {
            Id = "ctr_test",
            MaxCostUsd = -1m
        };

        var result = service.ValidatePreFlight(policy, new HashSet<string>());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("MaxCostUsd"));
    }

    [Fact]
    public void ValidatePreFlight_SoftWarningAboveHardCost_ReturnsError()
    {
        var service = CreateService();
        var policy = new ContractPolicy
        {
            Id = "ctr_test",
            MaxCostUsd = 1.00m,
            SoftCostWarningUsd = 2.00m
        };

        var result = service.ValidatePreFlight(policy, new HashSet<string>());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("SoftCostWarningUsd") && e.Contains("MaxCostUsd"));
    }

    [Fact]
    public void ValidatePreFlight_InvalidRuntimeMode_ReturnsError()
    {
        var service = CreateService();
        var policy = new ContractPolicy
        {
            Id = "ctr_test",
            RequiredRuntimeMode = "invalid"
        };

        var result = service.ValidatePreFlight(policy, new HashSet<string>());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("invalid"));
    }

    // === Cost Computation ===

    [Fact]
    public void ComputeSessionCostUsd_WithRates_CalculatesCorrectly()
    {
        var tracker = new ProviderUsageTracker();
        tracker.RecordTurn("session1", "ws", "openai", "gpt-4o", 1000, 500,
            new InputTokenComponentEstimate());

        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai:gpt-4o"] = 10.00m // $10 per 1K tokens for easy math
        };
        var startup = CreateStartup(tokenCostRates: rates);
        var service = CreateService(startup, tracker);

        var cost = service.ComputeSessionCostUsd("session1");

        // 1500 tokens * $10/1K = $15.00
        Assert.Equal(15.00m, cost);
    }

    [Fact]
    public void ComputeSessionCostUsd_NoUserRates_UsesDefaults()
    {
        var tracker = new ProviderUsageTracker();
        tracker.RecordTurn("session1", "ws", "openai", "gpt-4o", 1000, 500,
            new InputTokenComponentEstimate());

        var service = CreateService(tracker: tracker);
        var cost = service.ComputeSessionCostUsd("session1");

        // Default rate for openai:gpt-4o = 0.005 per 1K tokens
        // 1500 tokens * 0.005 / 1000 = 0.0075
        Assert.Equal(0.0075m, cost);
    }

    [Fact]
    public void ComputeSessionCostUsd_UserRateOverridesDefault()
    {
        var tracker = new ProviderUsageTracker();
        tracker.RecordTurn("session1", "ws", "openai", "gpt-4o", 1000, 500,
            new InputTokenComponentEstimate());

        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai:gpt-4o"] = 10.00m // Override the default 0.005
        };
        var startup = CreateStartup(tokenCostRates: rates);
        var service = CreateService(startup, tracker);

        var cost = service.ComputeSessionCostUsd("session1");

        // 1500 tokens * $10/1K = $15.00 (user rate wins over default)
        Assert.Equal(15.00m, cost);
    }

    [Fact]
    public void ComputeSessionCostUsd_FallbackToProviderRate()
    {
        var tracker = new ProviderUsageTracker();
        tracker.RecordTurn("session1", "ws", "openai", "gpt-4o-mini", 2000, 0,
            new InputTokenComponentEstimate());

        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = 5.00m // Fallback provider-level rate
        };
        var startup = CreateStartup(tokenCostRates: rates);
        var service = CreateService(startup, tracker);

        var cost = service.ComputeSessionCostUsd("session1");

        // 2000 tokens * $5/1K = $10.00
        Assert.Equal(10.00m, cost);
    }

    // === Contract Creation ===

    [Fact]
    public void CreateContract_GeneratesIdAndPersists()
    {
        var service = CreateService();
        var request = new ContractCreateRequest
        {
            Name = "test-contract",
            RequestedTools = ["web_search"]
        };

        var response = service.CreateContract(request, "session1", new HashSet<string> { "web_search" });

        Assert.NotNull(response.Policy);
        Assert.StartsWith("ctr_", response.Policy.Id);
        Assert.Equal("test-contract", response.Policy.Name);
        Assert.True(response.Validation.IsValid);
    }

    // === Scoped Capabilities (Hook) ===

    [Fact]
    public async Task ContractScopeHook_PathWithinScope_Allows()
    {
        var tempDir = Path.GetTempPath();
        var policy = new ContractPolicy
        {
            Id = "ctr_test",
            ScopedCapabilities = [new ScopedCapability { ToolName = "file_read", AllowedPaths = [tempDir] }]
        };
        var hook = new ContractScopeHook(
            _ => policy,
            _ => 0,
            NullLogger.Instance);

        var filePath = Path.Combine(tempDir, "test.txt");
        var context = new ToolHookContext
        {
            SessionId = "s1", ChannelId = "ws", SenderId = "u1",
            CorrelationId = "c1", ToolName = "file_read",
            ArgumentsJson = JsonSerializer.Serialize(new { path = filePath }),
            IsStreaming = false
        };

        var result = await hook.BeforeExecuteAsync(context, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task ContractScopeHook_PathOutsideScope_Denies()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"openclaw-scope-{Guid.NewGuid():N}");
        var allowedPath = Path.Combine(tempRoot, "allowed", "only");
        var deniedPath = Path.Combine(tempRoot, "other", "path", "file.txt");

        var policy = new ContractPolicy
        {
            Id = "ctr_test",
            ScopedCapabilities = [new ScopedCapability { ToolName = "file_read", AllowedPaths = [allowedPath] }]
        };
        var hook = new ContractScopeHook(
            _ => policy,
            _ => 0,
            NullLogger.Instance);

        var context = new ToolHookContext
        {
            SessionId = "s1", ChannelId = "ws", SenderId = "u1",
            CorrelationId = "c1", ToolName = "file_read",
            ArgumentsJson = JsonSerializer.Serialize(new { path = deniedPath }),
            IsStreaming = false
        };

        var result = await hook.BeforeExecuteAsync(context, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task ContractScopeHook_MaxToolCallsExceeded_Denies()
    {
        var policy = new ContractPolicy
        {
            Id = "ctr_test",
            MaxToolCalls = 5
        };
        var hook = new ContractScopeHook(
            _ => policy,
            _ => 5, // already at limit
            NullLogger.Instance);

        var context = new ToolHookContext
        {
            SessionId = "s1", ChannelId = "ws", SenderId = "u1",
            CorrelationId = "c1", ToolName = "web_search",
            ArgumentsJson = "{}",
            IsStreaming = false
        };

        var result = await hook.BeforeExecuteAsync(context, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task ContractScopeHook_NoContractPolicy_AllowsAll()
    {
        var hook = new ContractScopeHook(
            _ => null, // no contract
            _ => 0,
            NullLogger.Instance);

        var context = new ToolHookContext
        {
            SessionId = "s1", ChannelId = "ws", SenderId = "u1",
            CorrelationId = "c1", ToolName = "file_write",
            ArgumentsJson = JsonSerializer.Serialize(new { path = "/any/path" }),
            IsStreaming = false
        };

        var result = await hook.BeforeExecuteAsync(context, CancellationToken.None);
        Assert.True(result);
    }

    // === Store ===

    [Fact]
    public void ContractStore_AppendAndQuery_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var store = new ContractStore(tempDir, NullLogger<ContractStore>.Instance);

            var snapshot = new ContractExecutionSnapshot
            {
                ContractId = "ctr_001",
                SessionId = "session1",
                Status = "active",
                StartedAtUtc = DateTimeOffset.UtcNow
            };

            store.Append(snapshot);
            var results = store.Query(sessionId: "session1");

            Assert.Single(results);
            Assert.Equal("ctr_001", results[0].ContractId);
            Assert.Equal("session1", results[0].SessionId);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ContractStore_QueryBySessionId_FiltersCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var store = new ContractStore(tempDir, NullLogger<ContractStore>.Instance);

            store.Append(new ContractExecutionSnapshot
            {
                ContractId = "ctr_a",
                SessionId = "session1",
                StartedAtUtc = DateTimeOffset.UtcNow
            });
            store.Append(new ContractExecutionSnapshot
            {
                ContractId = "ctr_b",
                SessionId = "session2",
                StartedAtUtc = DateTimeOffset.UtcNow
            });

            var session1Results = store.Query(sessionId: "session1");
            var session2Results = store.Query(sessionId: "session2");
            var allResults = store.Query();

            Assert.Single(session1Results);
            Assert.Single(session2Results);
            Assert.Equal(2, allResults.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // === Budget Middleware ===

    [Fact]
    public async Task TokenBudgetMiddleware_CostExceeded_ShortCircuits()
    {
        var middleware = new TokenBudgetMiddleware(
            maxTokensPerSession: 0, // unlimited tokens
            logger: null,
            costChecker: (_, _) => (1.00m, 1.50m, true)); // cost exceeded

        var context = new MessageContext { ChannelId = "ws", SenderId = "user1", Text = "hello" };
        var nextCalled = false;

        await middleware.InvokeAsync(context, () => { nextCalled = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.True(context.IsShortCircuited);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task TokenBudgetMiddleware_NoCostChecker_PassesThrough()
    {
        var middleware = new TokenBudgetMiddleware(
            maxTokensPerSession: 0,
            logger: null,
            costChecker: null);

        var context = new MessageContext { ChannelId = "ws", SenderId = "user1", Text = "hello" };
        var nextCalled = false;

        await middleware.InvokeAsync(context, () => { nextCalled = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.False(context.IsShortCircuited);
        Assert.True(nextCalled);
    }
}
