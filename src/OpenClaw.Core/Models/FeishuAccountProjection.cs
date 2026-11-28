namespace OpenClaw.Core.Models;

/// <summary>
/// Projects root Feishu config + <c>Accounts[key]</c> into a single-account
/// <see cref="FeishuChannelConfig"/> for <c>FeishuClient</c>.
/// </summary>
public static class FeishuAccountProjection
{
    /// <summary>
    /// Account ids to start: keys of <c>Accounts</c>, or only <c>default</c> when the map is empty.
    /// Keys are trimmed; whitespace-only keys are ignored; trimmed-key collisions throw.
    /// </summary>
    public static IReadOnlyList<string> ListAccountIds(FeishuChannelConfig root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root.Accounts.Count == 0)
            return [FeishuAccountCredentialRefs.DefaultAccountId];

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in root.Accounts.Keys)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var key = raw.Trim();
            if (!seen.Add(key))
            {
                throw new InvalidOperationException(
                    $"Duplicate Feishu Accounts key after trim: '{key}'.");
            }

            ids.Add(key);
        }

        return ids.Count > 0 ? ids : [FeishuAccountCredentialRefs.DefaultAccountId];
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
    /// Looks up an Accounts entry by trimmed account id (OrdinalIgnoreCase),
    /// including when the stored key only differs by surrounding whitespace.
    /// </summary>
    public static bool TryGetAccountEntry(
        FeishuChannelConfig root,
        string accountId,
        out FeishuChannelConfig? entry)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var key = accountId.Trim();
        if (root.Accounts.TryGetValue(key, out entry))
            return true;

        foreach (var kv in root.Accounts)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
                continue;

            if (string.Equals(kv.Key.Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                entry = kv.Value;
                return true;
            }
        }

        entry = null;
        return false;
    }

    /// <summary>
    /// Projects one account: credentials via <see cref="FeishuAccountCredentialRefs"/>;
    /// omitted account fields inherit from root; explicit values override (including defaults);
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
                GroupPolicy = EffectiveGroupPolicy(root.GroupPolicy),
                AllowedFromUserIds = root.AllowedFromUserIds,
                AllowedGroupIds = root.AllowedGroupIds,
                MaxInboundChars = root.MaxInboundChars ?? 4096,
                RequireMentionInGroup = root.RequireMentionInGroup ?? false,
                ExposeInboundMediaUrls = root.ExposeInboundMediaUrls ?? true,
                Accounts = new Dictionary<string, FeishuChannelConfig>(StringComparer.OrdinalIgnoreCase)
            };
        }

        TryGetAccountEntry(root, key, out var entry);
        entry ??= new FeishuChannelConfig();

        // null/omitted on the account → inherit root; explicit values (including false/"open"/4096) win.
        var groupPolicy = string.IsNullOrWhiteSpace(entry.GroupPolicy)
            ? EffectiveGroupPolicy(root.GroupPolicy)
            : entry.GroupPolicy.Trim();
        var allowedFrom = entry.AllowedFromUserIds is { Length: > 0 }
            ? entry.AllowedFromUserIds
            : root.AllowedFromUserIds;
        var allowedGroups = entry.AllowedGroupIds is { Length: > 0 }
            ? entry.AllowedGroupIds
            : root.AllowedGroupIds;
        var maxChars = entry.MaxInboundChars ?? root.MaxInboundChars ?? 4096;
        var requireMention = entry.RequireMentionInGroup ?? root.RequireMentionInGroup ?? false;
        var exposeMedia = entry.ExposeInboundMediaUrls ?? root.ExposeInboundMediaUrls ?? true;

        return new FeishuChannelConfig
        {
            // Account entries: present in the map ⇒ start unless root disabled.
            // Disable by removing the key (Enabled alone is ambiguous with type defaults).
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

    /// <summary>Root group policy with null/blank treated as <c>open</c>.</summary>
    public static string EffectiveGroupPolicy(string? groupPolicy)
        => string.IsNullOrWhiteSpace(groupPolicy) ? "open" : groupPolicy.Trim();

    /// <summary>
    /// Account-map entries are enabled when marked Enabled, empty shells (inherit-all),
    /// entries with own credentials, or entries that only override policy (env-backed creds).
    /// Disable by removing the key from <c>Accounts</c>.
    /// </summary>
    private static bool IsAccountEntryEnabled(FeishuChannelConfig entry)
        => entry.Enabled
           || IsAccountEntryShell(entry)
           || HasOwnCredentials(entry)
           || HasPolicyOverride(entry);

    private static bool HasOwnCredentials(FeishuChannelConfig entry)
        => !string.IsNullOrWhiteSpace(entry.AppId)
           || !string.IsNullOrWhiteSpace(entry.AppSecret)
           || !FeishuAccountCredentialRefs.IsRootDefaultAppIdRef(entry.AppIdRef)
           || !FeishuAccountCredentialRefs.IsRootDefaultAppSecretRef(entry.AppSecretRef);

    private static bool HasPolicyOverride(FeishuChannelConfig entry)
        => !string.IsNullOrWhiteSpace(entry.GroupPolicy)
           || entry.AllowedFromUserIds.Length > 0
           || entry.AllowedGroupIds.Length > 0
           || entry.MaxInboundChars is not null
           || entry.RequireMentionInGroup is not null
           || entry.ExposeInboundMediaUrls is not null;

    private static bool IsAccountEntryShell(FeishuChannelConfig entry)
        => !HasOwnCredentials(entry)
           && !HasPolicyOverride(entry)
           && entry.Accounts.Count == 0;
}
