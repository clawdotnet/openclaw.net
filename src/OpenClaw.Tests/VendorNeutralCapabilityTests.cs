using System.Reflection;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Skills.Meta;
using NSubstitute;
using Xunit;
namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class VendorNeutralCapabilityTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Probe(string name = "weather") : ITool
    {
        public int Calls;
        public bool Fail;
        public string Name => name;
        public string Description => "weather city";
        public string ParameterSchema => "{\"type\":\"object\"}";
        public ValueTask<string> ExecuteAsync(string args, CancellationToken ct)
        { Calls++; if (Fail) throw new ToolOutcomeException("failed", "failed", "weather_failed", "failed"); return ValueTask.FromResult("local weather"); }
    }
    private sealed class Provider(Probe tool, string id = "local") : ICapabilityProvider
    {
        public int Searches, Binds;
        public TaskCompletionSource? Pause;
        public string Id => id;
        public Task<IReadOnlyList<CapabilityCandidate>> DiscoverAsync(ResolveCapabilityRequest request, CancellationToken ct)
        { Searches++; return Task.FromResult<IReadOnlyList<CapabilityCandidate>>([new("weather", "weather city", 1)]); }
        public async Task<CapabilityTarget?> BindAsync(string target, string? name, CancellationToken ct)
        { Binds++; if (Pause is not null) await Pause.Task.WaitAsync(ct); return new("weather", tool); }
    }
    private static MetaCapabilityRefDefinition Ref(string binding = "dynamic", string provider = "local") => new()
    {
        Provider = provider,
        Binding = binding,
        Static = binding == "static" ? new() { Target = "weather", ToolName = "weather" } : null,
        Intent = binding == "dynamic" ? new() { TaskDescription = "weather", Keywords = ["city"] } : null
    };
    [Theory]
    [InlineData(false, "static")]
    [InlineData(true, "static")]
    [InlineData(false, "dynamic")]
    [InlineData(true, "dynamic")]
    public async Task LocalProvider_BothRuntimes_ExecuteWithoutNacos_AndRecheckPolicyOnCacheHit(bool maf, string mode)
    {
        var tool = new Probe();
        var providers = new CapabilityProviderRegistry([new LocalCapabilityProvider(() => [tool])]);
        var skill = new SkillDefinition
        {
            Name = "local-weather",
            Description = "weather",
            Instructions = "",
            Location = "test",
            Kind = SkillKind.Meta,
            FinalTextMode = "step:query",
            Composition = new() { Steps = [new() { Id = "query", Kind = "tool_call", CapabilityRef = Ref(mode), ToolArgsJson = "{}" }] }
        };
        var path = Path.Join(Path.GetTempPath(), "capability-local-" + Guid.NewGuid().ToString("N"));
        using var memory = new FileMemoryStore(path, 4);
        var (runtime, chat, execution) = CapabilityRuntimeTestFactory.Create(maf, [tool], memory, skill, new GatewayConfig { Memory = new() { StoragePath = path } }, new(providers, new()));
        try
        {
            var session = new Session { Id = "s", SenderId = "user", ChannelId = "test" };
            var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal("local weather", await (Task<string>)method.Invoke(runtime, [session, skill.Name, "", TestContext.Current.CancellationToken])!);
            Assert.Equal(1, tool.Calls);
            Assert.Equal("local", session.MetaRunHistory.Single().StepResults.Single().ExecutionEvidence!.CapabilityBinding!.Provider);
            session.RouteAllowedTools = ["resolve_capability"];
            await (Task<string>)method.Invoke(runtime, [session, skill.Name, "", TestContext.Current.CancellationToken])!;
            Assert.Equal(1, tool.Calls); // cached binding is not cached permission
            Assert.Equal("preset_blocked", session.MetaRunHistory.Last().StepResults.Single().FailureCode);
            Assert.True(session.MetaRunHistory.Last().StepResults.Single().ExecutionEvidence!.CapabilityBinding!.CacheHit);
            Assert.Empty(chat.ReceivedCalls()); Assert.Empty(execution.ReceivedCalls());
        }
        finally
        {
            if (runtime is IAsyncDisposable a) await a.DisposeAsync(); else if (runtime is IDisposable d) d.Dispose();
            memory.Dispose(); Directory.Delete(path, true);
        }
    }
    [Fact]
    public async Task Invalidation_RejectsAnInflightBinding_AndForcesRebind()
    {
        var provider = new Provider(new()) { Pause = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = new CapabilityBindingCache();
        var executor = new CapabilitySlotExecutor(new([provider]), cache);
        var pending = executor.ExecuteAsync(Ref(), "{}", "s", TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.Binds);
        cache.Invalidate(new("local", "workspace", "2"));
        provider.Pause.SetResult();
        Assert.Equal("capability_stale_binding", (await pending).FailureCode);
        Assert.Equal(0, cache.Count);
        await executor.ExecuteAsync(Ref(), "{}", "s", TestContext.Current.CancellationToken);
        Assert.Equal(2, provider.Binds);
    }
    [Fact]
    public async Task Cache_IsScopedByProviderAndSecurity_AndConcurrentRequestsShareBinding()
    {
        var a = new Provider(new(), "a"); var b = new Provider(new(), "b");
        var executor = new CapabilitySlotExecutor(new([a, b]), new());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => executor.ExecuteAsync(Ref(provider: "a"), "{}", "s", TestContext.Current.CancellationToken)));
        Assert.Equal(1, a.Binds);
        await executor.ExecuteAsync(Ref(provider: "b"), "{}", "s", TestContext.Current.CancellationToken);
        await executor.ExecuteAsync(Ref(provider: "a"), "{}", "s", TestContext.Current.CancellationToken, securityScope: "other-user");
        Assert.Equal(2, a.Binds); Assert.Equal(1, b.Binds);
    }
    [Fact]
    public async Task Circuit_OpensAfterFailures_RecoversAfterClockAdvance_UnknownToolsAreNotRetrySafe()
    {
        var clock = new Clock(); var tool = new Probe { Fail = true };
        var executor = new CapabilitySlotExecutor(new([new Provider(tool)]), new(), clock);
        for (var i = 0; i < 3; i++) Assert.False((await executor.ExecuteAsync(Ref(), "{}", "s", TestContext.Current.CancellationToken)).RetrySafe);
        Assert.Equal("capability_circuit_open", (await executor.ExecuteAsync(Ref(), "{}", "s", TestContext.Current.CancellationToken)).FailureCode);
        Assert.Equal(3, tool.Calls);
        clock.Now += TimeSpan.FromSeconds(31); tool.Fail = false;
        Assert.Equal("completed", (await executor.ExecuteAsync(Ref(), "{}", "s", TestContext.Current.CancellationToken)).ResultStatus);
        Assert.Equal(4, tool.Calls);
    }
    [Fact]
    public void Cache_ExpiresBoundsStorageAndAvoidsDelimiterCollisions()
    {
        var clock = new Clock(); var cache = new CapabilityBindingCache(TimeSpan.FromSeconds(5), clock, capacity: 2);
        cache.Set("s", "1", "a", "b"); cache.Set("s", "2", "a", "b"); cache.Set("s", "3", "a", "b");
        Assert.Equal(2, cache.Count);
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.False(cache.TryGet("s", "3", out _, out _));
        Assert.NotEqual(CapabilityBindingCache.ComputeIntentKey("a\nb", "c", "first"), CapabilityBindingCache.ComputeIntentKey("a", "b\nc", "first"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledTools_PreventProviderDiscoveryAndBinding(bool maf)
    {
        var tool = new Probe(); var provider = new Provider(tool);
        var skill = new SkillDefinition
        {
            Name = "denied",
            Description = "test",
            Instructions = "",
            Location = "test",
            Kind = SkillKind.Meta,
            Composition = new() { Steps = [new() { Id = "query", Kind = "tool_call", CapabilityRef = Ref(), ToolArgsJson = "{}" }] }
        };
        var path = Path.Join(Path.GetTempPath(), "capability-denied-" + Guid.NewGuid().ToString("N"));
        using var memory = new FileMemoryStore(path, 4);
        var (runtime, _, _) = CapabilityRuntimeTestFactory.Create(maf, [tool], memory, skill,
            new GatewayConfig { Memory = new() { StoragePath = path } }, new(new([provider]), new()));
        try
        {
            var session = new Session { Id = "s", SenderId = "user", ChannelId = "test", RouteAllowedTools = ["some_other_tool"] };
            var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task<string>)method.Invoke(runtime, [session, skill.Name, "", TestContext.Current.CancellationToken])!;
            Assert.Equal(0, provider.Searches); Assert.Equal(0, provider.Binds); Assert.Equal(0, tool.Calls);
            Assert.Equal("preset_blocked", session.MetaRunHistory.Single().StepResults.Single().FailureCode);
        }
        finally
        {
            if (runtime is IAsyncDisposable a) await a.DisposeAsync(); else if (runtime is IDisposable d) d.Dispose();
            memory.Dispose(); Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task Replay_UsesIndependentSnapshot_AndDetectsChangedExpectation()
    {
        var result = await new CapabilitySlotExecutor(new([new Provider(new())]), new())
            .ExecuteAsync(Ref(), "{}", "s", TestContext.Current.CancellationToken);
        var observed = result.BindingTrajectory!;
        var expected = System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(observed, CoreJsonContext.Default.CapabilityBindingTrajectory),
            CoreJsonContext.Default.CapabilityBindingTrajectory)!;
        var fixture = new OpenClaw.Testing.CapabilityBindingReplayFixture { Recorded = observed, Expected = expected, SessionId = "s" };
        Assert.True((await new OpenClaw.Testing.CapabilityBindingReplay(fixture).RunAsync(TestContext.Current.CancellationToken)).Passed);
        expected.SchemaFingerprint = "changed";
        var replay = await new OpenClaw.Testing.CapabilityBindingReplay(fixture).RunAsync(TestContext.Current.CancellationToken);
        Assert.False(replay.Passed);
        Assert.Contains("schemaFingerprint", replay.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[null]")]
    [InlineData("[42]")]
    [InlineData("[\"\"]")]
    public async Task MalformedKeywords_AreRejectedByToolAndSkillLoader(string keywords)
    {
        var args = "{\"task_description\":\"weather\",\"keywords\":" + keywords + "}";
        var exception = await Assert.ThrowsAsync<ToolOutcomeException>(async () =>
            await new ResolveCapabilityTool(new([])).ExecuteAsync(args, TestContext.Current.CancellationToken));
        Assert.Equal("invalid_capability_request", exception.FailureCode);
        var composition = "{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"capability_ref\":{\"binding\":\"dynamic\",\"intent\":" + args + "}}]}";
        var content = "---\nname: invalid\ndescription: test\nkind: meta\ncomposition: " + composition + "\n---\ntest";
        Assert.False(SkillLoader.TryParseSkillContent(content, "/skills/invalid", SkillSource.Workspace, out _, out var error));
        Assert.Equal("invalid_capability_ref", error);
    }

    [Theory]
    [InlineData("static")]
    [InlineData("dynamic")]
    [InlineData("unsupported")]
    public async Task ProgrammaticReference_RequiresModePayloadBeforeBinding(string mode)
    {
        var provider = new Provider(new());
        var executor = new CapabilitySlotExecutor(new([provider]), new());
        var result = await executor.ExecuteAsync(new() { Binding = mode }, "{}", "s", TestContext.Current.CancellationToken);
        Assert.Equal("invalid_capability_ref", result.FailureCode);
        Assert.Equal(0, provider.Binds);
        Assert.Equal(0, provider.Searches);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkillProvider_IsTrimmed_AndCapabilityAllowlistIsRejected(bool allowlist)
    {
        var extra = allowlist ? ",\"tool_allowlist\":[\"weather\"]" : "";
        var composition = "{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\"" + extra
            + ",\"capability_ref\":{\"provider\":\" local \",\"binding\":\"static\",\"static\":{\"target\":\"weather\",\"tool_name\":\"weather\"}}}]}";
        var content = "---\nname: test\ndescription: test\nkind: meta\ncomposition: " + composition + "\n---\ntest";
        var parsed = SkillLoader.TryParseSkillContent(content, "/skills/test", SkillSource.Workspace, out var skill, out var error);
        Assert.Equal(!allowlist, parsed);
        if (allowlist) Assert.Equal("invalid_capability_ref", error);
        else Assert.Equal("local", skill!.Composition!.Steps.Single().CapabilityRef!.Provider);
    }

    [Theory]
    [InlineData("")]
    [InlineData("broken")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"city\":42}")]
    public void RouterFixture_InvalidParametersHaveProtocolError(string input)
    {
        var state = new NacosRouterFixtureState();
        var tool = new FakeNacosRouterMcpTools(state);
        Assert.True(tool.Use("weather-mcp", "get_weather", input).IsError);
        state.PlainTextFailure = true;
        Assert.False(tool.Use("weather-mcp", "get_weather", input).IsError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_PreservesPersistedBindingEvidenceAndReplay(bool maf)
    {
        var tool = new Probe();
        var skill = new SkillDefinition
        {
            Name = "resume-weather", Description = "weather", Instructions = "", Location = "test",
            Kind = SkillKind.Meta, FinalTextMode = "step:query",
            Composition = new() { Steps = [
                new() { Id = "query", Kind = "tool_call", CapabilityRef = Ref(), ToolArgsJson = "{}" },
                new() { Id = "ask", Kind = "user_input", DependsOn = ["query"], WithJson = "{\"prompt\":\"Continue?\"}" }
            ] }
        };
        var path = Path.Join(Path.GetTempPath(), "capability-resume-" + Guid.NewGuid().ToString("N"));
        using var memory = new FileMemoryStore(path, 4);
        var (runtime, _, _) = CapabilityRuntimeTestFactory.Create(maf, [tool], memory, skill,
            new GatewayConfig { Memory = new() { StoragePath = path } }, new(new([new Provider(tool)]), new()));
        try
        {
            var session = new Session { Id = "s", SenderId = "user", ChannelId = "test" };
            var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task<string>)method.Invoke(runtime, [session, skill.Name, "", TestContext.Current.CancellationToken])!;
            Assert.NotNull(session.MetaExecutionCheckpoint);
            session = System.Text.Json.JsonSerializer.Deserialize(
                System.Text.Json.JsonSerializer.Serialize(session, CoreJsonContext.Default.Session), CoreJsonContext.Default.Session)!;
            await (Task<string>)method.Invoke(runtime, [session, skill.Name, "yes", TestContext.Current.CancellationToken])!;
            Assert.Null(session.MetaExecutionCheckpoint);
            Assert.Equal(1, tool.Calls);
            var fixture = OpenClaw.Testing.CapabilityBindingReplayFixture.FromMetaRun(session.MetaRunHistory.Last(), session.Id);
            Assert.Equal("local", fixture.Recorded.Provider);
            Assert.True((await new OpenClaw.Testing.CapabilityBindingReplay(fixture).RunAsync(TestContext.Current.CancellationToken)).Passed);
        }
        finally
        {
            if (runtime is IAsyncDisposable a) await a.DisposeAsync(); else if (runtime is IDisposable d) d.Dispose();
            memory.Dispose(); Directory.Delete(path, true);
        }
    }

    [Theory]
    [InlineData(false, "metadata")]
    [InlineData(true, "metadata")]
    [InlineData(false, "approval")]
    [InlineData(true, "approval")]
    [InlineData(false, "hook")]
    [InlineData(true, "hook")]
    [InlineData(false, "journal")]
    [InlineData(true, "journal")]
    [InlineData(true, "delegation")]
    public async Task CapabilityTargets_RespectRuntimeAndSkillContracts(bool maf, string scenario)
    {
        var tool = new Probe(scenario is "approval" or "hook" ? "capability:nacos:weather-mcp:get_weather" : "weather");
        var skill = new SkillDefinition
        {
            Name = "guarded", Description = "test", Instructions = "", Location = "test", Kind = SkillKind.Meta,
            Composition = new() { Steps = [new() { Id = "query", Kind = "tool_call", CapabilityRef = Ref(), ToolArgsJson = "{}", TimeoutSeconds = 2 }] }
        };
        if (scenario == "metadata") skill.Metadata.Capabilities = ["tool:emit_text"];
        var path = Path.Join(Path.GetTempPath(), "capability-guard-" + Guid.NewGuid().ToString("N"));
        using var memory = new FileMemoryStore(path, 4);
        var config = new GatewayConfig { Memory = new() { StoragePath = path } };
        config.Tooling.DurableActionJournal = scenario == "journal";
        config.Tooling.RequireToolApproval = scenario == "approval";
        config.Tooling.ApprovalRequiredTools = [tool.Name];
        if (scenario == "delegation")
        {
            config.Delegation.Enabled = true;
            config.Delegation.Profiles["helper"] = new() { Name = "helper" };
        }
        var hook = Substitute.For<IToolHook>();
        hook.Name.Returns("deny-weather");
        hook.BeforeExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(call.ArgAt<string>(0) != tool.Name));
        var (runtime, _, _) = CapabilityRuntimeTestFactory.Create(maf, [tool], memory, skill, config,
            new(new([new Provider(tool)]), new()), scenario == "hook" ? [hook] : []);
        try
        {
            var session = new Session { Id = "s", SenderId = "user", ChannelId = "test" };
            var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await ((Task<string>)method.Invoke(runtime, [session, skill.Name, "", TestContext.Current.CancellationToken])!).WaitAsync(TimeSpan.FromSeconds(5));
            var step = session.MetaRunHistory.Last().StepResults.Single();
            if (scenario is "journal" or "delegation")
            {
                Assert.Equal("completed", step.Status);
                Assert.Equal(1, tool.Calls);
                if (scenario == "journal")
                {
                    await memory.SaveSessionAsync(session, TestContext.Current.CancellationToken);
                    var journal = new OpenClaw.Core.Actions.DurableActionJournal(path);
                    await journal.AcknowledgePersistedHistoryAsync(session, TestContext.Current.CancellationToken);
                    using (var lease = await journal.OpenAsync(session.Id, TestContext.Current.CancellationToken))
                        Assert.True(Assert.Single(lease.Records).HistoryPersisted);
                    // A new invocation must not reuse the prior call ID and cached result.
                    await (Task<string>)method.Invoke(runtime, [session, skill.Name, "", TestContext.Current.CancellationToken])!;
                    Assert.Equal(2, tool.Calls);
                }
            }
            else
            {
                Assert.Equal(0, tool.Calls);
                Assert.Equal("blocked", step.Status);
                Assert.Equal(scenario == "metadata" ? "metadata_capability_denied" : scenario == "approval" ? "approval_required" : "tool_failed", step.FailureCode);
                if (scenario == "metadata")
                {
                    for (var i = 0; i < 2; i++)
                        await (Task<string>)method.Invoke(runtime, [session, skill.Name, "", TestContext.Current.CancellationToken])!;
                    skill.Metadata.Capabilities = [];
                    await (Task<string>)method.Invoke(runtime, [session, skill.Name, "", TestContext.Current.CancellationToken])!;
                    Assert.Equal("completed", session.MetaRunHistory.Last().StepResults.Single().Status);
                    Assert.Equal(1, tool.Calls);
                }
            }
        }
        finally
        {
            if (runtime is IAsyncDisposable a) await a.DisposeAsync(); else if (runtime is IDisposable d) d.Dispose();
            memory.Dispose(); Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task DifferentSessions_DoNotWaitForAnotherBinding()
    {
        var slow = new Provider(new(), "slow") { Pause = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var fast = new Provider(new(), "fast");
        var executor = new CapabilitySlotExecutor(new([slow, fast]), new());
        var pending = executor.ExecuteAsync(Ref(provider: "slow"), "{}", "a", TestContext.Current.CancellationToken);
        try
        {
            var result = await executor.ExecuteAsync(Ref(provider: "fast"), "{}", "b", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("completed", result.ResultStatus);
        }
        finally { slow.Pause.SetResult(); await pending; }
    }

    [Fact]
    public async Task CircuitPressure_DoesNotClearOpenCooldowns()
    {
        var tool = new Probe { Fail = true };
        var executor = new CapabilitySlotExecutor(new([new Provider(tool)]), new());
        for (var i = 0; i < 3; i++) await executor.ExecuteAsync(Ref(), "{}", "protected", TestContext.Current.CancellationToken);
        for (var i = 0; i < 1030; i++) await executor.ExecuteAsync(Ref(), "{}", "s" + i, TestContext.Current.CancellationToken);
        Assert.Equal("capability_circuit_open", (await executor.ExecuteAsync(Ref(), "{}", "protected", TestContext.Current.CancellationToken)).FailureCode);
    }

    [Fact]
    public async Task ExactName_FiltersBeforeLimit_AndDiscoveryHonorsToolPolicy()
    {
        ITool Named(string name)
        {
            var tool = Substitute.For<ITool>();
            tool.Name.Returns(name); tool.Description.Returns("weather"); tool.ParameterSchema.Returns("{}");
            return tool;
        }
        var tools = new[] { "a_weather", "b_weather", "c_weather", "d_weather", "e_weather", "weather" }.Select(Named).ToArray();
        var registry = new CapabilityProviderRegistry([new LocalCapabilityProvider(() => tools)]);
        var request = new ResolveCapabilityRequest("weather", null, ResolveCapabilitySelectionPolicy.ExactName);
        Assert.Equal("weather", (await registry.ResolveAsync(request, TestContext.Current.CancellationToken)).Binding!.Tool);
        var hidden = await registry.ResolveAsync(request, TestContext.Current.CancellationToken, name => name != "weather");
        Assert.Null(hidden.Binding);
        Assert.DoesNotContain(hidden.Candidates, c => c.Name == "weather");
    }

    [Fact]
    public async Task Replay_DetectsChangedRecordedIntent_AndRetainsType()
    {
        var reference = new MetaCapabilityRefDefinition { Binding = "dynamic", Intent = new() { TaskDescription = "weather", Type = "weather-service" } };
        var result = await new CapabilitySlotExecutor(new([new Provider(new())]), new()).ExecuteAsync(reference, "{}", "s", TestContext.Current.CancellationToken);
        var expected = System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(result.BindingTrajectory, CoreJsonContext.Default.CapabilityBindingTrajectory), CoreJsonContext.Default.CapabilityBindingTrajectory)!;
        var fixture = new OpenClaw.Testing.CapabilityBindingReplayFixture { Recorded = result.BindingTrajectory!, Expected = expected, SessionId = "s" };
        Assert.True((await new OpenClaw.Testing.CapabilityBindingReplay(fixture).RunAsync(TestContext.Current.CancellationToken)).Passed);
        fixture.Recorded.TaskDescription = "changed";
        var replay = await new OpenClaw.Testing.CapabilityBindingReplay(fixture).RunAsync(TestContext.Current.CancellationToken);
        Assert.False(replay.Passed);
        Assert.Contains("intentKey", replay.Message);
    }
}
