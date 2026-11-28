namespace OpenClaw.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using Xunit;

/// <summary>Tests for FeishuChannelConfig.Accounts (official map shape).</summary>
public sealed class FeishuAccountsConfigTests
{
    /// <summary>Official JSON with default empty object and another account should deserialize.</summary>
    [Fact]
    public void Deserialize_WhenAccountsMapWithDefaultEmpty_ShouldBindDictionary()
    {
        const string json = """
            {
              "enabled": true,
              "appId": "cli_root",
              "appSecret": "sec_root",
              "groupPolicy": "open",
              "accounts": {
                "default": {},
                "yixiu-duoaiteam": {
                  "appId": "cli_yixiu",
                  "appSecret": "sec_yixiu"
                }
              }
            }
            """;

        var cfg = JsonSerializer.Deserialize(json, CoreJsonContext.Default.FeishuChannelConfig);
        Assert.NotNull(cfg);
        Assert.Equal("cli_root", cfg!.AppId);
        Assert.True(cfg.Accounts.ContainsKey("default"));
        Assert.NotNull(cfg.Accounts["default"]);
        Assert.Null(cfg.Accounts["default"].AppId); // {} inherits root; this step only asserts binding
        Assert.Equal("cli_yixiu", cfg.Accounts["yixiu-duoaiteam"].AppId);
        Assert.Equal("sec_yixiu", cfg.Accounts["yixiu-duoaiteam"].AppSecret);
    }

    /// <summary>default empty object should resolve to root credentials and root refs.</summary>
    [Fact]
    public void ResolveCredentials_WhenDefaultEmpty_ShouldUseRootRefs()
    {
        var root = new FeishuChannelConfig
        {
            AppId = "cli_root",
            AppSecret = "sec_root",
            AppIdRef = "env:FEISHU_APP_ID",
            AppSecretRef = "env:FEISHU_APP_SECRET",
            Accounts = { ["default"] = new FeishuChannelConfig() }
        };

        var resolved = FeishuAccountCredentialRefs.Resolve(root, "default");
        Assert.Equal("cli_root", resolved.AppId);
        Assert.Equal("sec_root", resolved.AppSecret);
        Assert.Equal(FeishuAccountCredentialRefs.RootAppIdRef, resolved.AppIdRef);
        Assert.Equal(FeishuAccountCredentialRefs.RootAppSecretRef, resolved.AppSecretRef);
    }

    /// <summary>Named account without direct values should use env:FEISHU_*_{key} (hyphen kept).</summary>
    [Fact]
    public void ResolveCredentials_WhenNamedAccountEmpty_ShouldUseKeyedEnvRefs()
    {
        var root = new FeishuChannelConfig
        {
            AppId = "cli_root",
            AppSecret = "sec_root",
            Accounts = { ["yixiu-duoaiteam"] = new FeishuChannelConfig() }
        };

        var resolved = FeishuAccountCredentialRefs.Resolve(root, "yixiu-duoaiteam");
        Assert.Null(resolved.AppId);
        Assert.Null(resolved.AppSecret);
        Assert.Equal("env:FEISHU_APP_ID_yixiu-duoaiteam", resolved.AppIdRef);
        Assert.Equal("env:FEISHU_APP_SECRET_yixiu-duoaiteam", resolved.AppSecretRef);
    }

    /// <summary>Direct AppId on an account should clear root-default refs.</summary>
    [Fact]
    public void ResolveCredentials_WhenAccountHasDirectAppId_ShouldClearRootDefaultRef()
    {
        var root = new FeishuChannelConfig
        {
            Accounts =
            {
                ["yixiu-duoaiteam"] = new FeishuChannelConfig
                {
                    AppId = "cli_yixiu",
                    AppSecret = "sec_yixiu"
                    // AppIdRef remains the type default env:FEISHU_APP_ID
                }
            }
        };

        var resolved = FeishuAccountCredentialRefs.Resolve(root, "yixiu-duoaiteam");
        Assert.Equal("cli_yixiu", resolved.AppId);
        Assert.Equal("sec_yixiu", resolved.AppSecret);
        Assert.Equal(string.Empty, resolved.AppIdRef);
        Assert.Equal(string.Empty, resolved.AppSecretRef);
    }

