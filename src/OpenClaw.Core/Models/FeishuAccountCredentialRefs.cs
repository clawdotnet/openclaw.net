namespace OpenClaw.Core.Models;

/// <summary>
/// Resolves Feishu app credentials per account id.
/// Root/<c>default</c> uses the root refs; other keys default to <c>env:FEISHU_*_{key}</c> (key kept as-is, including hyphens).
/// </summary>
public static class FeishuAccountCredentialRefs
{
    /// <summary>Same default as <see cref="FeishuChannelConfig.AppIdRef"/>.</summary>
    public const string RootAppIdRef = "env:FEISHU_APP_ID";

    /// <summary>Same default as <see cref="FeishuChannelConfig.AppSecretRef"/>.</summary>
    public const string RootAppSecretRef = "env:FEISHU_APP_SECRET";

    /// <summary>Account map key that means "use the root Feishu config".</summary>
    public const string DefaultAccountId = "default";

    /// <summary>AccountId → env suffix (trimmed; hyphens preserved).</summary>
    public static string ToEnvKeySuffix(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return accountId.Trim();
    }

    /// <summary>Default AppIdRef for a non-default account.</summary>
    public static string BuildAccountAppIdRef(string accountId)
        => $"env:FEISHU_APP_ID_{ToEnvKeySuffix(accountId)}";

    /// <summary>Default AppSecretRef for a non-default account.</summary>
    public static string BuildAccountAppSecretRef(string accountId)
        => $"env:FEISHU_APP_SECRET_{ToEnvKeySuffix(accountId)}";

    /// <summary>
    /// Resolves AppId/AppSecret and refs for one account before constructing <c>FeishuClient</c>.
    /// <list type="bullet">
    /// <item><c>default</c> or missing entry → root fields</item>
    /// <item>Direct AppId/Secret on the account → use them and clear root-default refs so root env does not win</item>
    /// <item>No direct values and ref still default/empty → <c>env:FEISHU_*_{key}</c></item>
    /// <item>Explicit non-default refs in JSON → keep them</item>
    /// </list>
    /// </summary>
    public static FeishuAccountCredentialResolution Resolve(
        FeishuChannelConfig root,
        string accountId,
        FeishuChannelConfig? accountEntry = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var key = accountId.Trim();
        if (IsDefaultAccount(key))
        {
            return new FeishuAccountCredentialResolution(
                key,
                root.AppId,
                root.AppIdRef,
                root.AppSecret,
                root.AppSecretRef);
        }

        root.Accounts.TryGetValue(key, out var fromMap);
        var entry = accountEntry ?? fromMap;
        if (entry is null)
        {
            // No map entry: still emit keyed env refs so env-only accounts can start.
            return new FeishuAccountCredentialResolution(
                key,
                AppId: null,
                AppIdRef: BuildAccountAppIdRef(key),
                AppSecret: null,
                AppSecretRef: BuildAccountAppSecretRef(key));
        }

        var appId = entry.AppId;
        var appSecret = entry.AppSecret;
        var appIdRef = entry.AppIdRef;
        var appSecretRef = entry.AppSecretRef;

        if (!string.IsNullOrWhiteSpace(appId))
        {
            // Direct values win: clear root-default refs so SecretResolver(env:FEISHU_APP_ID) cannot override.
            if (IsRootDefaultAppIdRef(appIdRef))
                appIdRef = string.Empty;
        }
        else if (IsRootDefaultAppIdRef(appIdRef) || string.IsNullOrWhiteSpace(appIdRef))
        {
            appIdRef = BuildAccountAppIdRef(key);
        }

        if (!string.IsNullOrWhiteSpace(appSecret))
        {
            if (IsRootDefaultAppSecretRef(appSecretRef))
                appSecretRef = string.Empty;
        }
        else if (IsRootDefaultAppSecretRef(appSecretRef) || string.IsNullOrWhiteSpace(appSecretRef))
        {
            appSecretRef = BuildAccountAppSecretRef(key);
        }

        return new FeishuAccountCredentialResolution(key, appId, appIdRef, appSecret, appSecretRef);
    }

    /// <summary>Returns true when the account id is the default/root account.</summary>
    public static bool IsDefaultAccount(string? accountId)
        => string.IsNullOrWhiteSpace(accountId)
           || string.Equals(accountId.Trim(), DefaultAccountId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true when AppIdRef is still the class default (or empty).</summary>
    public static bool IsRootDefaultAppIdRef(string? appIdRef)
        => string.IsNullOrWhiteSpace(appIdRef)
           || string.Equals(appIdRef.Trim(), RootAppIdRef, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true when AppSecretRef is still the class default (or empty).</summary>
    public static bool IsRootDefaultAppSecretRef(string? appSecretRef)
        => string.IsNullOrWhiteSpace(appSecretRef)
           || string.Equals(appSecretRef.Trim(), RootAppSecretRef, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Resolved credentials for one account, ready to project into <see cref="FeishuChannelConfig"/>.</summary>
public readonly record struct FeishuAccountCredentialResolution(
    string AccountId,
    string? AppId,
    string AppIdRef,
    string? AppSecret,
    string AppSecretRef);
