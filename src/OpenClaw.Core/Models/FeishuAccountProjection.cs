namespace OpenClaw.Core.Models;

/// <summary>
/// Projects root Feishu config + <c>Accounts[key]</c> into a single-account
/// <see cref="FeishuChannelConfig"/> for <c>FeishuClient</c>.
/// </summary>
public static class FeishuAccountProjection
{
    /// <summary>
    /// Account ids to start: keys of <c>Accounts</c>, or only <c>default</c> when the map is empty.
    /// </summary>
    public static IReadOnlyList<string> ListAccountIds(FeishuChannelConfig root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root.Accounts.Count == 0)
            return [FeishuAccountCredentialRefs.DefaultAccountId];

        return root.Accounts.Keys
            .Where(static k => !string.IsNullOrWhiteSpace(k))
            .Select(static k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Outbound account id: message value, else <c>default</c> if present,
    /// else first map key, else <c>default</c>.
    /// </summary>
    public static string ResolveOutboundAccountId(FeishuChannelConfig root, string? messageAccountId)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!string.IsNullOrWhiteSpace(messageAccountId))
            return messageAccountId.Trim();

        var ids = ListAccountIds(root);
        var defaultId = FeishuAccountCredentialRefs.DefaultAccountId;
        if (ids.Any(id => string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)))
            return defaultId;

        return ids.Count > 0 ? ids[0] : defaultId;
    }

    /// <summary>
    /// Projects one account: credentials via <see cref="FeishuAccountCredentialRefs"/>;
    /// policy inherits from root unless the account overrides relative to type defaults;
    /// result <c>Accounts</c> is always empty.
    /// </summary>
    public static FeishuChannelConfig Project(FeishuChannelConfig root, string accountId)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var key = accountId.Trim();
        var creds = FeishuAccountCredentialRefs.Resolve(root, key);

        if (FeishuAccountCredentialRefs.IsDefaultAccount(key))
        {
            return new FeishuChannelConfig
            {
                Enabled = root.Enabled,
                AppId = creds.AppId,
                AppIdRef = creds.AppIdRef,
                AppSecret = creds.AppSecret,
                AppSecretRef = creds.AppSecretRef,
                GroupPolicy = root.GroupPolicy,
                AllowedFromUserIds = root.AllowedFromUserIds,
                AllowedGroupIds = root.AllowedGroupIds,
                MaxInboundChars = root.MaxInboundChars,
                RequireMentionInGroup = root.RequireMentionInGroup,
                ExposeInboundMediaUrls = root.ExposeInboundMediaUrls,
                Accounts = new Dictionary<string, FeishuChannelConfig>(StringComparer.OrdinalIgnoreCase)
            };
        }

        root.Accounts.TryGetValue(key, out var entry);
        entry ??= new FeishuChannelConfig();

        // Policy: start from root; account overrides only when not still at type defaults (empty {} keeps root).
        var groupPolicy = IsDefaultGroupPolicy(entry.GroupPolicy) ? root.GroupPolicy : entry.GroupPolicy.Trim();
        var allowedFrom = entry.AllowedFromUserIds.Length > 0 ? entry.AllowedFromUserIds : root.AllowedFromUserIds;
        var allowedGroups = entry.AllowedGroupIds.Length > 0 ? entry.AllowedGroupIds : root.AllowedGroupIds;
        var maxChars = entry.MaxInboundChars != 4096 ? entry.MaxInboundChars : root.MaxInboundChars;
        var requireMention = entry.RequireMentionInGroup || root.RequireMentionInGroup;
        // ExposeInboundMediaUrls defaults to true; only an explicit false on the account overrides.
        var exposeMedia = entry.ExposeInboundMediaUrls ? root.ExposeInboundMediaUrls : false;

        return new FeishuChannelConfig
        {
            // Account entries default Enabled=false; treat empty {} as enabled.
            // Disable by removing the key (explicit false with credentials is ambiguous).
            Enabled = root.Enabled && IsAccountEntryEnabled(entry),
            AppId = creds.AppId,
            AppIdRef = creds.AppIdRef,
            AppSecret = creds.AppSecret,
            AppSecretRef = creds.AppSecretRef,
            GroupPolicy = groupPolicy,
            AllowedFromUserIds = allowedFrom,
            AllowedGroupIds = allowedGroups,
            MaxInboundChars = maxChars,
            RequireMentionInGroup = requireMention,
            ExposeInboundMediaUrls = exposeMedia,
            Accounts = new Dictionary<string, FeishuChannelConfig>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool IsDefaultGroupPolicy(string? groupPolicy)
        => string.IsNullOrWhiteSpace(groupPolicy)
           || string.Equals(groupPolicy.Trim(), "open", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Account-map Enabled defaults to false (same as the root type). JSON often omits the field.
    /// Treat empty shells / entries with own credentials as enabled; <c>enabled: true</c> also enables.
    /// Explicit false with credentials cannot be distinguished reliably — remove the key to disable.
    /// </summary>
    private static bool IsAccountEntryEnabled(FeishuChannelConfig entry)
        => entry.Enabled || IsAccountEntryShell(entry) || HasOwnCredentials(entry);

    private static bool HasOwnCredentials(FeishuChannelConfig entry)
        => !string.IsNullOrWhiteSpace(entry.AppId)
           || !string.IsNullOrWhiteSpace(entry.AppSecret)
           || !FeishuAccountCredentialRefs.IsRootDefaultAppIdRef(entry.AppIdRef)
           || !FeishuAccountCredentialRefs.IsRootDefaultAppSecretRef(entry.AppSecretRef);

    private static bool IsAccountEntryShell(FeishuChannelConfig entry)
        => !HasOwnCredentials(entry)
           && IsDefaultGroupPolicy(entry.GroupPolicy)
           && entry.AllowedFromUserIds.Length == 0
           && entry.AllowedGroupIds.Length == 0
           && entry.MaxInboundChars == 4096
           && !entry.RequireMentionInGroup
           && entry.ExposeInboundMediaUrls
           && entry.Accounts.Count == 0;
}