    /// <summary>Explicit account refs should be preserved.</summary>
    [Fact]
    public void ResolveCredentials_WhenAccountHasExplicitRef_ShouldKeepIt()
    {
        var root = new FeishuChannelConfig
        {
            Accounts =
            {
                ["bot-a"] = new FeishuChannelConfig
                {
                    AppIdRef = "env:CUSTOM_FEISHU_ID",
                    AppSecretRef = "env:CUSTOM_FEISHU_SECRET"
                }
            }
        };

        var resolved = FeishuAccountCredentialRefs.Resolve(root, "bot-a");
        Assert.Equal("env:CUSTOM_FEISHU_ID", resolved.AppIdRef);
        Assert.Equal("env:CUSTOM_FEISHU_SECRET", resolved.AppSecretRef);
    }

    /// <summary>Projecting default should copy root policy/credentials and clear Accounts.</summary>
    [Fact]
    public void Project_WhenDefault_ShouldCopyRootPolicyAndClearAccounts()
    {
        var root = new FeishuChannelConfig
        {
            Enabled = true,
            AppId = "cli_root",
            AppSecret = "sec_root",
            GroupPolicy = "allowlist",
            AllowedGroupIds = ["oc_x"],
            RequireMentionInGroup = true,
            MaxInboundChars = 2048,
            Accounts =
            {
                ["default"] = new FeishuChannelConfig(),
                ["yixiu-duoaiteam"] = new FeishuChannelConfig { AppId = "cli_y", AppSecret = "sec_y" }
            }
        };

        var projected = FeishuAccountProjection.Project(root, "default");
        Assert.True(projected.Enabled);
        Assert.Equal("cli_root", projected.AppId);
        Assert.Equal("allowlist", projected.GroupPolicy);
        Assert.Equal(["oc_x"], projected.AllowedGroupIds);
        Assert.True(projected.RequireMentionInGroup);
        Assert.Equal(2048, projected.MaxInboundChars);
        Assert.Empty(projected.Accounts);
    }

    /// <summary>Named account projection inherits root policy and applies direct credentials.</summary>
    [Fact]
    public void Project_WhenNamedAccount_ShouldInheritRootPolicyAndApplyCredentials()
    {
        var root = new FeishuChannelConfig
        {
            Enabled = true,
            AppId = "cli_root",
            AppSecret = "sec_root",
            GroupPolicy = "allowlist",
            AllowedGroupIds = ["oc_root"],
            RequireMentionInGroup = true,
            Accounts =
            {
                ["yixiu-duoaiteam"] = new FeishuChannelConfig
                {
                    AppId = "cli_yixiu",
                    AppSecret = "sec_yixiu",
                    RequireMentionInGroup = true
                }
            }
        };

        var projected = FeishuAccountProjection.Project(root, "yixiu-duoaiteam");
        Assert.True(projected.Enabled);
        Assert.Equal("cli_yixiu", projected.AppId);
        Assert.Equal(string.Empty, projected.AppIdRef);
        Assert.Equal("allowlist", projected.GroupPolicy);
        Assert.Equal(["oc_root"], projected.AllowedGroupIds);
        Assert.True(projected.RequireMentionInGroup);
        Assert.Empty(projected.Accounts);
    }

    /// <summary>Empty Accounts map lists only default.</summary>
    [Fact]
    public void ListAccountIds_WhenAccountsEmpty_ShouldReturnDefaultOnly()
    {
        var ids = FeishuAccountProjection.ListAccountIds(new FeishuChannelConfig());
        Assert.Equal([FeishuAccountCredentialRefs.DefaultAccountId], ids);
    }

    /// <summary>StartAsync with root disabled should complete without throwing.</summary>
    [Fact]
    public async Task MultiAccount_StartAsync_WhenRootDisabled_ShouldComplete()
    {
        var root = new FeishuChannelConfig
        {
            Enabled = false,
            Accounts =
            {
                ["default"] = new FeishuChannelConfig(),
                ["yixiu-duoaiteam"] = new FeishuChannelConfig { AppId = "cli_y", AppSecret = "sec_y" }
            }
        };

        await using var multi = new FeishuChannel(root, NullLoggerFactory.Instance);
        await multi.StartAsync(CancellationToken.None);
    }

