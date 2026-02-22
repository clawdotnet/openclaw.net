using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native "inbox-zero" plugin inspired by paperMoose/inbox-zero.
/// Provides AI-driven email triage: analyze, categorize, cleanup, trash-sender, and spam-rescue.
/// Works with any IMAP provider (Gmail, Outlook, ProtonMail Bridge, Fastmail, etc.).
/// </summary>
public sealed class InboxZeroTool : ITool, IDisposable
{
    private readonly InboxZeroConfig _config;
    private readonly EmailConfig _emailConfig;

    // Built-in protected domains (banks, healthcare, travel, major tech)
    private static readonly HashSet<string> BuiltInProtectedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "chase.com", "capitalone.com", "citi.com", "americanexpress.com",
        "wellsfargo.com", "bankofamerica.com", "paypal.com", "venmo.com",
        "mercury.com", "coinbase.com", "robinhood.com", "schwab.com",
        "google.com", "github.com", "apple.com", "microsoft.com",
        "united.com", "delta.com", "southwest.com", "aa.com",
        "uber.com", "lyft.com", "doordash.com",
        "irs.gov", "ssa.gov", "healthcare.gov"
    };

    private static readonly string[] ReceiptPatterns =
        ["payment", "invoice", "order", "shipping", "delivery", "receipt", "transaction", "purchase", "refund"];
    private static readonly string[] ConfirmationPatterns =
        ["confirmation", "confirmed", "appointment", "reservation", "booking", "registration", "scheduled"];
    private static readonly string[] PromoPatterns =
        ["sale", "% off", "deal", "limited time", "exclusive offer", "discount", "promo", "clearance", "shop now",
         "buy now", "free shipping", "unbeatable", "save big"];

    public InboxZeroTool(InboxZeroConfig config, EmailConfig emailConfig)
    {
        _config = config;
        _emailConfig = emailConfig;
    }

    public string Name => "inbox_zero";
    public string Description =>
        "AI-powered email triage tool. Analyze, categorize, clean up, and rescue emails. " +
        "Works with any IMAP email provider. Actions: analyze, cleanup, trash-sender, spam-rescue, categorize.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "description": "Action to perform",
              "enum": ["analyze", "cleanup", "trash-sender", "spam-rescue", "categorize"]
            },
            "sender": {
              "type": "string",
              "description": "Sender email address (required for trash-sender action)"
            },
            "folder": {
              "type": "string",
              "description": "IMAP folder to operate on (default: INBOX)",
              "default": "INBOX"
            },
            "count": {
              "type": "integer",
              "description": "Number of emails to process (default: 50, max: configured MaxBatchSize)",
              "default": 50
            }
          },
          "required": ["action"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var action = args.RootElement.GetProperty("action").GetString()!.ToLowerInvariant();

        return action switch
        {
            "analyze" => await AnalyzeAsync(args.RootElement, ct),
            "cleanup" => await CleanupAsync(args.RootElement, ct),
            "trash-sender" => await TrashBySenderAsync(args.RootElement, ct),
            "spam-rescue" => await SpamRescueAsync(args.RootElement, ct),
            "categorize" => await AnalyzeAsync(args.RootElement, ct), // alias
            _ => $"Error: Unknown action '{action}'. Use: analyze, cleanup, trash-sender, spam-rescue, categorize."
        };
    }

    // ── Actions ──────────────────────────────────────────────────────

    private async ValueTask<string> AnalyzeAsync(JsonElement args, CancellationToken ct)
    {
        var folder = args.TryGetProperty("folder", out var f) ? f.GetString() ?? "INBOX" : "INBOX";
        var count = GetCount(args);

        return await ExecuteImapAsync(async (reader, writer, cancellation) =>
        {
            await ImapSelectAsync(writer, reader, folder, cancellation);
            var total = await ImapGetMessageCountAsync(writer, reader, folder, cancellation);
            if (total == 0) return $"Folder '{folder}' is empty. Nothing to analyze.";

            var startMsg = Math.Max(1, total - count + 1);
            var categories = new Dictionary<string, List<string>>();
            var summary = new StringBuilder();
            summary.AppendLine($"## Inbox Analysis: {folder}");
            summary.AppendLine($"Total messages: {total} | Analyzing: {Math.Min(count, total)} most recent");
            summary.AppendLine();

            for (var i = total; i >= startMsg; i--)
            {
                var email = await ImapFetchHeadersExtendedAsync(writer, reader, i, cancellation);
                var category = CategorizeEmail(email);

                if (!categories.ContainsKey(category))
                    categories[category] = [];
                categories[category].Add($"[{i}] {email.From}: {email.Subject}");
            }

            // Summary counts
            summary.AppendLine("### Category Breakdown");
            foreach (var (cat, emails) in categories.OrderBy(c => GetCategoryPriority(c.Key)))
            {
                summary.AppendLine($"- **{cat}**: {emails.Count} email(s)");
            }
            summary.AppendLine();

            // Actionable recommendations
            var archivable = (categories.GetValueOrDefault("Newsletter")?.Count ?? 0)
                           + (categories.GetValueOrDefault("Promotional")?.Count ?? 0)
                           + (categories.GetValueOrDefault("Automated")?.Count ?? 0);
            if (archivable > 0)
            {
                summary.AppendLine($"### Recommendation");
                summary.AppendLine($"**{archivable} emails** can be safely archived/cleaned up (Newsletters, Promotions, Automated).");
                summary.AppendLine(_config.DryRun
                    ? "Run `cleanup` action to see what would be archived. DryRun is currently **ON** (safe mode)."
                    : "Run `cleanup` action to archive them. ⚠️ DryRun is **OFF** — changes will be applied.");
            }

            // Detailed listing
            summary.AppendLine();
            summary.AppendLine("### Details");
            foreach (var (cat, emails) in categories.OrderBy(c => GetCategoryPriority(c.Key)))
            {
                summary.AppendLine($"\n**{cat}** ({emails.Count}):");
                foreach (var e in emails.Take(10))
                    summary.AppendLine($"  {e}");
                if (emails.Count > 10)
                    summary.AppendLine($"  ... and {emails.Count - 10} more");
            }

            return summary.ToString();
        }, ct);
    }

    private async ValueTask<string> CleanupAsync(JsonElement args, CancellationToken ct)
    {
        var folder = args.TryGetProperty("folder", out var f) ? f.GetString() ?? "INBOX" : "INBOX";
        var count = GetCount(args);

        return await ExecuteImapAsync(async (reader, writer, cancellation) =>
        {
            await ImapSelectAsync(writer, reader, folder, cancellation);
            var total = await ImapGetMessageCountAsync(writer, reader, folder, cancellation);
            if (total == 0) return $"Folder '{folder}' is empty. Nothing to clean up.";

            var startMsg = Math.Max(1, total - count + 1);
            var toArchive = new List<(int MsgNum, string Category, string Desc)>();
            var kept = new List<(int MsgNum, string Category, string Desc)>();

            for (var i = total; i >= startMsg; i--)
            {
                var email = await ImapFetchHeadersExtendedAsync(writer, reader, i, cancellation);
                var category = CategorizeEmail(email);

                if (category is "Newsletter" or "Promotional" or "Automated")
                    toArchive.Add((i, category, $"{email.From}: {email.Subject}"));
                else
                    kept.Add((i, category, $"{email.From}: {email.Subject}"));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"## Cleanup Report: {folder}");
            sb.AppendLine($"Scanned: {Math.Min(count, total)} emails");
            sb.AppendLine($"To archive: {toArchive.Count} | Kept: {kept.Count}");
            sb.AppendLine();

            if (toArchive.Count == 0)
            {
                sb.AppendLine("✅ Nothing to clean up — your inbox is already tidy!");
                return sb.ToString();
            }

            if (_config.DryRun)
            {
                sb.AppendLine("### 🔍 DRY RUN — No changes made");
                sb.AppendLine("The following emails **would** be archived:");
                sb.AppendLine();
                foreach (var (msgNum, cat, desc) in toArchive)
                    sb.AppendLine($"  [{msgNum}] ({cat}) {desc}");
                sb.AppendLine();
                sb.AppendLine("Set `InboxZero.DryRun=false` in config to apply changes.");
            }
            else
            {
                // Actually archive by adding \\Seen flag and moving to archive
                foreach (var (msgNum, _, _) in toArchive)
                {
                    await ImapStoreFlag(writer, reader, msgNum, "\\Seen", cancellation);
                    await ImapMoveToArchive(writer, reader, msgNum, cancellation);
                }

                sb.AppendLine($"### ✅ Archived {toArchive.Count} emails");
                foreach (var (msgNum, cat, desc) in toArchive.Take(20))
                    sb.AppendLine($"  [{msgNum}] ({cat}) {desc}");
                if (toArchive.Count > 20)
                    sb.AppendLine($"  ... and {toArchive.Count - 20} more");
            }

            return sb.ToString();
        }, ct);
    }

    private async ValueTask<string> TrashBySenderAsync(JsonElement args, CancellationToken ct)
    {
        var sender = args.TryGetProperty("sender", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(sender))
            return "Error: 'sender' is required for trash-sender action.";

        var folder = args.TryGetProperty("folder", out var f) ? f.GetString() ?? "INBOX" : "INBOX";
        var count = GetCount(args);

        return await ExecuteImapAsync(async (reader, writer, cancellation) =>
        {
            await ImapSelectAsync(writer, reader, folder, cancellation);
            var total = await ImapGetMessageCountAsync(writer, reader, folder, cancellation);
            if (total == 0) return $"Folder '{folder}' is empty.";

            var startMsg = Math.Max(1, total - count + 1);
            var toTrash = new List<(int MsgNum, string Subject)>();

            for (var i = total; i >= startMsg; i--)
            {
                var email = await ImapFetchHeadersExtendedAsync(writer, reader, i, cancellation);
                if (email.From.Contains(sender, StringComparison.OrdinalIgnoreCase))
                    toTrash.Add((i, email.Subject));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"## Trash by Sender: {sender}");

            if (toTrash.Count == 0)
            {
                sb.AppendLine($"No emails found from '{sender}' in the last {count} messages.");
                return sb.ToString();
            }

            if (_config.DryRun)
            {
                sb.AppendLine($"### 🔍 DRY RUN — {toTrash.Count} emails **would** be trashed:");
                foreach (var (_, subj) in toTrash.Take(20))
                    sb.AppendLine($"  - {subj}");
                if (toTrash.Count > 20)
                    sb.AppendLine($"  ... and {toTrash.Count - 20} more");
                sb.AppendLine();
                sb.AppendLine("Set `InboxZero.DryRun=false` in config to apply.");
            }
            else
            {
                foreach (var (msgNum, _) in toTrash)
                    await ImapStoreFlag(writer, reader, msgNum, "\\Deleted", cancellation);

                // Expunge to actually delete
                await writer.WriteLineAsync("A_EXP EXPUNGE".AsMemory(), cancellation);
                await writer.FlushAsync(cancellation);
                await ReadUntilTagAsync(reader, "A_EXP", cancellation);

                sb.AppendLine($"### 🗑️ Trashed {toTrash.Count} emails from '{sender}'");
                foreach (var (_, subj) in toTrash.Take(20))
                    sb.AppendLine($"  - {subj}");
            }

            return sb.ToString();
        }, ct);
    }

    private async ValueTask<string> SpamRescueAsync(JsonElement args, CancellationToken ct)
    {
        var count = GetCount(args);
        // Try common spam folder names
        var spamFolders = new[] { "[Gmail]/Spam", "Junk", "Spam", "Junk E-mail", "INBOX.Junk", "INBOX.Spam" };

        return await ExecuteImapAsync(async (reader, writer, cancellation) =>
        {
            // Find which spam folder exists
            string? actualSpamFolder = null;
            foreach (var candidate in spamFolders)
            {
                try
                {
                    await writer.WriteLineAsync($"A_LIST LIST \"\" \"{candidate}\"".AsMemory(), cancellation);
                    await writer.FlushAsync(cancellation);
                    var resp = await ReadUntilTagAsync(reader, "A_LIST", cancellation);
                    if (resp.Contains("* LIST", StringComparison.OrdinalIgnoreCase))
                    {
                        actualSpamFolder = candidate;
                        break;
                    }
                }
                catch { /* try next */ }
            }

            if (actualSpamFolder is null)
                return "Could not find a Spam/Junk folder on this IMAP server. Tried: " + string.Join(", ", spamFolders);

            await ImapSelectAsync(writer, reader, actualSpamFolder, cancellation);
            var total = await ImapGetMessageCountAsync(writer, reader, actualSpamFolder, cancellation);
            if (total == 0) return $"Spam folder '{actualSpamFolder}' is empty. No false positives to rescue.";

            var startMsg = Math.Max(1, total - count + 1);
            var rescued = new List<(int MsgNum, string Reason, string Desc)>();

            for (var i = total; i >= startMsg; i--)
            {
                var email = await ImapFetchHeadersExtendedAsync(writer, reader, i, cancellation);
                var category = CategorizeEmail(email);

                // If classified as VIP, Protected, Receipt, or Confirmation, it's likely a false positive
                if (category is "VIP" or "Protected" or "Protected Keyword" or "Receipt" or "Confirmation")
                    rescued.Add((i, category, $"{email.From}: {email.Subject}"));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"## Spam Rescue: {actualSpamFolder}");
            sb.AppendLine($"Scanned: {Math.Min(count, total)} emails in spam");

            if (rescued.Count == 0)
            {
                sb.AppendLine("✅ No false positives detected. Your spam filter seems accurate.");
                return sb.ToString();
            }

            if (_config.DryRun)
            {
                sb.AppendLine($"### 🔍 DRY RUN — {rescued.Count} potential false positive(s):");
                foreach (var (_, reason, desc) in rescued)
                    sb.AppendLine($"  - ({reason}) {desc}");
                sb.AppendLine();
                sb.AppendLine("Set `InboxZero.DryRun=false` to move these back to INBOX.");
            }
            else
            {
                foreach (var (msgNum, _, _) in rescued)
                    await ImapCopyToInbox(writer, reader, msgNum, cancellation);

                sb.AppendLine($"### ✅ Rescued {rescued.Count} email(s) back to INBOX:");
                foreach (var (_, reason, desc) in rescued)
                    sb.AppendLine($"  - ({reason}) {desc}");
            }

            return sb.ToString();
        }, ct);
    }

    // ── Categorization Engine ────────────────────────────────────────

    private string CategorizeEmail(EmailHeaders email)
    {
        var fromLower = email.From.ToLowerInvariant();
        var subjectLower = email.Subject.ToLowerInvariant();
        var senderDomain = ExtractDomain(fromLower);

        // 1. VIP
        foreach (var vip in _config.VipSenders)
        {
            if (fromLower.Contains(vip, StringComparison.OrdinalIgnoreCase))
                return "VIP";
        }

        // 2. Protected sender
        foreach (var ps in _config.ProtectedSenders)
        {
            if (fromLower.Contains(ps, StringComparison.OrdinalIgnoreCase))
                return "Protected";
        }

        // 2b. Built-in protected domains
        if (senderDomain is not null && BuiltInProtectedDomains.Contains(senderDomain))
            return "Protected";

        // 3. Protected keyword
        foreach (var kw in _config.ProtectedKeywords)
        {
            if (subjectLower.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return "Protected Keyword";
        }

        // 4. Receipt
        foreach (var pattern in ReceiptPatterns)
        {
            if (subjectLower.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return "Receipt";
        }

        // 5. Confirmation
        foreach (var pattern in ConfirmationPatterns)
        {
            if (subjectLower.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return "Confirmation";
        }

        // 6. Newsletter (check for List-Unsubscribe header)
        if (email.HasListUnsubscribe)
            return "Newsletter";

        // 7. Promotional
        foreach (var pattern in PromoPatterns)
        {
            if (subjectLower.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return "Promotional";
        }

        // 8. Automated (noreply patterns)
        if (fromLower.Contains("noreply") || fromLower.Contains("no-reply") ||
            fromLower.Contains("donotreply") || fromLower.Contains("do-not-reply") ||
            fromLower.Contains("notifications@") || fromLower.Contains("mailer-daemon"))
            return "Automated";

        // 9. Unknown (likely a real person)
        return "Unknown";
    }

    private static int GetCategoryPriority(string category) => category switch
    {
        "VIP" => 0, "Protected" => 1, "Protected Keyword" => 2,
        "Receipt" => 3, "Confirmation" => 4, "Newsletter" => 5,
        "Promotional" => 6, "Automated" => 7, _ => 8
    };

    private static string? ExtractDomain(string email)
    {
        var atIdx = email.LastIndexOf('@');
        if (atIdx < 0) return null;
        var domain = email[(atIdx + 1)..].Trim().TrimEnd('>');
        return domain;
    }

    // ── IMAP Helpers ─────────────────────────────────────────────────

    private record EmailHeaders(string Subject, string From, string Date, bool HasListUnsubscribe);

    private int GetCount(JsonElement args)
    {
        var count = args.TryGetProperty("count", out var c) ? c.GetInt32() : 50;
        return Math.Min(count, _config.MaxBatchSize);
    }

    private async Task<string> ExecuteImapAsync(
        Func<StreamReader, StreamWriter, CancellationToken, Task<string>> action,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_emailConfig.ImapHost))
            return "Error: IMAP host not configured. Set Plugins.Native.Email.ImapHost.";

        var password = SecretResolver.Resolve(_emailConfig.PasswordRef);
        if (string.IsNullOrWhiteSpace(_emailConfig.Username) || string.IsNullOrWhiteSpace(password))
            return "Error: Email credentials not configured. Set Email.Username and Email.PasswordRef.";

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(_emailConfig.ImapHost, _emailConfig.ImapPort, ct);

            System.IO.Stream stream = client.GetStream();
            var sslStream = new System.Net.Security.SslStream(stream, false);
            await sslStream.AuthenticateAsClientAsync(_emailConfig.ImapHost);
            stream = sslStream;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };

            // Read greeting
            await reader.ReadLineAsync(ct);

            // Login
            await writer.WriteLineAsync($"A1 LOGIN {_emailConfig.Username} {password}".AsMemory(), ct);
            await writer.FlushAsync(ct);
            var loginResp = await ReadUntilTagAsync(reader, "A1", ct);
            if (!loginResp.Contains("OK", StringComparison.OrdinalIgnoreCase))
                return $"Error: IMAP login failed — {loginResp}";

            var result = await action(reader, writer, ct);

            // Logout
            await writer.WriteLineAsync("A99 LOGOUT".AsMemory(), ct);
            await writer.FlushAsync(ct);

            return result;
        }
        catch (Exception ex)
        {
            return $"Error: IMAP operation failed — {ex.Message}";
        }
    }

    private static async Task ImapSelectAsync(StreamWriter writer, StreamReader reader, string folder, CancellationToken ct)
    {
        folder = InputSanitizer.StripCrlf(folder);
        await writer.WriteLineAsync($"A2 SELECT \"{folder}\"".AsMemory(), ct);
        await writer.FlushAsync(ct);
        await ReadUntilTagAsync(reader, "A2", ct);
    }

    private static async Task<int> ImapGetMessageCountAsync(StreamWriter writer, StreamReader reader, string folder, CancellationToken ct)
    {
        await writer.WriteLineAsync($"A3 STATUS \"{folder}\" (MESSAGES)".AsMemory(), ct);
        await writer.FlushAsync(ct);

        var count = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Contains("MESSAGES", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf("MESSAGES", StringComparison.OrdinalIgnoreCase);
                var numStart = idx + 8;
                while (numStart < line.Length && !char.IsDigit(line[numStart])) numStart++;
                var numEnd = numStart;
                while (numEnd < line.Length && char.IsDigit(line[numEnd])) numEnd++;
                if (numEnd > numStart)
                    int.TryParse(line.AsSpan(numStart, numEnd - numStart), out count);
            }
            if (line.StartsWith("A3 ", StringComparison.Ordinal)) break;
        }
        return count;
    }

    private static async Task<EmailHeaders> ImapFetchHeadersExtendedAsync(
        StreamWriter writer, StreamReader reader, int msgNum, CancellationToken ct)
    {
        await writer.WriteLineAsync(
            $"A5 FETCH {msgNum} (BODY.PEEK[HEADER.FIELDS (SUBJECT FROM DATE LIST-UNSUBSCRIBE)])".AsMemory(), ct);
        await writer.FlushAsync(ct);

        string subject = "(no subject)", from = "(unknown)", date = "";
        var hasListUnsub = false;
        var sb = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            sb.AppendLine(line);
            if (line.StartsWith("A5 ", StringComparison.Ordinal)) break;
        }

        var headers = sb.ToString();
        foreach (var headerLine in headers.Split('\n'))
        {
            var trimmed = headerLine.Trim();
            if (trimmed.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
                subject = trimmed["Subject:".Length..].Trim();
            else if (trimmed.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
                from = trimmed["From:".Length..].Trim();
            else if (trimmed.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))
                date = trimmed["Date:".Length..].Trim();
            else if (trimmed.StartsWith("List-Unsubscribe:", StringComparison.OrdinalIgnoreCase))
                hasListUnsub = true;
        }

        return new EmailHeaders(subject, from, date, hasListUnsub);
    }

    private static async Task ImapStoreFlag(StreamWriter writer, StreamReader reader, int msgNum, string flag, CancellationToken ct)
    {
        var tag = $"AS{msgNum}";
        await writer.WriteLineAsync($"{tag} STORE {msgNum} +FLAGS ({flag})".AsMemory(), ct);
        await writer.FlushAsync(ct);
        await ReadUntilTagAsync(reader, tag, ct);
    }

    private static async Task ImapMoveToArchive(StreamWriter writer, StreamReader reader, int msgNum, CancellationToken ct)
    {
        // Try MOVE command first (RFC 6851), fall back to COPY+DELETE
        var tag = $"AM{msgNum}";
        // Most providers support "[Gmail]/All Mail" or "Archive"
        await writer.WriteLineAsync($"{tag} COPY {msgNum} \"Archive\"".AsMemory(), ct);
        await writer.FlushAsync(ct);
        var resp = await ReadUntilTagAsync(reader, tag, ct);

        if (resp.Contains("NO", StringComparison.OrdinalIgnoreCase))
        {
            // Try Gmail-style archive
            var tag2 = $"AM2{msgNum}";
            await writer.WriteLineAsync($"{tag2} COPY {msgNum} \"[Gmail]/All Mail\"".AsMemory(), ct);
            await writer.FlushAsync(ct);
            await ReadUntilTagAsync(reader, tag2, ct);
        }

        // Mark as deleted from current folder
        await ImapStoreFlag(writer, reader, msgNum, "\\Deleted", ct);
    }

    private static async Task ImapCopyToInbox(StreamWriter writer, StreamReader reader, int msgNum, CancellationToken ct)
    {
        var tag = $"AC{msgNum}";
        await writer.WriteLineAsync($"{tag} COPY {msgNum} \"INBOX\"".AsMemory(), ct);
        await writer.FlushAsync(ct);
        await ReadUntilTagAsync(reader, tag, ct);
    }

    private static async Task<string> ReadUntilTagAsync(StreamReader reader, string tag, CancellationToken ct)
    {
        var sb = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            sb.AppendLine(line);
            if (line.StartsWith($"{tag} ", StringComparison.Ordinal)) break;
        }
        return sb.ToString();
    }

    public void Dispose() { }
}
