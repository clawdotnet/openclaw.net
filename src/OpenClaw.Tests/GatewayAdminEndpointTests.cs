using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Client;
using OpenClaw.Companion.Services;
using OpenClaw.Companion.ViewModels;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Features;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Backends;
using OpenClaw.Gateway.Bootstrap;
using ModelContextProtocol.AspNetCore;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Mcp;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayAdminEndpointTests
{
    [Fact]
    public async Task AuthSession_BearerAndBrowserSessionFlow_Works()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var anonymousResponse = await harness.Client.GetAsync("/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var bearerRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/session");
        bearerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var bearerResponse = await harness.Client.SendAsync(bearerRequest);
        Assert.Equal(HttpStatusCode.OK, bearerResponse.StatusCode);
        var bearerPayload = await ReadJsonAsync(bearerResponse);
        Assert.Equal("bearer", bearerPayload.RootElement.GetProperty("authMode").GetString());

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/session")
        {
            Content = JsonContent("""{"remember":true}""")
        };
        loginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var loginResponse = await harness.Client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginPayload = await ReadJsonAsync(loginResponse);
        Assert.Equal("browser-session", loginPayload.RootElement.GetProperty("authMode").GetString());
        var csrfToken = loginPayload.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));
        var cookie = Assert.Single(loginResponse.Headers.GetValues("Set-Cookie"));

        using var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/session");
        sessionRequest.Headers.Add("Cookie", cookie);
        var sessionResponse = await harness.Client.SendAsync(sessionRequest);
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        var sessionPayload = await ReadJsonAsync(sessionResponse);
        Assert.Equal("browser-session", sessionPayload.RootElement.GetProperty("authMode").GetString());

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/auth/session");
        deleteRequest.Headers.Add("Cookie", cookie);
        deleteRequest.Headers.Add(BrowserSessionAuthService.CsrfHeaderName, csrfToken);
        var deleteResponse = await harness.Client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task AdminSettings_BrowserSessionMutation_RequiresCsrf()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        var (cookie, csrfToken) = await LoginAsync(harness.Client, harness.AuthToken);

        using var currentSettingsRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/settings");
        currentSettingsRequest.Headers.Add("Cookie", cookie);
        var currentSettingsResponse = await harness.Client.SendAsync(currentSettingsRequest);
        currentSettingsResponse.EnsureSuccessStatusCode();
        using var currentSettings = await ReadJsonAsync(currentSettingsResponse);
        var settingsPayload = currentSettings.RootElement.GetProperty("settings").Clone();
        var settingsDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsPayload.GetRawText(), CoreJsonContext.Default.BridgeDictionaryStringJsonElement)!;
        settingsDict["usageFooter"] = JsonSerializer.SerializeToElement("tokens");

        using var forbiddenRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/settings")
        {
            Content = JsonContent(JsonSerializer.Serialize(settingsDict, CoreJsonContext.Default.BridgeDictionaryStringJsonElement))
        };
        forbiddenRequest.Headers.Add("Cookie", cookie);
        var forbiddenResponse = await harness.Client.SendAsync(forbiddenRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, forbiddenResponse.StatusCode);

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/settings")
        {
            Content = JsonContent(JsonSerializer.Serialize(settingsDict, CoreJsonContext.Default.BridgeDictionaryStringJsonElement))
        };
        allowedRequest.Headers.Add("Cookie", cookie);
        allowedRequest.Headers.Add(BrowserSessionAuthService.CsrfHeaderName, csrfToken);
        var allowedResponse = await harness.Client.SendAsync(allowedRequest);
        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        var payload = await ReadJsonAsync(allowedResponse);
        Assert.Equal("tokens", payload.RootElement.GetProperty("settings").GetProperty("usageFooter").GetString());
    }

    [Fact]
    public async Task AdminPosture_ReportsPublicBindRisks()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/posture");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        Assert.True(payload.RootElement.GetProperty("publicBind").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("requireRequesterMatchForHttpToolApproval").GetBoolean());
        Assert.Contains(
            payload.RootElement.GetProperty("riskFlags").EnumerateArray().Select(static item => item.GetString()).OfType<string>(),
            flag => flag == "public_bind_admin_override_tool_approval");
    }

    [Fact]
    public async Task ApprovalSimulation_ReturnsRequiresApprovalForEffectiveToolPolicy()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/approvals/simulate")
        {
            Content = JsonContent("""{"toolName":"shell","autonomyMode":"full","requireToolApproval":true,"approvalRequiredTools":["shell"]}""")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        Assert.Equal("requires_approval", payload.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task ApprovalSimulation_NormalizesToolAliasForAutonomyChecks()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true, config =>
        {
            config.Tooling.ReadOnlyMode = true;
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/approvals/simulate")
        {
            Content = JsonContent("""{"toolName":"file_write","autonomyMode":"readonly","requireToolApproval":false}""")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        Assert.Equal("deny", payload.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task IncidentExport_RedactsSensitiveRuntimeEventContent()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        harness.Runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = "evt_sensitive",
            Component = "test",
            Action = "sensitive",
            Severity = "warning",
            Summary = "raw:super-secret-token",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["authorization"] = "Bearer abc123"
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/incident/export");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        var runtimeEvent = payload.RootElement.GetProperty("runtimeEvents").EnumerateArray()
            .First(item => item.GetProperty("id").GetString() == "evt_sensitive");
        Assert.DoesNotContain("super-secret-token", runtimeEvent.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal("[redacted]", runtimeEvent.GetProperty("metadata").GetProperty("authorization").GetString());
    }

    [Fact]
    public async Task Allowlists_DiscordChannel_UsesConfiguredAllowlist()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true, config =>
        {
            config.Channels.Discord.AllowedFromUserIds = ["discord-user"];
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/allowlists/discord");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        using var payload = await ReadJsonAsync(response);
        var allowed = payload.RootElement.GetProperty("effective").GetProperty("allowedFrom").EnumerateArray().Select(static item => item.GetString()).OfType<string>().ToArray();
        Assert.Single(allowed);
        Assert.Equal("discord-user", allowed[0]);
    }

    [Fact]
    public async Task DoctorText_ListsAllChannelAllowlists()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true, config =>
        {
            config.Channels.Teams.AllowedFromIds = ["teams-user"];
            config.Channels.Slack.AllowedFromUserIds = ["slack-user"];
            config.Channels.Discord.AllowedFromUserIds = ["discord-user"];
            config.Channels.Signal.AllowedFromNumbers = ["+15551230000"];
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/doctor/text");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("- teams:", text, StringComparison.Ordinal);
        Assert.Contains("- slack:", text, StringComparison.Ordinal);
        Assert.Contains("- discord:", text, StringComparison.Ordinal);
        Assert.Contains("- signal:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LearningProposalDetail_ProfileUpdate_IncludesDiffAndProvenance()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        var store = new FileFeatureStore(harness.StoragePath);
        var actorId = "telegram:detail-user";

        await store.SaveProfileAsync(new UserProfile
        {
            ActorId = actorId,
            ChannelId = "telegram",
            SenderId = "detail-user",
            Summary = "Prefers terse updates.",
            Tone = "concise",
            Preferences = ["terse"],
            RecentIntents = ["status"],
            Facts =
            [
                new UserProfileFact
                {
                    Key = "style",
                    Value = "terse",
                    Confidence = 0.6f,
                    SourceSessionIds = ["sess-prev"]
                }
            ]
        }, CancellationToken.None);

        await store.SaveProposalAsync(new LearningProposal
        {
            Id = "lp_detail_profile",
            Kind = LearningProposalKind.ProfileUpdate,
            Status = LearningProposalStatus.Pending,
            ActorId = actorId,
            Title = "Profile update suggestion",
            Summary = "Detected a style preference change.",
            ProfileUpdate = new UserProfile
            {
                ActorId = actorId,
                ChannelId = "telegram",
                SenderId = "detail-user",
                Summary = "Prefers terse updates and weekly summaries.",
                Tone = "concise",
                Preferences = ["terse", "weekly-digest"],
                RecentIntents = ["status", "digest"],
                Facts =
                [
                    new UserProfileFact
                    {
                        Key = "style",
                        Value = "terse",
                        Confidence = 0.7f,
                        SourceSessionIds = ["sess-new"]
                    }
                ]
            },
            SourceSessionIds = ["sess-1", "sess-2"],
            Confidence = 0.72f
        }, CancellationToken.None);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/learning/proposals/lp_detail_profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        Assert.Equal("lp_detail_profile", payload.RootElement.GetProperty("proposal").GetProperty("id").GetString());
        Assert.Equal("Prefers terse updates.", payload.RootElement.GetProperty("baselineProfile").GetProperty("summary").GetString());
        Assert.Equal("Prefers terse updates.", payload.RootElement.GetProperty("currentProfile").GetProperty("summary").GetString());
        Assert.False(payload.RootElement.GetProperty("canRollback").GetBoolean());
        Assert.Equal(0.72f, payload.RootElement.GetProperty("provenance").GetProperty("confidence").GetSingle());

        var diff = payload.RootElement.GetProperty("profileDiff").EnumerateArray().ToArray();
        Assert.Contains(diff, entry => entry.GetProperty("path").GetString() == "summary" &&
                                       entry.GetProperty("before").GetString() == "Prefers terse updates." &&
                                       entry.GetProperty("after").GetString() == "Prefers terse updates and weekly summaries.");
        Assert.Contains(diff, entry => entry.GetProperty("path").GetString() == "preferences" &&
                                       entry.GetProperty("before").GetString()!.Contains("terse", StringComparison.Ordinal) &&
                                       entry.GetProperty("after").GetString()!.Contains("weekly-digest", StringComparison.Ordinal));
        Assert.Contains(diff, entry => entry.GetProperty("path").GetString() == "facts" &&
                                       entry.GetProperty("before").GetString()!.Contains("confidence:0.6", StringComparison.Ordinal) &&
                                       entry.GetProperty("after").GetString()!.Contains("confidence:0.7", StringComparison.Ordinal));
        var sourceSessions = payload.RootElement.GetProperty("provenance").GetProperty("sourceSessionIds").EnumerateArray().Select(static item => item.GetString()).OfType<string>().ToArray();
        Assert.Equal(["sess-1", "sess-2"], sourceSessions);
    }

    [Fact]
    public async Task LearningProposalRollback_ProfileUpdate_RestoresPreviousProfile()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        var store = new FileFeatureStore(harness.StoragePath);
        var actorId = "slack:rollback-user";
        var beforeProfile = new UserProfile
        {
            ActorId = actorId,
            ChannelId = "slack",
            SenderId = "rollback-user",
            Summary = "Original profile",
            Tone = "friendly",
            Preferences = ["plain-text"]
        };
        var updatedProfile = new UserProfile
        {
            ActorId = actorId,
            ChannelId = "slack",
            SenderId = "rollback-user",
            Summary = "Updated profile",
            Tone = "friendly",
            Preferences = ["plain-text", "charts"]
        };

        await store.SaveProfileAsync(updatedProfile, CancellationToken.None);
        await store.SaveProposalAsync(new LearningProposal
        {
            Id = "lp_rollback_profile",
            Kind = LearningProposalKind.ProfileUpdate,
            Status = LearningProposalStatus.Approved,
            ActorId = actorId,
            Title = "Profile update suggestion",
            Summary = "Approved change.",
            ProfileUpdate = updatedProfile,
            AppliedProfileBefore = beforeProfile,
            SourceSessionIds = ["sess-rollback"],
            Confidence = 0.8f,
            ReviewedAtUtc = DateTimeOffset.UtcNow,
            ReviewNotes = "approved"
        }, CancellationToken.None);

        using var rollbackRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/learning/proposals/lp_rollback_profile/rollback")
        {
            Content = JsonContent("""{"reason":"revert noisy preference"}""")
        };
        rollbackRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var rollbackResponse = await harness.Client.SendAsync(rollbackRequest);

        Assert.Equal(HttpStatusCode.OK, rollbackResponse.StatusCode);
        using var rollbackPayload = await ReadJsonAsync(rollbackResponse);
        Assert.Equal("rolled_back", rollbackPayload.RootElement.GetProperty("status").GetString());
        Assert.True(rollbackPayload.RootElement.GetProperty("rolledBack").GetBoolean());
        Assert.Equal("revert noisy preference", rollbackPayload.RootElement.GetProperty("rollbackReason").GetString());

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, $"/admin/profiles/{Uri.EscapeDataString(actorId)}");
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var profileResponse = await harness.Client.SendAsync(profileRequest);
        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
        using var profilePayload = await ReadJsonAsync(profileResponse);
        Assert.Equal("Original profile", profilePayload.RootElement.GetProperty("profile").GetProperty("summary").GetString());
    }

    [Fact]
    public async Task AdminProfiles_ExportAndImport_RoundTripsProfilesAndProposals()
    {
        const string actorId = "discord:portable-user";

        await using var sourceHarness = await CreateHarnessAsync(nonLoopbackBind: true);
        var sourceStore = new FileFeatureStore(sourceHarness.StoragePath);
        await sourceStore.SaveProfileAsync(new UserProfile
        {
            ActorId = actorId,
            ChannelId = "discord",
            SenderId = "portable-user",
            Summary = "Portable memory",
            Tone = "direct",
            Preferences = ["summaries"],
            ActiveProjects = ["roadmap"]
        }, CancellationToken.None);
        await sourceStore.SaveProposalAsync(new LearningProposal
        {
            Id = "lp_portable_profile",
            Kind = LearningProposalKind.ProfileUpdate,
            Status = LearningProposalStatus.Pending,
            ActorId = actorId,
            Title = "Portable proposal",
            Summary = "Pending proposal in export bundle.",
            ProfileUpdate = new UserProfile
            {
                ActorId = actorId,
                ChannelId = "discord",
                SenderId = "portable-user",
                Summary = "Portable memory with follow-up cadence",
                Tone = "direct",
                Preferences = ["summaries", "cadence"]
            },
            SourceSessionIds = ["sess-portable"],
            Confidence = 0.66f
        }, CancellationToken.None);

        using var exportRequest = new HttpRequestMessage(HttpMethod.Get, $"/admin/profiles/export?actorId={Uri.EscapeDataString(actorId)}");
        exportRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sourceHarness.AuthToken);
        var exportResponse = await sourceHarness.Client.SendAsync(exportRequest);
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        var exportJson = await exportResponse.Content.ReadAsStringAsync();

        await using var targetHarness = await CreateHarnessAsync(nonLoopbackBind: true);
        using var importRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/profiles/import")
        {
            Content = new StringContent(exportJson, Encoding.UTF8, "application/json")
        };
        importRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetHarness.AuthToken);
        var importResponse = await targetHarness.Client.SendAsync(importRequest);
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        using var importPayload = await ReadJsonAsync(importResponse);
        Assert.True(importPayload.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, importPayload.RootElement.GetProperty("profilesImported").GetInt32());
        Assert.Equal(1, importPayload.RootElement.GetProperty("proposalsImported").GetInt32());

        using var detailRequest = new HttpRequestMessage(HttpMethod.Get, $"/admin/profiles/{Uri.EscapeDataString(actorId)}");
        detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetHarness.AuthToken);
        var detailResponse = await targetHarness.Client.SendAsync(detailRequest);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detailPayload = await ReadJsonAsync(detailResponse);
        Assert.Equal("Portable memory", detailPayload.RootElement.GetProperty("profile").GetProperty("summary").GetString());

        using var proposalRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/learning/proposals/lp_portable_profile");
        proposalRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetHarness.AuthToken);
        var proposalResponse = await targetHarness.Client.SendAsync(proposalRequest);
        Assert.Equal(HttpStatusCode.OK, proposalResponse.StatusCode);
        using var proposalPayload = await ReadJsonAsync(proposalResponse);
        Assert.Equal("lp_portable_profile", proposalPayload.RootElement.GetProperty("proposal").GetProperty("id").GetString());
    }

    [Fact]
    public async Task AdminMemoryNotes_ListSearchSaveAndDelete_Work()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        await harness.MemoryStore.SaveNoteAsync("project:alpha:architecture", "Use NativeAOT for the shipping target.", CancellationToken.None);
        await harness.MemoryStore.SaveNoteAsync("runbook:deploy-checklist", "Confirm doctor and posture before deploy.", CancellationToken.None);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/memory/notes?memoryClass=project_fact&projectId=alpha");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var listResponse = await harness.Client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var listPayload = await ReadJsonAsync(listResponse);
        var listedItems = listPayload.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(listedItems);
        Assert.Equal("project:alpha:architecture", listedItems[0].GetProperty("key").GetString());
        Assert.Equal("architecture", listedItems[0].GetProperty("displayKey").GetString());
        Assert.Equal("project_fact", listedItems[0].GetProperty("memoryClass").GetString());

        using var searchRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/memory/search?query=NativeAOT&memoryClass=project_fact&projectId=alpha");
        searchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var searchResponse = await harness.Client.SendAsync(searchRequest);
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        using var searchPayload = await ReadJsonAsync(searchResponse);
        Assert.Single(searchPayload.RootElement.GetProperty("items").EnumerateArray());

        using var saveRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/memory/notes")
        {
            Content = JsonContent("""{"key":"daily-triage","memoryClass":"approved_automation","content":"Run inbox triage every morning."}""")
        };
        saveRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var saveResponse = await harness.Client.SendAsync(saveRequest);
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        using var savePayload = await ReadJsonAsync(saveResponse);
        var savedNote = savePayload.RootElement.GetProperty("note");
        Assert.Equal("automation:daily-triage", savedNote.GetProperty("key").GetString());
        Assert.Equal("approved_automation", savedNote.GetProperty("memoryClass").GetString());
        Assert.Equal("Run inbox triage every morning.", savedNote.GetProperty("content").GetString());

        using var detailRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/memory/notes/automation%3Adaily-triage");
        detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var detailResponse = await harness.Client.SendAsync(detailRequest);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detailPayload = await ReadJsonAsync(detailResponse);
        Assert.Equal("daily-triage", detailPayload.RootElement.GetProperty("note").GetProperty("displayKey").GetString());

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/admin/memory/notes/automation%3Adaily-triage");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var deleteResponse = await harness.Client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        Assert.Null(await harness.MemoryStore.LoadNoteAsync("automation:daily-triage", CancellationToken.None));
    }

    [Fact]
    public async Task AdminMemoryExportImport_RoundTripsNotesProfilesProposalsAndAutomations()
    {
        const string actorId = "telegram:memory-portable";

        await using var sourceHarness = await CreateHarnessAsync(nonLoopbackBind: true);
        var sourceFeatureStore = new FileFeatureStore(sourceHarness.StoragePath);

        await sourceHarness.MemoryStore.SaveNoteAsync("project:apollo:runbook", "Escalate incidents through the launch room.", CancellationToken.None);
        await sourceFeatureStore.SaveProfileAsync(new UserProfile
        {
            ActorId = actorId,
            ChannelId = "telegram",
            SenderId = "memory-portable",
            Summary = "Portable operator profile",
            Tone = "direct",
            Preferences = ["daily-summary"]
        }, CancellationToken.None);
        await sourceFeatureStore.SaveProposalAsync(new LearningProposal
        {
            Id = "lp_memory_bundle",
            Kind = LearningProposalKind.SkillDraft,
            Status = LearningProposalStatus.Pending,
            ActorId = actorId,
            Title = "Skill draft bundle item",
            Summary = "Draft captured in memory export.",
            SkillName = "incident-followup",
            DraftContent = "---\nname: incident-followup\ndescription: Follow up incidents\n---\nUse after incidents.",
            DraftContentHash = "hash",
            SourceSessionIds = ["sess-memory"],
            Confidence = 0.7f
        }, CancellationToken.None);
        await sourceFeatureStore.SaveAutomationAsync(new AutomationDefinition
        {
            Id = "auto_memory_bundle",
            Name = "Daily memory digest",
            Enabled = false,
            Schedule = "@daily",
            Prompt = "Summarize memory changes.",
            DeliveryChannelId = "cron",
            IsDraft = true,
            Source = "learning",
            TemplateKey = "custom"
        }, CancellationToken.None);

        using var exportRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/memory/export?projectId=apollo");
        exportRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sourceHarness.AuthToken);
        var exportResponse = await sourceHarness.Client.SendAsync(exportRequest);
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        var exportJson = await exportResponse.Content.ReadAsStringAsync();

        await using var targetHarness = await CreateHarnessAsync(nonLoopbackBind: true);
        using var importRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/memory/import")
        {
            Content = new StringContent(exportJson, Encoding.UTF8, "application/json")
        };
        importRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetHarness.AuthToken);
        var importResponse = await targetHarness.Client.SendAsync(importRequest);
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        using var importPayload = await ReadJsonAsync(importResponse);
        Assert.True(importPayload.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, importPayload.RootElement.GetProperty("notesImported").GetInt32());
        Assert.Equal(1, importPayload.RootElement.GetProperty("profilesImported").GetInt32());
        Assert.Equal(1, importPayload.RootElement.GetProperty("proposalsImported").GetInt32());
        Assert.True(importPayload.RootElement.GetProperty("automationsImported").GetInt32() >= 1);

        using var noteRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/memory/notes?memoryClass=project_fact&projectId=apollo");
        noteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetHarness.AuthToken);
        var noteResponse = await targetHarness.Client.SendAsync(noteRequest);
        Assert.Equal(HttpStatusCode.OK, noteResponse.StatusCode);
        using var notePayload = await ReadJsonAsync(noteResponse);
        Assert.Single(notePayload.RootElement.GetProperty("items").EnumerateArray());

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, $"/admin/profiles/{Uri.EscapeDataString(actorId)}");
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetHarness.AuthToken);
        var profileResponse = await targetHarness.Client.SendAsync(profileRequest);
        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);

        using var proposalRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/learning/proposals/lp_memory_bundle");
        proposalRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetHarness.AuthToken);
        var proposalResponse = await targetHarness.Client.SendAsync(proposalRequest);
        Assert.Equal(HttpStatusCode.OK, proposalResponse.StatusCode);

        using var automationRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/automations/auto_memory_bundle");
        automationRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetHarness.AuthToken);
        var automationResponse = await targetHarness.Client.SendAsync(automationRequest);
        Assert.Equal(HttpStatusCode.OK, automationResponse.StatusCode);
        using var automationPayload = await ReadJsonAsync(automationResponse);
        Assert.Equal("Daily memory digest", automationPayload.RootElement.GetProperty("automation").GetProperty("name").GetString());
    }

    [Fact]
    public async Task AdminMemoryEndpoints_RejectInvalidKeys()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        using var detailRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/memory/notes/bad..key");
        detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var detailResponse = await harness.Client.SendAsync(detailRequest);
        Assert.Equal(HttpStatusCode.BadRequest, detailResponse.StatusCode);

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/admin/memory/notes/bad..key");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var deleteResponse = await harness.Client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.BadRequest, deleteResponse.StatusCode);

        using var importRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/memory/import")
        {
            Content = JsonContent("""{"notes":[{"key":"bad..key","displayKey":"bad..key","memoryClass":"general","preview":"bad","content":"bad"}],"profiles":[],"proposals":[],"automations":[]}""")
        };
        importRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var importResponse = await harness.Client.SendAsync(importRequest);
        Assert.Equal(HttpStatusCode.BadRequest, importResponse.StatusCode);
    }

    [Fact]
    public async Task HeartbeatEndpoints_PreviewSaveAndStatus_Work()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        await File.WriteAllTextAsync(Path.Combine(harness.StoragePath, "memory.md"), "Prefer concise summaries.");

        var config = new HeartbeatConfigDto
        {
            Enabled = true,
            CronExpression = "@hourly",
            Timezone = "UTC",
            DeliveryChannelId = "cron",
            DeliverySubject = "Ops heartbeat",
            ModelId = "gpt-4o-mini",
            Tasks =
            [
                new HeartbeatTaskDto
                {
                    Id = "watch-critical-alerts",
                    TemplateKey = "custom",
                    Title = "Watch critical alerts",
                    Instruction = "Only report urgent findings."
                }
            ]
        };

        using var previewRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/heartbeat/preview")
        {
            Content = JsonContent(JsonSerializer.Serialize(config, CoreJsonContext.Default.HeartbeatConfigDto))
        };
        previewRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var previewResponse = await harness.Client.SendAsync(previewRequest);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);

        using var previewPayload = await ReadJsonAsync(previewResponse);
        Assert.True(Path.IsPathRooted(previewPayload.RootElement.GetProperty("configPath").GetString()!));
        Assert.True(Path.IsPathRooted(previewPayload.RootElement.GetProperty("heartbeatPath").GetString()!));
        Assert.True(Path.IsPathRooted(previewPayload.RootElement.GetProperty("memoryMarkdownPath").GetString()!));
        Assert.Equal("gpt-4o-mini", previewPayload.RootElement.GetProperty("costEstimate").GetProperty("modelId").GetString());
        Assert.Equal(0, previewPayload.RootElement.GetProperty("issues").GetArrayLength());

        using var saveRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/heartbeat")
        {
            Content = JsonContent(JsonSerializer.Serialize(config, CoreJsonContext.Default.HeartbeatConfigDto))
        };
        saveRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var saveResponse = await harness.Client.SendAsync(saveRequest);
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        using var savePayload = await ReadJsonAsync(saveResponse);
        var configPath = savePayload.RootElement.GetProperty("configPath").GetString()!;
        var heartbeatPath = savePayload.RootElement.GetProperty("heartbeatPath").GetString()!;
        Assert.True(File.Exists(configPath));
        Assert.True(File.Exists(heartbeatPath));

        var heartbeatMarkdown = await File.ReadAllTextAsync(heartbeatPath);
        Assert.Contains("managed_by: openclaw_heartbeat_wizard", heartbeatMarkdown, StringComparison.Ordinal);
        Assert.Contains("source_hash:", heartbeatMarkdown, StringComparison.Ordinal);

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/heartbeat/status");
        statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var statusResponse = await harness.Client.SendAsync(statusRequest);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        using var statusPayload = await ReadJsonAsync(statusResponse);
        Assert.True(statusPayload.RootElement.GetProperty("configExists").GetBoolean());
        Assert.True(statusPayload.RootElement.GetProperty("heartbeatExists").GetBoolean());
        Assert.Equal(Path.Combine(harness.StoragePath, "memory.md"), statusPayload.RootElement.GetProperty("memoryMarkdownPath").GetString());
        Assert.Equal("cron", statusPayload.RootElement.GetProperty("config").GetProperty("deliveryChannelId").GetString());
    }

    [Fact]
    public async Task HeartbeatPreview_UsesSuggestionsAndCostEstimateVariesBySchedule()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        await harness.MemoryStore.SaveNoteAsync("competitor-watch", "Check https://example.com/status for outages.", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(harness.StoragePath, "memory.md"), "Please keep checking https://example.com/status for major changes.");
        var session = await harness.Runtime.SessionManager.GetOrCreateAsync("websocket", "tester", CancellationToken.None);
        session.History.Add(new ChatTurn
        {
            Role = "user",
            Content = "Please monitor https://example.com/status and /tmp/competitor-alerts for changes."
        });
        await harness.Runtime.SessionManager.PersistAsync(session, CancellationToken.None);

        var dailyConfig = new HeartbeatConfigDto
        {
            Enabled = true,
            CronExpression = "0 9 * * *",
            Timezone = "UTC",
            DeliveryChannelId = "cron",
            ModelId = "gpt-4o-mini",
            Tasks =
            [
                new HeartbeatTaskDto
                {
                    Id = "watch-site",
                    TemplateKey = "website_monitoring",
                    Title = "Watch competitor status page",
                    Target = "https://example.com/status"
                }
            ]
        };

        using var dailyPreviewRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/heartbeat/preview")
        {
            Content = JsonContent(JsonSerializer.Serialize(dailyConfig, CoreJsonContext.Default.HeartbeatConfigDto))
        };
        dailyPreviewRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var dailyPreviewResponse = await harness.Client.SendAsync(dailyPreviewRequest);
        Assert.Equal(HttpStatusCode.OK, dailyPreviewResponse.StatusCode);

        using var dailyPayload = await ReadJsonAsync(dailyPreviewResponse);
        Assert.Contains(
            dailyPayload.RootElement.GetProperty("suggestions").EnumerateArray(),
            item => string.Equals(item.GetProperty("target").GetString(), "https://example.com/status", StringComparison.Ordinal));
        Assert.Contains(
            dailyPayload.RootElement.GetProperty("suggestions").EnumerateArray(),
            item => item.GetProperty("reason").GetString()!.Contains("memory.md", StringComparison.Ordinal));

        var dailyRuns = dailyPayload.RootElement.GetProperty("costEstimate").GetProperty("estimatedRunsPerMonth").GetInt32();

        var hourlyConfig = new HeartbeatConfigDto
        {
            Enabled = true,
            CronExpression = "@hourly",
            Timezone = "UTC",
            DeliveryChannelId = "cron",
            ModelId = "gpt-4o-mini",
            Tasks = dailyConfig.Tasks
        };

        using var hourlyPreviewRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/heartbeat/preview")
        {
            Content = JsonContent(JsonSerializer.Serialize(hourlyConfig, CoreJsonContext.Default.HeartbeatConfigDto))
        };
        hourlyPreviewRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var hourlyPreviewResponse = await harness.Client.SendAsync(hourlyPreviewRequest);
        Assert.Equal(HttpStatusCode.OK, hourlyPreviewResponse.StatusCode);

        using var hourlyPayload = await ReadJsonAsync(hourlyPreviewResponse);
        var hourlyRuns = hourlyPayload.RootElement.GetProperty("costEstimate").GetProperty("estimatedRunsPerMonth").GetInt32();

        Assert.True(hourlyRuns > dailyRuns);
    }

    [Fact]
    public async Task HeartbeatSave_InvalidConfig_ReturnsBadRequest()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var invalidConfig = new HeartbeatConfigDto
        {
            Enabled = true,
            CronExpression = "not-a-cron",
            Timezone = "Mars/Phobos",
            DeliveryChannelId = "telegram",
            Tasks = []
        };

        using var saveRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/heartbeat")
        {
            Content = JsonContent(JsonSerializer.Serialize(invalidConfig, CoreJsonContext.Default.HeartbeatConfigDto))
        };
        saveRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var saveResponse = await harness.Client.SendAsync(saveRequest);

        Assert.Equal(HttpStatusCode.BadRequest, saveResponse.StatusCode);
        using var payload = await ReadJsonAsync(saveResponse);
        Assert.NotEqual(0, payload.RootElement.GetProperty("issues").GetArrayLength());
    }

    [Fact]
    public async Task AdminSettings_Mutation_RejectsOversizedPayload()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var oversizedFooter = new string('x', 300_000);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/settings")
        {
            Content = JsonContent($$"""{"usageFooter":"{{oversizedFooter}}"}""")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);

        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task ChatCompletions_RequestTooLarge_Returns413()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var oversizedPrompt = new string('x', 1024 * 1024);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent($$"""{"messages":[{"role":"user","content":"{{oversizedPrompt}}"}]}""")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);

        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("Request too large.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChatCompletions_StableSession_DoesNotDuplicatePersistedHistory()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        harness.Runtime.AgentRuntime.RunAsync(
                Arg.Any<Session>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ToolApprovalCallback?>(),
                Arg.Any<JsonElement?>())
            .Returns(callInfo =>
            {
                var session = callInfo.Arg<Session>();
                var userMessage = callInfo.ArgAt<string>(1);
                session.History.Add(new ChatTurn { Role = "user", Content = userMessage });
                var response = $"history:{session.History.Count}";
                session.History.Add(new ChatTurn { Role = "assistant", Content = response });
                return Task.FromResult(response);
            });

        const string stableSessionId = "stable-chat-session";

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent("""
                {
                  "messages": [
                    { "role": "system", "content": "You are helpful." },
                    { "role": "user", "content": "hello" }
                  ]
                }
                """)
        };
        firstRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        firstRequest.Headers.Add("X-OpenClaw-Session-Id", stableSessionId);

        var firstResponse = await harness.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using var firstPayload = await ReadJsonAsync(firstResponse);
        Assert.Equal(
            "history:2",
            firstPayload.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent("""
                {
                  "messages": [
                    { "role": "system", "content": "You are helpful." },
                    { "role": "user", "content": "hello" },
                    { "role": "assistant", "content": "history:2" },
                    { "role": "user", "content": "follow up" }
                  ]
                }
                """)
        };
        secondRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        secondRequest.Headers.Add("X-OpenClaw-Session-Id", stableSessionId);

        var secondResponse = await harness.Client.SendAsync(secondRequest);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        using var secondPayload = await ReadJsonAsync(secondResponse);
        Assert.Equal(
            "history:4",
            secondPayload.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        var persisted = await harness.MemoryStore.GetSessionAsync(stableSessionId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Collection(
            persisted!.History,
            turn =>
            {
                Assert.Equal("system", turn.Role);
                Assert.Equal("You are helpful.", turn.Content);
            },
            turn =>
            {
                Assert.Equal("user", turn.Role);
                Assert.Equal("hello", turn.Content);
            },
            turn =>
            {
                Assert.Equal("assistant", turn.Role);
                Assert.Equal("history:2", turn.Content);
            },
            turn =>
            {
                Assert.Equal("user", turn.Role);
                Assert.Equal("follow up", turn.Content);
            },
            turn =>
            {
                Assert.Equal("assistant", turn.Role);
                Assert.Equal("history:4", turn.Content);
            });

        Assert.True(harness.Runtime.SessionManager.RemoveActive(stableSessionId));
    }

    [Fact]
    public async Task ChatCompletions_StableSession_ReturnsResponseWhenPersistenceFails()
    {
        await using var harness = await CreateHarnessAsync(
            nonLoopbackBind: true,
            memoryStoreFactory: storagePath => new FailingSaveMemoryStore(new FileMemoryStore(storagePath, maxCachedSessions: 8)));
        harness.Runtime.AgentRuntime.RunAsync(
                Arg.Any<Session>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ToolApprovalCallback?>(),
                Arg.Any<JsonElement?>())
            .Returns(callInfo =>
            {
                var session = callInfo.Arg<Session>();
                var userMessage = callInfo.ArgAt<string>(1);
                session.History.Add(new ChatTurn { Role = "user", Content = userMessage });
                session.History.Add(new ChatTurn { Role = "assistant", Content = "ok" });
                return Task.FromResult("ok");
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent("""
                {
                  "messages": [
                    { "role": "user", "content": "hello" }
                  ]
                }
                """)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        request.Headers.Add("X-OpenClaw-Session-Id", "stable-save-failure");

        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        Assert.Equal(
            "ok",
            payload.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        Assert.True(harness.Runtime.SessionManager.RemoveActive("stable-save-failure"));
    }

    [Fact]
    public async Task Responses_StableSession_AccumulatesPersistedHistory()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        harness.Runtime.AgentRuntime.RunAsync(
                Arg.Any<Session>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ToolApprovalCallback?>(),
                Arg.Any<JsonElement?>())
            .Returns(callInfo =>
            {
                var session = callInfo.Arg<Session>();
                var userMessage = callInfo.ArgAt<string>(1);
                session.History.Add(new ChatTurn { Role = "user", Content = userMessage });
                var response = $"history:{session.History.Count}";
                session.History.Add(new ChatTurn { Role = "assistant", Content = response });
                return Task.FromResult(response);
            });

        const string stableSessionId = "stable-responses-session";

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"input":"hello"}""")
        };
        firstRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        firstRequest.Headers.Add("X-OpenClaw-Session-Id", stableSessionId);

        var firstResponse = await harness.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using var firstPayload = await ReadJsonAsync(firstResponse);
        // Unlike chat completions, /v1/responses does not hydrate request history into the session,
        // so the stub sees an empty session on the first turn (count is 1 after appending the user).
        Assert.Equal(
            "history:1",
            GetResponsesAssistantText(firstPayload.RootElement));

        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"input":"follow up"}""")
        };
        secondRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        secondRequest.Headers.Add("X-OpenClaw-Session-Id", stableSessionId);

        var secondResponse = await harness.Client.SendAsync(secondRequest);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        using var secondPayload = await ReadJsonAsync(secondResponse);
        Assert.Equal(
            "history:3",
            GetResponsesAssistantText(secondPayload.RootElement));

        var persisted = await harness.MemoryStore.GetSessionAsync(stableSessionId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Collection(
            persisted!.History,
            turn =>
            {
                Assert.Equal("user", turn.Role);
                Assert.Equal("hello", turn.Content);
            },
            turn =>
            {
                Assert.Equal("assistant", turn.Role);
                Assert.Equal("history:1", turn.Content);
            },
            turn =>
            {
                Assert.Equal("user", turn.Role);
                Assert.Equal("follow up", turn.Content);
            },
            turn =>
            {
                Assert.Equal("assistant", turn.Role);
                Assert.Equal("history:3", turn.Content);
            });

        Assert.True(harness.Runtime.SessionManager.RemoveActive(stableSessionId));
    }

    [Fact]
    public async Task Responses_StableSession_ReturnsResponseWhenPersistenceFails()
    {
        await using var harness = await CreateHarnessAsync(
            nonLoopbackBind: true,
            memoryStoreFactory: storagePath => new FailingSaveMemoryStore(new FileMemoryStore(storagePath, maxCachedSessions: 8)));
        harness.Runtime.AgentRuntime.RunAsync(
                Arg.Any<Session>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ToolApprovalCallback?>(),
                Arg.Any<JsonElement?>())
            .Returns(callInfo =>
            {
                var session = callInfo.Arg<Session>();
                var userMessage = callInfo.ArgAt<string>(1);
                session.History.Add(new ChatTurn { Role = "user", Content = userMessage });
                session.History.Add(new ChatTurn { Role = "assistant", Content = "ok" });
                return Task.FromResult("ok");
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"input":"hello"}""")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        request.Headers.Add("X-OpenClaw-Session-Id", "stable-responses-save-failure");

        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        Assert.Equal("ok", GetResponsesAssistantText(payload.RootElement));

        Assert.True(harness.Runtime.SessionManager.RemoveActive("stable-responses-save-failure"));
    }

    private static string GetResponsesAssistantText(JsonElement root)
    {
        foreach (var item in root.GetProperty("output").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "message")
                continue;
            return item.GetProperty("content")[0].GetProperty("text").GetString()!;
        }

        throw new InvalidOperationException("No assistant message in responses output.");
    }

    [Fact]
    public async Task GenericWebhook_HmacAndIdempotencyUseFullBody_WhenPromptBodyIsTruncated()
    {
        await using var harness = await CreateHarnessAsync(
            nonLoopbackBind: true,
            configure: config =>
            {
                config.Webhooks.Enabled = true;
                config.Webhooks.Endpoints["alerts"] = new WebhookEndpointConfig
                {
                    Secret = "raw:test-secret",
                    ValidateHmac = true,
                    MaxRequestBytes = 4096,
                    MaxBodyLength = 20,
                    PromptTemplate = "Webhook received:\n{body}"
                };
            });

        const string body1 = """{"payload":"12345678901234567890AAAA"}""";
        const string body2 = """{"payload":"12345678901234567890BBBB"}""";

        var first = await PostWebhookAsync(harness.Client, "alerts", body1, "test-secret");
        var second = await PostWebhookAsync(harness.Client, "alerts", body2, "test-secret");
        var duplicate = await PostWebhookAsync(harness.Client, "alerts", body1, "test-secret");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal("Webhook queued.", await first.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("Webhook queued.", await second.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        Assert.Equal("Webhook already processed.", await duplicate.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ToolsApprovals_AndHistory_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        var approval = harness.Runtime.ToolApprovalService.Create("sess1", "telegram", "sender1", "shell", """{"cmd":"ls"}""", TimeSpan.FromMinutes(5));
        harness.Runtime.ApprovalAuditStore.RecordCreated(approval);

        using var approvalsRequest = new HttpRequestMessage(HttpMethod.Get, "/tools/approvals");
        approvalsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var approvalsResponse = await harness.Client.SendAsync(approvalsRequest);
        Assert.Equal(HttpStatusCode.OK, approvalsResponse.StatusCode);
        var approvalsPayload = await ReadJsonAsync(approvalsResponse);
        Assert.Equal(1, approvalsPayload.RootElement.GetProperty("items").GetArrayLength());

        using var historyRequest = new HttpRequestMessage(HttpMethod.Get, "/tools/approvals/history?limit=10");
        historyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var historyResponse = await harness.Client.SendAsync(historyRequest);
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var historyPayload = await ReadJsonAsync(historyResponse);
        Assert.Equal(1, historyPayload.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("created", historyPayload.RootElement.GetProperty("items")[0].GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task ProviderPolicies_Audit_AndRateLimits_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        using var createPolicy = new HttpRequestMessage(HttpMethod.Post, "/admin/providers/policies")
        {
            Content = JsonContent("""
                {
                  "id": "pp_test",
                  "priority": 10,
                  "providerId": "openai",
                  "modelId": "gpt-4o-mini",
                  "enabled": true,
                  "fallbackModels": ["gpt-4o"]
                }
                """)
        };
        createPolicy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var createPolicyResponse = await harness.Client.SendAsync(createPolicy);
        Assert.Equal(HttpStatusCode.OK, createPolicyResponse.StatusCode);

        using var listPolicies = new HttpRequestMessage(HttpMethod.Get, "/admin/providers/policies");
        listPolicies.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var listPoliciesResponse = await harness.Client.SendAsync(listPolicies);
        Assert.Equal(HttpStatusCode.OK, listPoliciesResponse.StatusCode);
        using var policiesPayload = await ReadJsonAsync(listPoliciesResponse);
        Assert.Equal(1, policiesPayload.RootElement.GetProperty("items").GetArrayLength());

        using var resetCircuit = new HttpRequestMessage(HttpMethod.Post, "/admin/providers/openai/circuit/reset");
        resetCircuit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var resetCircuitResponse = await harness.Client.SendAsync(resetCircuit);
        Assert.Equal(HttpStatusCode.OK, resetCircuitResponse.StatusCode);

        using var createRateLimit = new HttpRequestMessage(HttpMethod.Post, "/admin/rate-limits")
        {
            Content = JsonContent("""
                {
                  "id": "rl_test",
                  "actorType": "ip",
                  "endpointScope": "openai_http",
                  "burstLimit": 5,
                  "burstWindowSeconds": 60,
                  "sustainedLimit": 10,
                  "sustainedWindowSeconds": 300,
                  "enabled": true
                }
                """)
        };
        createRateLimit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var createRateLimitResponse = await harness.Client.SendAsync(createRateLimit);
        Assert.Equal(HttpStatusCode.OK, createRateLimitResponse.StatusCode);

        using var auditRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/audit?limit=10");
        auditRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var auditResponse = await harness.Client.SendAsync(auditRequest);
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        using var auditPayload = await ReadJsonAsync(auditResponse);
        var actions = auditPayload.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("actionType").GetString())
            .ToArray();
        Assert.Contains("provider_policy_upsert", actions);
        Assert.Contains("provider_circuit_reset", actions);
        Assert.Contains("rate_limit_policy_upsert", actions);
    }

    [Fact]
    public async Task PluginState_ApprovalPolicies_AndTimeline_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        using var disablePlugin = new HttpRequestMessage(HttpMethod.Post, "/admin/plugins/test-plugin/disable")
        {
            Content = JsonContent("""{"reason":"maintenance"}""")
        };
        disablePlugin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var disableResponse = await harness.Client.SendAsync(disablePlugin);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);

        using var pluginRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/plugins/test-plugin");
        pluginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var pluginResponse = await harness.Client.SendAsync(pluginRequest);
        Assert.Equal(HttpStatusCode.OK, pluginResponse.StatusCode);
        using var pluginPayload = await ReadJsonAsync(pluginResponse);
        Assert.True(pluginPayload.RootElement.GetProperty("disabled").GetBoolean());

        using var createGrant = new HttpRequestMessage(HttpMethod.Post, "/tools/approval-policies")
        {
            Content = JsonContent("""
                {
                  "id": "grant_test",
                  "scope": "sender_tool_window",
                  "channelId": "telegram",
                  "senderId": "user1",
                  "toolName": "shell",
                  "grantedBy": "tester",
                  "grantSource": "test",
                  "remainingUses": 1
                }
                """)
        };
        createGrant.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var createGrantResponse = await harness.Client.SendAsync(createGrant);
        Assert.Equal(HttpStatusCode.OK, createGrantResponse.StatusCode);

        using var listGrantRequest = new HttpRequestMessage(HttpMethod.Get, "/tools/approval-policies");
        listGrantRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var listGrantResponse = await harness.Client.SendAsync(listGrantRequest);
        Assert.Equal(HttpStatusCode.OK, listGrantResponse.StatusCode);
        using var grantPayload = await ReadJsonAsync(listGrantResponse);
        Assert.Equal(1, grantPayload.RootElement.GetProperty("items").GetArrayLength());

        var session = await harness.Runtime.SessionManager.GetOrCreateByIdAsync("sess-timeline", "telegram", "user1", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "hello" });
        await harness.Runtime.SessionManager.PersistAsync(session, CancellationToken.None);
        harness.Runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = "evt_timeline",
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            Component = "test",
            Action = "seeded",
            Severity = "info",
            Summary = "seeded"
        });

        using var timelineRequest = new HttpRequestMessage(HttpMethod.Get, $"/admin/sessions/{Uri.EscapeDataString(session.Id)}/timeline");
        timelineRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var timelineResponse = await harness.Client.SendAsync(timelineRequest);
        Assert.Equal(HttpStatusCode.OK, timelineResponse.StatusCode);
        using var timelinePayload = await ReadJsonAsync(timelineResponse);
        Assert.Equal(session.Id, timelinePayload.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(1, timelinePayload.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task AdminSummary_IncludesRuntimeOrchestrator()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/summary");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        Assert.Equal(
            OpenClaw.Core.Models.RuntimeOrchestrator.Native,
            payload.RootElement.GetProperty("runtime").GetProperty("orchestrator").GetString());
    }

    [Fact]
    public async Task AdminSessions_StarredFilter_PaginatesAfterMetadataMatch()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);
        var t = DateTimeOffset.Parse("2025-06-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        await harness.MemoryStore.SaveSessionAsync(new Session
        {
            Id = "sess-old",
            ChannelId = "ch",
            SenderId = "u",
            CreatedAt = t,
            LastActiveAt = t,
            State = SessionState.Active,
            History = []
        }, CancellationToken.None);
        await harness.MemoryStore.SaveSessionAsync(new Session
        {
            Id = "sess-mid",
            ChannelId = "ch",
            SenderId = "u",
            CreatedAt = t.AddHours(1),
            LastActiveAt = t.AddHours(1),
            State = SessionState.Active,
            History = []
        }, CancellationToken.None);
        await harness.MemoryStore.SaveSessionAsync(new Session
        {
            Id = "sess-new",
            ChannelId = "ch",
            SenderId = "u",
            CreatedAt = t.AddHours(2),
            LastActiveAt = t.AddHours(2),
            State = SessionState.Active,
            History = []
        }, CancellationToken.None);

        harness.Runtime.Operations.SessionMetadata.Set("sess-old", new SessionMetadataUpdateRequest { Starred = true });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/sessions?page=1&pageSize=2&starred=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        var items = payload.RootElement.GetProperty("persisted").GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("sess-old", items[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task AdminSessions_TagFilter_PaginatesAfterMetadataMatch()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);
        var t = DateTimeOffset.Parse("2025-06-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 3; i++)
        {
            await harness.MemoryStore.SaveSessionAsync(new Session
            {
                Id = $"sess-tag-{i}",
                ChannelId = "ch",
                SenderId = "u",
                CreatedAt = t.AddHours(i),
                LastActiveAt = t.AddHours(i),
                State = SessionState.Active,
                History = []
            }, CancellationToken.None);
        }

        harness.Runtime.Operations.SessionMetadata.Set("sess-tag-0", new SessionMetadataUpdateRequest { Tags = ["vip"] });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/sessions?page=1&pageSize=2&tag=vip");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        var items = payload.RootElement.GetProperty("persisted").GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("sess-tag-0", items[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task IntegrationApi_Status_Sessions_Events_AndMessageQueue_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var session = await harness.Runtime.SessionManager.GetOrCreateByIdAsync("sess-integration", "api", "user1", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "hello" });
        await harness.Runtime.SessionManager.PersistAsync(session, CancellationToken.None);
        harness.Runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = "evt_integration",
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            Component = "integration-test",
            Action = "seeded",
            Severity = "info",
            Summary = "seeded"
        });

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/status");
        statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var statusResponse = await harness.Client.SendAsync(statusRequest);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        using var statusPayload = await ReadJsonAsync(statusResponse);
        Assert.Equal("ok", statusPayload.RootElement.GetProperty("health").GetProperty("status").GetString());
        Assert.True(statusPayload.RootElement.GetProperty("activeSessions").GetInt32() >= 1);

        using var sessionsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/sessions?page=1&pageSize=10&channelId=api");
        sessionsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var sessionsResponse = await harness.Client.SendAsync(sessionsRequest);
        Assert.Equal(HttpStatusCode.OK, sessionsResponse.StatusCode);
        using var sessionsPayload = await ReadJsonAsync(sessionsResponse);
        Assert.Equal(1, sessionsPayload.RootElement.GetProperty("active").GetArrayLength());

        using var detailRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/integration/sessions/{Uri.EscapeDataString(session.Id)}");
        detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var detailResponse = await harness.Client.SendAsync(detailRequest);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detailPayload = await ReadJsonAsync(detailResponse);
        Assert.Equal(session.Id, detailPayload.RootElement.GetProperty("session").GetProperty("id").GetString());
        Assert.True(detailPayload.RootElement.GetProperty("isActive").GetBoolean());

        using var eventsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/runtime-events?limit=10&component=integration-test");
        eventsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var eventsResponse = await harness.Client.SendAsync(eventsRequest);
        Assert.Equal(HttpStatusCode.OK, eventsResponse.StatusCode);
        using var eventsPayload = await ReadJsonAsync(eventsResponse);
        Assert.Equal(1, eventsPayload.RootElement.GetProperty("items").GetArrayLength());

        using var enqueueRequest = new HttpRequestMessage(HttpMethod.Post, "/api/integration/messages")
        {
            Content = JsonContent("""
                {
                  "channelId": "api",
                  "senderId": "client-1",
                  "text": "queued message"
                }
                """)
        };
        enqueueRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var enqueueResponse = await harness.Client.SendAsync(enqueueRequest);
        Assert.Equal(HttpStatusCode.Accepted, enqueueResponse.StatusCode);
        using var enqueuePayload = await ReadJsonAsync(enqueueResponse);
        Assert.True(enqueuePayload.RootElement.GetProperty("accepted").GetBoolean());

        var queued = await harness.Runtime.Pipeline.InboundReader.ReadAsync(CancellationToken.None);
        Assert.Equal("api", queued.ChannelId);
        Assert.Equal("client-1", queued.SenderId);
        Assert.Equal("queued message", queued.Text);
    }

    [Fact]
    public async Task IntegrationApi_Dashboard_Approvals_Providers_Plugins_Audit_AndTimeline_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var session = await harness.Runtime.SessionManager.GetOrCreateByIdAsync("sess-dashboard", "api", "user-dashboard", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "inspect me" });
        await harness.Runtime.SessionManager.PersistAsync(session, CancellationToken.None);

        var approval = harness.Runtime.ToolApprovalService.Create("sess-dashboard", "api", "user-dashboard", "shell", "{\"cmd\":\"pwd\"}", TimeSpan.FromMinutes(5));
        harness.Runtime.ApprovalAuditStore.RecordCreated(approval);
        harness.Runtime.Operations.OperatorAudit.Append(new OperatorAuditEntry
        {
            Id = "audit_dashboard_1",
            ActorId = "tester",
            AuthMode = "bearer",
            ActionType = "dashboard_test",
            TargetId = session.Id,
            Summary = "seeded",
            Success = true
        });
        harness.Runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = "evt_dashboard",
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            Component = "dashboard-test",
            Action = "seeded",
            Severity = "info",
            Summary = "seeded"
        });

        using var dashboardRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/dashboard");
        dashboardRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var dashboardResponse = await harness.Client.SendAsync(dashboardRequest);
        Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);
        using var dashboardPayload = await ReadJsonAsync(dashboardResponse);
        Assert.Equal("ok", dashboardPayload.RootElement.GetProperty("status").GetProperty("health").GetProperty("status").GetString());
        Assert.Equal(1, dashboardPayload.RootElement.GetProperty("approvals").GetProperty("items").GetArrayLength());

        using var approvalsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/approvals?channelId=api&senderId=user-dashboard");
        approvalsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var approvalsResponse = await harness.Client.SendAsync(approvalsRequest);
        Assert.Equal(HttpStatusCode.OK, approvalsResponse.StatusCode);
        using var approvalsPayload = await ReadJsonAsync(approvalsResponse);
        Assert.Equal(1, approvalsPayload.RootElement.GetProperty("items").GetArrayLength());

        using var approvalHistoryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/approval-history?limit=10&channelId=api");
        approvalHistoryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var approvalHistoryResponse = await harness.Client.SendAsync(approvalHistoryRequest);
        Assert.Equal(HttpStatusCode.OK, approvalHistoryResponse.StatusCode);
        using var approvalHistoryPayload = await ReadJsonAsync(approvalHistoryResponse);
        Assert.Equal("created", approvalHistoryPayload.RootElement.GetProperty("items")[0].GetProperty("eventType").GetString());

        using var providersRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/providers?recentTurnsLimit=5");
        providersRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var providersResponse = await harness.Client.SendAsync(providersRequest);
        Assert.Equal(HttpStatusCode.OK, providersResponse.StatusCode);
        using var providersPayload = await ReadJsonAsync(providersResponse);
        Assert.True(providersPayload.RootElement.TryGetProperty("routes", out _));

        using var pluginsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/plugins");
        pluginsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var pluginsResponse = await harness.Client.SendAsync(pluginsRequest);
        Assert.Equal(HttpStatusCode.OK, pluginsResponse.StatusCode);
        using var pluginsPayload = await ReadJsonAsync(pluginsResponse);
        Assert.True(pluginsPayload.RootElement.TryGetProperty("items", out _));

        using var auditRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/operator-audit?limit=10&actionType=dashboard_test");
        auditRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var auditResponse = await harness.Client.SendAsync(auditRequest);
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        using var auditPayload = await ReadJsonAsync(auditResponse);
        Assert.Equal(1, auditPayload.RootElement.GetProperty("items").GetArrayLength());

        using var detailRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/integration/sessions/{Uri.EscapeDataString(session.Id)}");
        detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var detailResponse = await harness.Client.SendAsync(detailRequest);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detailPayload = await ReadJsonAsync(detailResponse);
        Assert.Equal(0, detailPayload.RootElement.GetProperty("branchCount").GetInt32());

        using var timelineRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/integration/sessions/{Uri.EscapeDataString(session.Id)}/timeline?limit=10");
        timelineRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var timelineResponse = await harness.Client.SendAsync(timelineRequest);
        Assert.Equal(HttpStatusCode.OK, timelineResponse.StatusCode);
        using var timelinePayload = await ReadJsonAsync(timelineResponse);
        Assert.Equal(session.Id, timelinePayload.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(1, timelinePayload.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task Mcp_Initialize_List_And_Call_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var anonymousResponse = await harness.Client.PostAsync("/mcp", JsonContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        HttpRequestMessage McpRequest(string json)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent(json) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
            return req;
        }

        using var initializeRequest = McpRequest("""
                {
                  "jsonrpc": "2.0",
                  "id": 1,
                  "method": "initialize",
                  "params": {
                    "protocolVersion": "2025-03-26",
                    "capabilities": {},
                    "clientInfo": { "name": "test-client", "version": "1.0.0" }
                  }
                }
                """);
        var initializeResponse = await harness.Client.SendAsync(initializeRequest);
        Assert.Equal(HttpStatusCode.OK, initializeResponse.StatusCode);
        using var initializePayload = await ReadMcpJsonAsync(initializeResponse);
        Assert.Equal("OpenClaw Gateway MCP", initializePayload.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        using var toolsListRequest = McpRequest("""
                {
                  "jsonrpc": "2.0",
                  "id": 2,
                  "method": "tools/list",
                  "params": {}
                }
                """);
        var toolsListResponse = await harness.Client.SendAsync(toolsListRequest);
        Assert.Equal(HttpStatusCode.OK, toolsListResponse.StatusCode);
        using var toolsListPayload = await ReadMcpJsonAsync(toolsListResponse);
        Assert.Contains(toolsListPayload.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(item => item.GetProperty("name").GetString()), name => name == "openclaw.get_dashboard");

        using var templatesListRequest = McpRequest("""
                {
                  "jsonrpc": "2.0",
                  "id": 22,
                  "method": "resources/templates/list",
                  "params": {}
                }
                """);
        var templatesListResponse = await harness.Client.SendAsync(templatesListRequest);
        Assert.Equal(HttpStatusCode.OK, templatesListResponse.StatusCode);
        using var templatesListPayload = await ReadMcpJsonAsync(templatesListResponse);
        Assert.Contains(templatesListPayload.RootElement.GetProperty("result").GetProperty("resourceTemplates").EnumerateArray().Select(item => item.GetProperty("uriTemplate").GetString()), template => template == "openclaw://sessions/{sessionId}");

        using var callToolRequest = McpRequest("""
                {
                  "jsonrpc": "2.0",
                  "id": 3,
                  "method": "tools/call",
                  "params": {
                    "name": "openclaw.get_status",
                    "arguments": {}
                  }
                }
                """);
        var callToolResponse = await harness.Client.SendAsync(callToolRequest);
        Assert.Equal(HttpStatusCode.OK, callToolResponse.StatusCode);
        using var callToolPayload = await ReadMcpJsonAsync(callToolResponse);
        var statusText = callToolPayload.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("activeSessions", statusText);

        using var resourceReadRequest = McpRequest("""
                {
                  "jsonrpc": "2.0",
                  "id": 4,
                  "method": "resources/read",
                  "params": {
                    "uri": "openclaw://dashboard"
                  }
                }
                """);
        var resourceReadResponse = await harness.Client.SendAsync(resourceReadRequest);
        Assert.Equal(HttpStatusCode.OK, resourceReadResponse.StatusCode);
        using var resourceReadPayload = await ReadMcpJsonAsync(resourceReadResponse);
        var dashboardText = resourceReadPayload.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString();
        Assert.Contains("status", dashboardText);

        using var promptGetRequest = McpRequest("""
                {
                  "jsonrpc": "2.0",
                  "id": 23,
                  "method": "prompts/get",
                  "params": {
                    "name": "openclaw_session_summary",
                    "arguments": {
                      "sessionId": "sess-dashboard"
                    }
                  }
                }
                """);
        var promptGetResponse = await harness.Client.SendAsync(promptGetRequest);
        Assert.Equal(HttpStatusCode.OK, promptGetResponse.StatusCode);
        using var promptGetPayload = await ReadMcpJsonAsync(promptGetResponse);
        var promptText = promptGetPayload.RootElement.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString();
        Assert.Contains("sess-dashboard", promptText);
    }

    [Fact]
    public async Task OpenClawHttpClient_McpSurface_Works()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var session = await harness.Runtime.SessionManager.GetOrCreateByIdAsync("sess-client-mcp", "api", "sdk-user", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "hello from sdk" });
        await harness.Runtime.SessionManager.PersistAsync(session, CancellationToken.None);

        using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);

        var initialize = await client.InitializeMcpAsync(new McpInitializeRequest { ProtocolVersion = "2025-03-26" }, CancellationToken.None);
        Assert.NotNull(initialize.ServerInfo);

        var tools = await client.ListMcpToolsAsync(CancellationToken.None);
        Assert.Contains(tools.Tools, item => item.Name == "openclaw.get_dashboard");

        var templates = await client.ListMcpResourceTemplatesAsync(CancellationToken.None);
        Assert.Contains(templates.ResourceTemplates, item => item.UriTemplate == "openclaw://sessions/{sessionId}");

        var prompt = await client.GetMcpPromptAsync(
            "openclaw_session_summary",
            new Dictionary<string, string> { ["sessionId"] = session.Id },
            CancellationToken.None);
        Assert.Contains(session.Id, prompt.Messages[0].Content.Text);

        var sessionResource = await client.ReadMcpResourceAsync($"openclaw://sessions/{Uri.EscapeDataString(session.Id)}", CancellationToken.None);
        Assert.Contains(session.Id, sessionResource.Contents[0].Text);

        using var emptyArguments = JsonDocument.Parse("{}");
        var toolResult = await client.CallMcpToolAsync("openclaw.get_status", emptyArguments.RootElement.Clone(), CancellationToken.None);
        Assert.False(toolResult.IsError);
        Assert.Contains("activeSessions", toolResult.Content[0].Text);
    }

    [Fact]
    public async Task IntegrationAccounts_And_AdminResolution_Work()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/integration/accounts")
        {
            Content = JsonContent("""{"provider":"codex","displayName":"Local Codex","secret":"secret-value"}""")
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var createResponse = await harness.Client.SendAsync(createRequest);
        var createText = await createResponse.Content.ReadAsStringAsync();
        Assert.True(createResponse.StatusCode == HttpStatusCode.OK, createText);
        using var createPayload = await ReadJsonAsync(createResponse);
        var createdAccount = createPayload.RootElement.GetProperty("account");
        var accountId = createdAccount.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accountId));
        AssertRedactedOrMissing(createdAccount, "encryptedSecretJson");
        AssertRedactedOrMissing(createdAccount, "tokenFilePath");
        AssertRedactedOrMissing(createdAccount, "secretRef");

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/accounts");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var listResponse = await harness.Client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var listPayload = await ReadJsonAsync(listResponse);
        var listedAccount = listPayload.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == accountId);
        AssertRedactedOrMissing(listedAccount, "encryptedSecretJson");
        AssertRedactedOrMissing(listedAccount, "tokenFilePath");
        AssertRedactedOrMissing(listedAccount, "secretRef");

        using var probeRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/accounts/test-resolution")
        {
            Content = JsonContent($"{{\"credentialSource\":{{\"connectedAccountId\":\"{accountId}\"}}}}")
        };
        probeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var probeResponse = await harness.Client.SendAsync(probeRequest);
        var probeText = await probeResponse.Content.ReadAsStringAsync();
        Assert.True(probeResponse.StatusCode == HttpStatusCode.OK, probeText);
        using var probePayload = await ReadJsonAsync(probeResponse);
        Assert.True(probePayload.RootElement.GetProperty("success").GetBoolean());
        Assert.True(probePayload.RootElement.GetProperty("hasSecret").GetBoolean());
        Assert.Equal(accountId, probePayload.RootElement.GetProperty("credential").GetProperty("accountId").GetString());
        AssertRedactedOrMissing(probePayload.RootElement.GetProperty("credential"), "secret");
        AssertRedactedOrMissing(probePayload.RootElement.GetProperty("credential"), "tokenFilePath");
    }

    [Fact]
    public async Task IntegrationBackends_FakeBackend_SessionLifecycle_Works()
    {
        await using var harness = await CreateHarnessAsync(
            nonLoopbackBind: true,
            configureServices: static (services, _) =>
            {
                services.AddSingleton<ICodingAgentBackend, FakeCodingAgentBackend>();
            });

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/backends");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var listResponse = await harness.Client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var listPayload = await ReadJsonAsync(listResponse);
        Assert.Contains(
            listPayload.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("backendId").GetString()),
            id => id == "fake-backend");

        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/api/integration/backends/fake-backend/sessions")
        {
            Content = JsonContent("""{"backendId":"fake-backend","prompt":"hello fake"}""")
        };
        startRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var startResponse = await harness.Client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        using var startPayload = await ReadJsonAsync(startResponse);
        var sessionId = startPayload.RootElement.GetProperty("session").GetProperty("sessionId").GetString()!;

        using var inputRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/integration/backends/fake-backend/sessions/{Uri.EscapeDataString(sessionId)}/input")
        {
            Content = JsonContent("""{"text":"ping"}""")
        };
        inputRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var inputResponse = await harness.Client.SendAsync(inputRequest);
        Assert.Equal(HttpStatusCode.OK, inputResponse.StatusCode);

        using var eventsRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/integration/backends/fake-backend/sessions/{Uri.EscapeDataString(sessionId)}/events?afterSequence=0&limit=20");
        eventsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var eventsResponse = await harness.Client.SendAsync(eventsRequest);
        Assert.Equal(HttpStatusCode.OK, eventsResponse.StatusCode);
        using var eventsPayload = await ReadJsonAsync(eventsResponse);
        Assert.True(eventsPayload.RootElement.GetProperty("items").GetArrayLength() >= 2);

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/integration/backends/fake-backend/sessions/{Uri.EscapeDataString(sessionId)}");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var deleteResponse = await harness.Client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task IntegrationBackends_EventStream_ReplaysSsePayload()
    {
        await using var harness = await CreateHarnessAsync(
            nonLoopbackBind: true,
            configureServices: static (services, _) =>
            {
                services.AddSingleton<ICodingAgentBackend, FakeCodingAgentBackend>();
            });

        var owner = await harness.Runtime.SessionManager.GetOrCreateByIdAsync("sess-backend-owner", "api", "owner", CancellationToken.None);
        await harness.Runtime.SessionManager.PersistAsync(owner, CancellationToken.None);

        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/api/integration/backends/fake-backend/sessions")
        {
            Content = JsonContent("""{"backendId":"fake-backend","ownerSessionId":"sess-backend-owner","prompt":"stream me"}""")
        };
        startRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var startResponse = await harness.Client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        using var startPayload = await ReadJsonAsync(startResponse);
        var sessionId = startPayload.RootElement.GetProperty("session").GetProperty("sessionId").GetString()!;

        using var stopRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/integration/backends/fake-backend/sessions/{Uri.EscapeDataString(sessionId)}");
        stopRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var stopResponse = await harness.Client.SendAsync(stopRequest);
        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);

        using var streamRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/integration/backends/fake-backend/sessions/{Uri.EscapeDataString(sessionId)}/events/stream?afterSequence=0&limit=20");
        streamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        streamRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        var streamResponse = await harness.Client.SendAsync(streamRequest);
        Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
        Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType);
        var payload = await streamResponse.Content.ReadAsStringAsync();
        Assert.Contains("assistant_message", payload, StringComparison.Ordinal);
        Assert.Contains("session_completed", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntegrationBackends_EventStream_SendsInitialComment_AndStopsCleanlyOnRequestAbort()
    {
        using var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<BackendEvent>();
        var responseBody = new MemoryStream();
        var context = new DefaultHttpContext();
        context.RequestAborted = cts.Token;
        context.Response.Body = responseBody;
        context.Response.ContentType = "text/event-stream";

        var session = new BackendSessionRecord
        {
            SessionId = "backend-stream",
            BackendId = "fake-backend",
            Provider = "fake",
            State = BackendSessionState.Running
        };

        var streamTask = IntegrationBackendEndpoints.StreamSessionEventsAsync(context, session, [], channel.Reader, afterSequence: 0);

        await WaitForAsync(
            static state => ((MemoryStream)state!).Length > 0,
            responseBody,
            TimeSpan.FromSeconds(1));

        cts.Cancel();
        await streamTask;

        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody, Encoding.UTF8, leaveOpen: true);
        var payload = await reader.ReadToEndAsync();
        Assert.StartsWith(": stream-open", payload, StringComparison.Ordinal);
        Assert.Equal("text/event-stream", context.Response.ContentType);
    }

    [Fact]
    public async Task WhatsAppSetup_GetPut_AndClientSurface_Work()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true, config =>
        {
            config.Channels.WhatsApp.Enabled = true;
            config.Channels.WhatsApp.Type = "official";
            config.Channels.WhatsApp.DmPolicy = "pairing";
            config.Channels.WhatsApp.WebhookPath = "/whatsapp/inbound";
            config.Channels.WhatsApp.WebhookPublicBaseUrl = "https://example.test";
            config.Channels.WhatsApp.WebhookVerifyToken = "verify-me";
            config.Channels.WhatsApp.WebhookVerifyTokenRef = "env:WA_VERIFY";
            config.Channels.WhatsApp.ValidateSignature = true;
            config.Channels.WhatsApp.WebhookAppSecretRef = "env:WA_SECRET";
            config.Channels.WhatsApp.CloudApiTokenRef = "env:WA_TOKEN";
            config.Channels.WhatsApp.PhoneNumberId = "phone-1";
            config.Channels.WhatsApp.BusinessAccountId = "biz-1";
        });

        using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);

        var initial = await client.GetWhatsAppSetupAsync(CancellationToken.None);
        Assert.Equal("official", initial.ActiveBackend);
        Assert.True(initial.Enabled);
        Assert.Equal("phone-1", initial.PhoneNumberId);
        Assert.Equal("https://example.test/whatsapp/inbound", initial.DerivedWebhookUrl);
        Assert.Equal("", initial.WebhookVerifyToken);
        Assert.True(initial.WebhookVerifyTokenConfigured);
        Assert.False(initial.CloudApiTokenConfigured);
        Assert.Null(initial.CloudApiToken);
        Assert.Contains(initial.Warnings, warning => warning.Contains("redacted on read", StringComparison.OrdinalIgnoreCase));

        var updated = await client.SaveWhatsAppSetupAsync(new WhatsAppSetupRequest
        {
            Enabled = true,
            Type = "bridge",
            DmPolicy = "open",
            WebhookPath = "/wa/hook",
            WebhookPublicBaseUrl = "https://example.test/root",
            WebhookVerifyToken = "verify-2",
            WebhookVerifyTokenRef = "env:WA_VERIFY_2",
            ValidateSignature = false,
            BridgeUrl = "http://127.0.0.1:3001",
            BridgeToken = "bridge-token",
            BridgeTokenRef = "env:WA_BRIDGE_TOKEN",
            BridgeSuppressSendExceptions = true
        }, CancellationToken.None);

        Assert.Equal("bridge", updated.ConfiguredType);
        Assert.Equal("http://127.0.0.1:3001", updated.BridgeUrl);
        Assert.True(updated.BridgeSuppressSendExceptions);
        Assert.True(updated.RestartRequired);

        var reloaded = await client.GetWhatsAppSetupAsync(CancellationToken.None);
        Assert.Equal("bridge", reloaded.ConfiguredType);
        Assert.Equal("open", reloaded.DmPolicy);
        Assert.Equal("/wa/hook", reloaded.WebhookPath);
        Assert.Equal("https://example.test/root/wa/hook", reloaded.DerivedWebhookUrl);
        Assert.Null(reloaded.BridgeToken);
        Assert.True(reloaded.BridgeTokenConfigured);
    }

    [Fact]
    public async Task WhatsAppAuthEndpoints_ReturnPerAccountState_AndSupportFiltering()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        harness.Runtime.ChannelAuthEvents.Record(new BridgeChannelAuthEvent
        {
            ChannelId = "whatsapp",
            AccountId = "acc-1",
            State = "qr_code",
            Data = "qr-one",
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        harness.Runtime.ChannelAuthEvents.Record(new BridgeChannelAuthEvent
        {
            ChannelId = "whatsapp",
            AccountId = "acc-2",
            State = "connected",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        using var allRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/channels/whatsapp/auth");
        allRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var allResponse = await harness.Client.SendAsync(allRequest);
        Assert.Equal(HttpStatusCode.OK, allResponse.StatusCode);
        using var allPayload = await ReadJsonAsync(allResponse);
        Assert.Equal(2, allPayload.RootElement.GetProperty("items").GetArrayLength());

        using var filteredRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/channels/whatsapp/auth?accountId=acc-1");
        filteredRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var filteredResponse = await harness.Client.SendAsync(filteredRequest);
        Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);
        using var filteredPayload = await ReadJsonAsync(filteredResponse);
        var filteredItems = filteredPayload.RootElement.GetProperty("items");
        Assert.Single(filteredItems.EnumerateArray());
        Assert.Equal("acc-1", filteredItems[0].GetProperty("accountId").GetString());
        Assert.Equal("qr_code", filteredItems[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task ChannelAuthStream_SendsInitialComment_AndStopsCleanlyOnRequestAbort()
    {
        var store = new ChannelAuthEventStore();
        using var cts = new CancellationTokenSource();
        var responseBody = new MemoryStream();
        var context = new DefaultHttpContext();
        context.RequestAborted = cts.Token;
        context.Response.Body = responseBody;

        var streamTask = AdminEndpoints.StreamChannelAuthEventsAsync(context, store, "whatsapp", accountId: null);

        await WaitForAsync(
            static state => ((MemoryStream)state!).Length > 0,
            responseBody,
            TimeSpan.FromSeconds(1));

        cts.Cancel();
        await streamTask;

        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody, Encoding.UTF8, leaveOpen: true);
        var payload = await reader.ReadToEndAsync();
        Assert.StartsWith(": stream-open", payload, StringComparison.Ordinal);
        Assert.Equal("text/event-stream", context.Response.ContentType);
    }

    [Fact]
    public async Task WhatsAppSetup_PersistsFirstPartyWorkerConfigJson()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true, config =>
        {
            config.Channels.WhatsApp.Enabled = true;
            config.Channels.WhatsApp.Type = "official";
        });

        using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);
        var updated = await client.SaveWhatsAppSetupAsync(new WhatsAppSetupRequest
        {
            Enabled = true,
            Type = "first_party_worker",
            DmPolicy = "pairing",
            FirstPartyWorkerConfigJson =
                """
                {
                  "driver": "simulated",
                  "executablePath": "/tmp/OpenClaw.WhatsApp.BaileysWorker.dll",
                  "accounts": [
                    {
                      "accountId": "primary",
                      "sessionPath": "./session/primary",
                      "pairingMode": "qr"
                    }
                  ]
                }
                """
        }, CancellationToken.None);

        Assert.Equal("first_party_worker", updated.ConfiguredType);
        Assert.NotNull(updated.FirstPartyWorker);
        Assert.Equal("simulated", updated.FirstPartyWorker!.Driver);
        Assert.Contains("\"accountId\":\"primary\"", updated.FirstPartyWorkerConfigJson);
        Assert.False(string.IsNullOrWhiteSpace(updated.FirstPartyWorkerConfigSchemaJson));
        Assert.Contains("\"whatsmeow\"", updated.FirstPartyWorkerConfigSchemaJson, StringComparison.Ordinal);

        var reloaded = await client.GetWhatsAppSetupAsync(CancellationToken.None);
        Assert.Equal("first_party_worker", reloaded.ConfiguredType);
        Assert.Equal("simulated", reloaded.FirstPartyWorker?.Driver);
    }

    [Fact]
    public async Task WhatsAppSetup_SaveBlankSecrets_PreservesExistingValues_UntilRefsAreCleared()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true, config =>
        {
            config.Channels.WhatsApp.Enabled = true;
            config.Channels.WhatsApp.Type = "official";
            config.Channels.WhatsApp.WebhookVerifyToken = "verify-existing";
            config.Channels.WhatsApp.WebhookVerifyTokenRef = "env:WA_VERIFY";
            config.Channels.WhatsApp.WebhookAppSecret = "secret-existing";
            config.Channels.WhatsApp.WebhookAppSecretRef = "env:WA_SECRET";
            config.Channels.WhatsApp.CloudApiToken = "cloud-existing";
            config.Channels.WhatsApp.CloudApiTokenRef = "env:WA_TOKEN";
            config.Channels.WhatsApp.BridgeToken = "bridge-existing";
            config.Channels.WhatsApp.BridgeTokenRef = "env:WA_BRIDGE";
        });

        using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);

        var preserved = await client.SaveWhatsAppSetupAsync(new WhatsAppSetupRequest
        {
            Enabled = true,
            Type = "official",
            DmPolicy = "pairing",
            WebhookPath = "/whatsapp/inbound",
            WebhookVerifyToken = "",
            WebhookVerifyTokenRef = "env:WA_VERIFY",
            WebhookAppSecret = null,
            WebhookAppSecretRef = "env:WA_SECRET",
            CloudApiToken = null,
            CloudApiTokenRef = "env:WA_TOKEN",
            BridgeToken = null,
            BridgeTokenRef = "env:WA_BRIDGE"
        }, CancellationToken.None);

        Assert.True(preserved.WebhookVerifyTokenConfigured);
        Assert.True(preserved.WebhookAppSecretConfigured);
        Assert.True(preserved.CloudApiTokenConfigured);
        Assert.True(preserved.BridgeTokenConfigured);

        var cleared = await client.SaveWhatsAppSetupAsync(new WhatsAppSetupRequest
        {
            Enabled = true,
            Type = "official",
            DmPolicy = "pairing",
            WebhookPath = "/whatsapp/inbound",
            WebhookVerifyToken = "",
            WebhookVerifyTokenRef = "",
            WebhookAppSecret = null,
            WebhookAppSecretRef = "",
            CloudApiToken = null,
            CloudApiTokenRef = "",
            BridgeToken = null,
            BridgeTokenRef = ""
        }, CancellationToken.None);

        Assert.False(cleared.WebhookVerifyTokenConfigured);
        Assert.False(cleared.WebhookAppSecretConfigured);
        Assert.False(cleared.CloudApiTokenConfigured);
        Assert.False(cleared.BridgeTokenConfigured);
    }

    [Fact]
    public async Task WhatsAppWebhookVerification_AllowsRepeatedGetChallenges()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true, config =>
        {
            config.Channels.WhatsApp.Enabled = true;
            config.Channels.WhatsApp.Type = "official";
            config.Channels.WhatsApp.WebhookPath = "/whatsapp/inbound";
            config.Channels.WhatsApp.WebhookVerifyToken = "verify-me";
            config.Channels.WhatsApp.WebhookVerifyTokenRef = "";
        });

        const string path = "/whatsapp/inbound?hub.mode=subscribe&hub.verify_token=verify-me&hub.challenge=challenge-123";

        var firstResponse = await harness.Client.GetAsync(path);
        var secondResponse = await harness.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal("challenge-123", await firstResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal("challenge-123", await secondResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WhatsAppFirstPartyWorker_DoesNotRequireWebhookHandlerRegistration()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true, config =>
        {
            config.Channels.WhatsApp.Enabled = true;
            config.Channels.WhatsApp.Type = "first_party_worker";
            config.Channels.WhatsApp.WebhookPath = "/whatsapp/inbound";
        });

        var response = await harness.Client.GetAsync("/whatsapp/inbound");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WhatsAppRestartEndpoint_RestartsAdapter_AndClearsAuthState()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        var adapter = new RestartableTestChannelAdapter("whatsapp");
        ((Dictionary<string, IChannelAdapter>)harness.Runtime.ChannelAdapters)["whatsapp"] = adapter;
        harness.Runtime.ChannelAuthEvents.Record(new BridgeChannelAuthEvent
        {
            ChannelId = "whatsapp",
            AccountId = "acc-1",
            State = "qr_code",
            Data = "qr-one",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/channels/whatsapp/restart");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        Assert.Equal(1, adapter.RestartCount);
        Assert.Equal(0, payload.RootElement.GetProperty("authStates").GetArrayLength());
        Assert.Empty(harness.Runtime.ChannelAuthEvents.GetAll("whatsapp"));
    }

    [Fact]
    public async Task AdminUi_ContainsDedicatedWhatsAppSetupControls()
    {
        var adminHtmlPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/OpenClaw.Gateway/wwwroot/admin.html"));
        var html = await File.ReadAllTextAsync(adminHtmlPath);

        Assert.Contains("id=\"whatsapp-section\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"whatsapp-save-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"whatsapp-reload-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"whatsapp-restart-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"wa-plugin-config-json-input\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"wa-first-party-worker-config-json-input\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"first_party_worker\"", html, StringComparison.Ordinal);
        Assert.Contains("/admin/channels/whatsapp/setup", html, StringComparison.Ordinal);
        Assert.Contains("/admin/channels/whatsapp/auth/stream", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ws://127.0.0.1:18789/ws", "http://127.0.0.1:18789")]
    [InlineData("wss://example.com/ws", "https://example.com")]
    [InlineData("wss://example.com/root/ws?x=1", "https://example.com/root")]
    public void GatewayEndpointResolver_MapsWebSocketUrlsToHttpBase(string input, string expected)
    {
        var success = GatewayEndpointResolver.TryResolveHttpBaseUrl(input, out var resolved);

        Assert.True(success);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public async Task CompanionViewModel_LoadWhatsAppSetupCommand_PopulatesSetupState()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true, config =>
        {
            config.Channels.WhatsApp.Enabled = true;
            config.Channels.WhatsApp.Type = "bridge";
            config.Channels.WhatsApp.DmPolicy = "open";
            config.Channels.WhatsApp.WebhookPublicBaseUrl = "https://example.test";
            config.Channels.WhatsApp.WebhookPath = "/whatsapp/inbound";
            config.Channels.WhatsApp.BridgeUrl = "http://127.0.0.1:3001";
        });
        harness.Runtime.ChannelAuthEvents.Record(new BridgeChannelAuthEvent
        {
            ChannelId = "whatsapp",
            AccountId = "acc-1",
            State = "qr_code",
            Data = "qr-payload",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        var settingsDir = Path.Combine(harness.StoragePath, "companion");
        var viewModel = new MainWindowViewModel(
            new SettingsStore(settingsDir),
            new GatewayWebSocketClient(),
            (_, token) => new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), token, harness.Client))
        {
            ServerUrl = "ws://127.0.0.1:18789/ws",
            AuthToken = harness.AuthToken
        };

        await viewModel.LoadWhatsAppSetupCommand.ExecuteAsync(null);

        Assert.Equal("bridge", viewModel.WhatsAppType);
        Assert.Equal("http://127.0.0.1:3001", viewModel.WhatsAppBridgeUrl);
        Assert.Equal("https://example.test/whatsapp/inbound", viewModel.WhatsAppDerivedWebhookUrl);
        Assert.Equal("qr-payload", viewModel.WhatsAppQrData);
        Assert.Contains("acc-1", viewModel.WhatsAppAuthSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApi_Document_IsExposed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);

        var response = await harness.Client.GetAsync("/openapi/openclaw-integration.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var openApiVersion = payload.RootElement.GetProperty("openapi").GetString();
        Assert.StartsWith("3.", openApiVersion);
        Assert.True(payload.RootElement.GetProperty("paths").TryGetProperty("/api/integration/dashboard", out _));
    }

    [Fact]
    public async Task AdminUiContract_ReferencedRoutes_AreMapped()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);
        var dataSource = harness.App.Services.GetRequiredService<EndpointDataSource>();
        var routePatterns = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToHashSet(StringComparer.Ordinal);

        var expectedRoutes = new[]
        {
            "/auth/session",
            "/openapi/{documentName}.json",
            "/api/integration/dashboard",
            "/api/integration/status",
            "/api/integration/approvals",
            "/api/integration/approval-history",
            "/api/integration/providers",
            "/api/integration/plugins",
            "/api/integration/operator-audit",
            "/api/integration/sessions",
            "/api/integration/sessions/{id}",
            "/api/integration/sessions/{id}/timeline",
            "/api/integration/runtime-events",
            "/api/integration/messages",
            "/mcp/",
            "/admin",
            "/admin/summary",
            "/admin/providers",
            "/admin/providers/policies",
            "/admin/providers/{providerId}/circuit/reset",
            "/admin/events",
            "/admin/sessions",
            "/admin/sessions/{id}",
            "/admin/sessions/{id}/branches",
            "/admin/sessions/{id}/timeline",
            "/admin/sessions/{id}/diff",
            "/admin/sessions/{id}/metadata",
            "/admin/sessions/export",
            "/admin/sessions/{id}/export",
            "/admin/branches/{id}/restore",
            "/admin/plugins",
            "/admin/plugins/{id}",
            "/admin/plugins/{id}/disable",
            "/admin/plugins/{id}/enable",
            "/admin/plugins/{id}/quarantine",
            "/admin/plugins/{id}/clear-quarantine",
            "/admin/audit",
            "/admin/webhooks/dead-letter",
            "/admin/webhooks/dead-letter/{id}/replay",
            "/admin/webhooks/dead-letter/{id}/discard",
            "/admin/rate-limits",
            "/admin/rate-limits/{id}",
            "/admin/settings",
            "/admin/heartbeat",
            "/admin/heartbeat/preview",
            "/admin/heartbeat/status",
            "/admin/channels/auth",
            "/admin/channels/{channelId}/auth",
            "/admin/channels/{channelId}/auth/stream",
            "/admin/channels/whatsapp/setup",
            "/admin/channels/whatsapp/restart",
            "/admin/channels/whatsapp/auth",
            "/admin/channels/whatsapp/auth/stream",
            "/admin/channels/whatsapp/auth/qr.svg",
            "/tools/approvals",
            "/tools/approvals/history",
            "/tools/approval-policies",
            "/tools/approval-policies/{id}",
            "/pairing/list",
            "/allowlists/{channelId}",
            "/allowlists/{channelId}/add_latest",
            "/allowlists/{channelId}/tighten",
            "/memory/retention/status",
            "/memory/retention/sweep",
            "/doctor/text"
        };

        foreach (var route in expectedRoutes)
            Assert.Contains(route, routePatterns);
    }

    [Fact]
    public async Task AdminUi_StaticApiTargets_MapToKnownRoutes()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);
        var dataSource = harness.App.Services.GetRequiredService<EndpointDataSource>();
        var routePatterns = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToHashSet(StringComparer.Ordinal);

        var adminHtmlPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/OpenClaw.Gateway/wwwroot/admin.html"));
        var html = await File.ReadAllTextAsync(adminHtmlPath);
        var matches = Regex.Matches(html, @"(?:api|mutate)\('(?<route>/[^']+)'");
        var staticRoutes = matches
            .Select(match => match.Groups["route"].Value.Split('?', 2)[0])
            .Where(static route => !route.Contains('{', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var route in staticRoutes)
            Assert.Contains(route, routePatterns);
    }

    private static async Task<(string Cookie, string CsrfToken)> LoginAsync(HttpClient client, string authToken)
    {
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/session")
        {
            Content = JsonContent("""{"remember":false}""")
        };
        loginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        var response = await client.SendAsync(loginRequest);
        response.EnsureSuccessStatusCode();
        var payload = await ReadJsonAsync(response);
        return (
            Assert.Single(response.Headers.GetValues("Set-Cookie")),
            payload.RootElement.GetProperty("csrfToken").GetString()!);
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, string name, string body, string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{name}")
        {
            Content = JsonContent(body)
        };
        request.Headers.Add("X-Hub-Signature-256", $"sha256={GatewaySecurity.ComputeHmacSha256Hex(secret, body)}");
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(payload);
    }

    private static async Task WaitForAsync(Func<object?, bool> condition, object? state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition(state))
                return;

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private static void AssertRedactedOrMissing(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return;

        Assert.Equal(JsonValueKind.Null, value.ValueKind);
    }

    private static async Task<JsonDocument> ReadMcpJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in payload.Split('\n'))
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                    return JsonDocument.Parse(line["data:".Length..].TrimStart());
            }

            throw new InvalidOperationException("SSE response did not contain a data line.");
        }

        return JsonDocument.Parse(payload);
    }

    private static async Task<GatewayTestHarness> CreateHarnessAsync(
        bool nonLoopbackBind,
        Action<GatewayConfig>? configure = null,
        Func<string, IMemoryStore>? memoryStoreFactory = null,
        Action<IServiceCollection, GatewayConfig>? configureServices = null)
    {
        return await CreateHarnessAsyncInternal(nonLoopbackBind, configure, memoryStoreFactory, configureServices);
    }

    private static async Task<GatewayTestHarness> CreateHarnessAsyncInternal(
        bool nonLoopbackBind,
        Action<GatewayConfig>? configure,
        Func<string, IMemoryStore>? memoryStoreFactory,
        Action<IServiceCollection, GatewayConfig>? configureServices)
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-admin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        var config = new GatewayConfig
        {
            BindAddress = nonLoopbackBind ? "0.0.0.0" : "127.0.0.1",
            AuthToken = "test-admin-token",
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Llm = new LlmProviderConfig
            {
                Provider = "openai",
                ApiKey = "test-key",
                Model = "gpt-4o",
                RetryCount = 0,
                TimeoutSeconds = 0
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false,
                AllowBrowserEvaluate = false
            },
            Plugins = new OpenClaw.Core.Plugins.PluginsConfig
            {
                Enabled = false
            }
        };
        configure?.Invoke(config);

        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
            IsNonLoopbackBind = nonLoopbackBind,
            WorkspacePath = null
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOpenApi("openclaw-integration");
        builder.Services.ConfigureHttpJsonOptions(opts => opts.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));
        builder.Services.AddSingleton(config);
        var memoryStore = memoryStoreFactory?.Invoke(storagePath) ?? new FileMemoryStore(storagePath, maxCachedSessions: 8);
        var sessionManager = new SessionManager(memoryStore, config, NullLogger.Instance);
        var heartbeatService = new HeartbeatService(config, memoryStore, sessionManager, NullLogger<HeartbeatService>.Instance);
        builder.Services.AddSingleton<IMemoryStore>(memoryStore);
        builder.Services.AddSingleton<ISessionAdminStore>(_ => (ISessionAdminStore)memoryStore);
        var featureStore = new FileFeatureStore(storagePath);
        builder.Services.AddSingleton<IConnectedAccountStore>(_ => featureStore);
        builder.Services.AddSingleton<IBackendSessionStore>(_ => featureStore);
        builder.Services.AddSingleton(sessionManager);
        builder.Services.AddSingleton(heartbeatService);
        builder.Services.AddSingleton(new BrowserSessionAuthService(config));
        builder.Services.AddSingleton(new AdminSettingsService(
            config,
            AdminSettingsService.CreateSnapshot(config),
            AdminSettingsService.GetSettingsPath(config),
            NullLogger<AdminSettingsService>.Instance));
        builder.Services.AddSingleton(new PluginAdminSettingsService(
            config,
            NullLogger<PluginAdminSettingsService>.Instance));
        if (!string.Equals(config.Channels.WhatsApp.Type, "first_party_worker", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton(new WhatsAppWebhookHandler(
                config.Channels.WhatsApp,
                new AllowlistManager(storagePath, NullLogger<AllowlistManager>.Instance),
                new RecentSendersStore(storagePath, NullLogger<RecentSendersStore>.Instance),
                AllowlistPolicy.ParseSemantics(config.Channels.AllowlistSemantics),
                NullLogger<WhatsAppWebhookHandler>.Instance));
        }
        builder.Services.AddSingleton(new ProviderUsageTracker());
        builder.Services.AddSingleton(new ToolUsageTracker());
        builder.Services.AddSingleton(new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance));
        builder.Services.AddSingleton(new ContractStore(storagePath, NullLogger<ContractStore>.Instance));
        builder.Services.AddSingleton(sp =>
        {
            var contractStartup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
                IsNonLoopbackBind = nonLoopbackBind,
                WorkspacePath = null
            };
            return new ContractGovernanceService(
                contractStartup,
                sp.GetRequiredService<ContractStore>(),
                sp.GetRequiredService<RuntimeEventStore>(),
                sp.GetRequiredService<ProviderUsageTracker>(),
                NullLogger<ContractGovernanceService>.Instance);
        });
        builder.Services.AddOpenClawMcpServices(startup);
        builder.Services.AddOpenClawBackendServices(startup);
        configureServices?.Invoke(builder.Services, config);

        var app = builder.Build();
        var runtime = CreateRuntime(config, storagePath, memoryStore, sessionManager, heartbeatService);
        app.InitializeMcpRuntime(runtime);
        app.UseOpenClawMcpAuth(startup, runtime);
        app.MapOpenApi("/openapi/{documentName}.json");
        app.MapOpenClawEndpoints(startup, runtime);
        app.MapMcp("/mcp");
        await app.StartAsync();

        return new GatewayTestHarness(app, app.GetTestClient(), runtime, config.AuthToken!, storagePath, memoryStore);
    }

    private static GatewayAppRuntime CreateRuntime(
        GatewayConfig config,
        string storagePath,
        IMemoryStore memoryStore,
        SessionManager sessionManager,
        HeartbeatService heartbeatService)
    {
        var allowlistSemantics = AllowlistPolicy.ParseSemantics(config.Channels.AllowlistSemantics);
        var allowlists = new AllowlistManager(storagePath, NullLogger<AllowlistManager>.Instance);
        var recentSenders = new RecentSendersStore(storagePath, NullLogger<RecentSendersStore>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var runtimeMetrics = new RuntimeMetrics();
        var providerUsage = new ProviderUsageTracker();
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operatorAudit = new OperatorAuditStore(storagePath, NullLogger<OperatorAuditStore>.Instance);
        var approvalGrants = new ToolApprovalGrantStore(storagePath, NullLogger<ToolApprovalGrantStore>.Instance);
        var webhookDeliveries = new WebhookDeliveryStore(storagePath, NullLogger<WebhookDeliveryStore>.Instance);
        var actorRateLimits = new ActorRateLimitService(storagePath, NullLogger<ActorRateLimitService>.Instance);
        var sessionMetadata = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
        var pluginHealth = new PluginHealthService(storagePath, NullLogger<PluginHealthService>.Instance);
        var llmExecution = new GatewayLlmExecutionService(
            config,
            providerRegistry,
            providerPolicies,
            runtimeEvents,
            runtimeMetrics,
            providerUsage,
            NullLogger<GatewayLlmExecutionService>.Instance);
        var retentionCoordinator = Substitute.For<IMemoryRetentionCoordinator>();
        retentionCoordinator.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new RetentionRunStatus { Enabled = false, StoreSupportsRetention = false }));
        retentionCoordinator.SweepNowAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new RetentionSweepResult()));

        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.CircuitBreakerState.Returns(CircuitState.Closed);
        agentRuntime.LoadedSkillNames.Returns(Array.Empty<string>());
        agentRuntime.ReloadSkillsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        var nativeRegistry = new NativePluginRegistry(config.Plugins.Native, NullLogger.Instance, config.Tooling);
        var skillWatcher = new SkillWatcherService(config.Skills, null, [], agentRuntime, NullLogger<SkillWatcherService>.Instance);

        return new GatewayAppRuntime
        {
            AgentRuntime = agentRuntime,
            OrchestratorId = RuntimeOrchestrator.Native,
            Pipeline = pipeline,
            MiddlewarePipeline = middleware,
            WebSocketChannel = wsChannel,
            ChannelAdapters = new Dictionary<string, OpenClaw.Core.Abstractions.IChannelAdapter>(StringComparer.Ordinal)
            {
                ["websocket"] = wsChannel
            },
            SessionManager = sessionManager,
            RetentionCoordinator = retentionCoordinator,
            PairingManager = new PairingManager(storagePath, NullLogger<PairingManager>.Instance),
            Allowlists = allowlists,
            AllowlistSemantics = allowlistSemantics,
            RecentSenders = recentSenders,
            CommandProcessor = commandProcessor,
            ToolApprovalService = toolApprovalService,
            ApprovalAuditStore = approvalAuditStore,
            RuntimeMetrics = runtimeMetrics,
            ProviderUsage = providerUsage,
            Heartbeat = heartbeatService,
            SkillWatcher = skillWatcher,
            PluginReports = Array.Empty<PluginLoadReport>(),
            Operations = new RuntimeOperationsState
            {
                ProviderPolicies = providerPolicies,
                ProviderRegistry = providerRegistry,
                LlmExecution = llmExecution,
                PluginHealth = pluginHealth,
                ApprovalGrants = approvalGrants,
                RuntimeEvents = runtimeEvents,
                OperatorAudit = operatorAudit,
                WebhookDeliveries = webhookDeliveries,
                ActorRateLimits = actorRateLimits,
                SessionMetadata = sessionMetadata
            },
            EffectiveRequireToolApproval = false,
            EffectiveApprovalRequiredTools = Array.Empty<string>(),
            NativeRegistry = nativeRegistry,
            SessionLocks = new ConcurrentDictionary<string, SemaphoreSlim>(),
            LockLastUsed = new ConcurrentDictionary<string, DateTimeOffset>(),
            AllowedOriginsSet = null,
            DynamicProviderOwners = Array.Empty<string>(),
            EstimatedSkillPromptChars = 0,
            CronTask = null,
            TwilioSmsWebhookHandler = null,
            PluginHost = null,
            NativeDynamicPluginHost = null,
            RegisteredToolNames = System.Collections.Frozen.FrozenSet<string>.Empty,
            ChannelAuthEvents = new ChannelAuthEventStore()
        };
    }

    private sealed record GatewayTestHarness(
        WebApplication App,
        HttpClient Client,
        GatewayAppRuntime Runtime,
        string AuthToken,
        string StoragePath,
        IMemoryStore MemoryStore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class RestartableTestChannelAdapter(string channelId) : IChannelAdapter, IRestartableChannelAdapter
    {
        public string ChannelId { get; } = channelId;
        public int RestartCount { get; private set; }

        public event Func<InboundMessage, CancellationToken, ValueTask> OnMessageReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask SendAsync(OutboundMessage message, CancellationToken ct) => ValueTask.CompletedTask;

        public Task RestartAsync(CancellationToken ct)
        {
            RestartCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingSaveMemoryStore(IMemoryStore inner) : IMemoryStore, ISessionAdminStore, ISessionSearchStore, IAsyncDisposable, IDisposable
    {
        public ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct) => inner.GetSessionAsync(sessionId, ct);
        public ValueTask SaveSessionAsync(Session session, CancellationToken ct) => throw new IOException("Simulated persistence failure.");
        public ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct) => inner.LoadNoteAsync(key, ct);
        public ValueTask SaveNoteAsync(string key, string content, CancellationToken ct) => inner.SaveNoteAsync(key, content, ct);
        public ValueTask DeleteNoteAsync(string key, CancellationToken ct) => inner.DeleteNoteAsync(key, ct);
        public ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct) => inner.ListNotesWithPrefixAsync(prefix, ct);
        public ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct) => inner.SaveBranchAsync(branch, ct);
        public ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct) => inner.LoadBranchAsync(branchId, ct);
        public ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct) => inner.ListBranchesAsync(sessionId, ct);
        public ValueTask DeleteBranchAsync(string branchId, CancellationToken ct) => inner.DeleteBranchAsync(branchId, ct);
        public ValueTask<PagedSessionList> ListSessionsAsync(int page, int pageSize, SessionListQuery query, CancellationToken ct)
            => ((ISessionAdminStore)inner).ListSessionsAsync(page, pageSize, query, ct);
        public ValueTask<SessionSearchResult> SearchSessionsAsync(SessionSearchQuery query, CancellationToken ct)
            => ((ISessionSearchStore)inner).SearchSessionsAsync(query, ct);

        public ValueTask DisposeAsync()
        {
            return inner switch
            {
                IAsyncDisposable asyncDisposable => asyncDisposable.DisposeAsync(),
                IDisposable disposable => new ValueTask(Task.Run(disposable.Dispose)),
                _ => ValueTask.CompletedTask
            };
        }

        public void Dispose()
        {
            switch (inner)
            {
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
                case IAsyncDisposable asyncDisposable:
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    break;
            }
        }
    }
}