    /// <summary>SendAsync for an unknown AccountId should throw unavailable.</summary>
    [Fact]
    public async Task MultiAccount_SendAsync_WhenAccountUnknown_ShouldThrowUnavailable()
    {
        var root = new FeishuChannelConfig
        {
            Enabled = false,
            Accounts =
            {
                ["default"] = new FeishuChannelConfig(),
                ["yixiu-duoaiteam"] = new FeishuChannelConfig { AppId = "cli_y", AppSecret = "sec_y" }
            }
        };

        await using var multi = new FeishuChannel(root, NullLoggerFactory.Instance);
        await multi.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ChannelDeliveryException>(async () =>
            await multi.SendAsync(new OutboundMessage
            {
                ChannelId = "feishu",
                RecipientId = "ou_test",
                Text = "ping",
                AccountId = "no-such-bot"
            }, CancellationToken.None));

        Assert.Contains("is not available", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>SetRuntimeConfig(null) should revert to the initial Accounts map.</summary>
    [Fact]
    public void MultiAccount_SetRuntimeConfig_WhenNull_ShouldRevertToInitial()
    {
        var initial = new FeishuChannelConfig
        {
            Enabled = false,
            AppId = "cli_initial",
            Accounts = { ["default"] = new FeishuChannelConfig() }
        };

        var multi = new FeishuChannel(initial, NullLoggerFactory.Instance);
        multi.SetRuntimeConfig(new FeishuChannelConfig
        {
            Enabled = false,
            AppId = "cli_override",
            Accounts = { ["other"] = new FeishuChannelConfig { AppId = "cli_o", AppSecret = "sec_o" } }
        });
        Assert.Equal("cli_override", multi.GetEffectiveConfig().AppId);

        multi.SetRuntimeConfig(null);
        Assert.Equal("cli_initial", multi.GetEffectiveConfig().AppId);
        Assert.True(multi.GetEffectiveConfig().Accounts.ContainsKey("default"));
    }

    /// <summary>Whitespace in Accounts keys should still resolve credentials and policy.</summary>
    [Fact]
    public void Project_WhenAccountKeyHasWhitespace_ShouldFindEntry()
    {
        var root = new FeishuChannelConfig
        {
            Enabled = true,
            RequireMentionInGroup = true,
            Accounts =
            {
                [" bot-a "] = new FeishuChannelConfig
                {
                    AppId = "cli_a",
                    AppSecret = "sec_a",
                    RequireMentionInGroup = false
                }
            }
        };

        Assert.Equal(["bot-a"], FeishuAccountProjection.ListAccountIds(root));
        var projected = FeishuAccountProjection.Project(root, "bot-a");
        Assert.True(projected.Enabled);
        Assert.Equal("cli_a", projected.AppId);
        Assert.False(projected.RequireMentionInGroup);
    }

    /// <summary>Policy-only account entries stay enabled for env-backed credentials.</summary>
    [Fact]
    public void Project_WhenPolicyOnlyAccount_ShouldStayEnabled()
    {
        var root = new FeishuChannelConfig
        {
            Enabled = true,
            Accounts =
            {
                ["env-bot"] = new FeishuChannelConfig
                {
                    RequireMentionInGroup = true
                }
            }
        };

        var projected = FeishuAccountProjection.Project(root, "env-bot");
        Assert.True(projected.Enabled);
        Assert.True(projected.RequireMentionInGroup);
        Assert.Equal("env:FEISHU_APP_ID_env-bot", projected.AppIdRef);
    }

    /// <summary>Explicit false on an account overrides root RequireMentionInGroup=true.</summary>
    [Fact]
    public void Project_WhenAccountSetsRequireMentionFalse_ShouldOverrideRoot()
    {
        var root = new FeishuChannelConfig
        {
            Enabled = true,
            RequireMentionInGroup = true,
            Accounts =
            {
                ["bot-a"] = new FeishuChannelConfig
                {
                    AppId = "cli_a",
                    AppSecret = "sec_a",
                    RequireMentionInGroup = false
                }
            }
        };

        var projected = FeishuAccountProjection.Project(root, "bot-a");
        Assert.False(projected.RequireMentionInGroup);
    }

    /// <summary>Trimmed-key collisions in Accounts should throw.</summary>
    [Fact]
    public void ListAccountIds_WhenTrimmedKeysCollide_ShouldThrow()
    {
        var root = new FeishuChannelConfig
        {
            Accounts =
            {
                ["bot"] = new FeishuChannelConfig(),
                [" bot "] = new FeishuChannelConfig()
            }
        };

        Assert.Throws<InvalidOperationException>(() => FeishuAccountProjection.ListAccountIds(root));
    }
}
