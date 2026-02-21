using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw email plugin.
/// Sends email via SMTP and reads email via IMAP.
/// Uses System.Net.Mail for sending (built-in, AOT-compatible).
/// Uses raw IMAP commands for reading (no external dependency).
/// </summary>
public sealed class EmailTool : ITool, IDisposable
{
    private readonly EmailConfig _config;

    public EmailTool(EmailConfig config) => _config = config;

    public string Name => "email";
    public string Description =>
        "Send and read emails. Supports sending via SMTP and reading via IMAP.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "description": "Action to perform",
              "enum": ["send", "list", "read", "search"]
            },
            "to": {
              "type": "string",
              "description": "Recipient email address (for send)"
            },
            "subject": {
              "type": "string",
              "description": "Email subject (for send)"
            },
            "body": {
              "type": "string",
              "description": "Email body text (for send)"
            },
            "folder": {
              "type": "string",
              "description": "IMAP folder to read from (default: INBOX)",
              "default": "INBOX"
            },
            "message_id": {
              "type": "string",
              "description": "Message number to read (for read action)"
            },
            "query": {
              "type": "string",
              "description": "Search query (for search action, uses IMAP SEARCH syntax)"
            },
            "count": {
              "type": "integer",
              "description": "Number of messages to list (default: 10)",
              "default": 10
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
            "send" => await SendEmailAsync(args.RootElement, ct),
            "list" => await ListEmailsAsync(args.RootElement, ct),
            "read" => await ReadEmailAsync(args.RootElement, ct),
            "search" => await SearchEmailsAsync(args.RootElement, ct),
            _ => $"Error: Unsupported email action '{action}'. Use: send, list, read, search."
        };
    }

    private async ValueTask<string> SendEmailAsync(JsonElement args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.SmtpHost))
            return "Error: SMTP host not configured. Set Email.SmtpHost.";

        var to = args.TryGetProperty("to", out var t) ? t.GetString() : null;
        var subject = args.TryGetProperty("subject", out var s) ? s.GetString() : null;
        var body = args.TryGetProperty("body", out var b) ? b.GetString() : null;

        if (string.IsNullOrWhiteSpace(to))
            return "Error: 'to' is required to send an email.";
        if (string.IsNullOrWhiteSpace(subject))
            return "Error: 'subject' is required to send an email.";

        var password = ResolveSecret(_config.PasswordRef);
        if (string.IsNullOrWhiteSpace(_config.Username) || string.IsNullOrWhiteSpace(password))
            return "Error: Email credentials not configured. Set Email.Username and Email.PasswordRef.";

        var from = _config.FromAddress ?? _config.Username;

        try
        {
            using var client = new System.Net.Mail.SmtpClient(_config.SmtpHost, _config.SmtpPort)
            {
                Credentials = new System.Net.NetworkCredential(_config.Username, password),
                EnableSsl = _config.SmtpUseTls
            };

            using var message = new System.Net.Mail.MailMessage(from!, to, subject, body ?? "");
            await client.SendMailAsync(message, ct);

            return $"Email sent successfully.\nTo: {to}\nSubject: {subject}";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to send email — {ex.Message}";
        }
    }

    private async ValueTask<string> ListEmailsAsync(JsonElement args, CancellationToken ct)
    {
        var folder = args.TryGetProperty("folder", out var f) ? f.GetString() ?? "INBOX" : "INBOX";
        var count = args.TryGetProperty("count", out var c) ? c.GetInt32() : 10;
        count = Math.Min(count, _config.MaxResults);

        // Sanitize folder name to prevent IMAP command injection
        folder = InputSanitizer.StripCrlf(folder);
        var folderError = InputSanitizer.CheckImapFolderName(folder);
        if (folderError is not null) return folderError;

        return await ExecuteImapAsync(async (stream, reader, writer, cancellation) =>
        {
            await ImapSelectAsync(writer, reader, folder, cancellation);

            // Get total message count
            var total = await ImapGetMessageCountAsync(writer, reader, cancellation);
            if (total == 0) return "No messages in folder.";

            var startMsg = Math.Max(1, total - count + 1);
            var sb = new StringBuilder();
            sb.AppendLine($"Folder: {folder} ({total} messages, showing {Math.Min(count, total)} most recent)");
            sb.AppendLine();

            for (var i = total; i >= startMsg; i--)
            {
                var headers = await ImapFetchHeadersAsync(writer, reader, i, cancellation);
                sb.AppendLine($"[{i}] {headers.Subject}");
                sb.AppendLine($"    From: {headers.From}");
                sb.AppendLine($"    Date: {headers.Date}");
                sb.AppendLine();
            }

            return sb.ToString();
        }, ct);
    }

    private async ValueTask<string> ReadEmailAsync(JsonElement args, CancellationToken ct)
    {
        var folder = args.TryGetProperty("folder", out var f) ? f.GetString() ?? "INBOX" : "INBOX";
        var msgId = args.TryGetProperty("message_id", out var m) ? m.GetString() : null;
        if (string.IsNullOrWhiteSpace(msgId) || !int.TryParse(msgId, out var msgNum))
            return "Error: 'message_id' (message number) is required to read an email.";

        // Sanitize folder name
        folder = InputSanitizer.StripCrlf(folder);
        var folderError = InputSanitizer.CheckImapFolderName(folder);
        if (folderError is not null) return folderError;

        return await ExecuteImapAsync(async (stream, reader, writer, cancellation) =>
        {
            await ImapSelectAsync(writer, reader, folder, cancellation);
            var body = await ImapFetchBodyAsync(writer, reader, msgNum, cancellation);
            return body;
        }, ct);
    }

    private async ValueTask<string> SearchEmailsAsync(JsonElement args, CancellationToken ct)
    {
        var folder = args.TryGetProperty("folder", out var f) ? f.GetString() ?? "INBOX" : "INBOX";
        var query = args.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
            return "Error: 'query' is required for search.";

        // Sanitize to prevent IMAP command injection
        folder = InputSanitizer.StripCrlf(folder);
        query = InputSanitizer.StripCrlf(query);
        var folderError = InputSanitizer.CheckImapFolderName(folder);
        if (folderError is not null) return folderError;

        return await ExecuteImapAsync(async (stream, reader, writer, cancellation) =>
        {
            await ImapSelectAsync(writer, reader, folder, cancellation);

            await writer.WriteLineAsync($"A4 SEARCH {query}".AsMemory(), cancellation);
            await writer.FlushAsync(cancellation);

            var sb = new StringBuilder();
            sb.AppendLine($"Search results for: {query}");
            sb.AppendLine();

            var ids = new List<int>();
            string? line;
            while ((line = await reader.ReadLineAsync(cancellation)) is not null)
            {
                if (line.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 2; i < parts.Length; i++)
                    {
                        if (int.TryParse(parts[i], out var id))
                            ids.Add(id);
                    }
                }
                if (line.StartsWith("A4 ", StringComparison.Ordinal)) break;
            }

            if (ids.Count == 0) return "No messages matched the search.";

            var showCount = Math.Min(ids.Count, _config.MaxResults);
            for (var i = ids.Count - 1; i >= ids.Count - showCount; i--)
            {
                var headers = await ImapFetchHeadersAsync(writer, reader, ids[i], cancellation);
                sb.AppendLine($"[{ids[i]}] {headers.Subject}");
                sb.AppendLine($"    From: {headers.From}");
                sb.AppendLine($"    Date: {headers.Date}");
                sb.AppendLine();
            }

            return sb.ToString();
        }, ct);
    }

    // ── IMAP helpers ─────────────────────────────────────────────────

    private async Task<string> ExecuteImapAsync(
        Func<System.Net.Sockets.NetworkStream, StreamReader, StreamWriter, CancellationToken, Task<string>> action,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.ImapHost))
            return "Error: IMAP host not configured. Set Email.ImapHost.";

        var password = ResolveSecret(_config.PasswordRef);
        if (string.IsNullOrWhiteSpace(_config.Username) || string.IsNullOrWhiteSpace(password))
            return "Error: Email credentials not configured. Set Email.Username and Email.PasswordRef.";

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(_config.ImapHost, _config.ImapPort, ct);

            System.IO.Stream stream = client.GetStream();

            // Wrap in SSL
            var sslStream = new System.Net.Security.SslStream(stream, false);
            await sslStream.AuthenticateAsClientAsync(_config.ImapHost);
            stream = sslStream;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };

            // Read greeting
            await reader.ReadLineAsync(ct);

            // Login
            await writer.WriteLineAsync($"A1 LOGIN {_config.Username} {password}".AsMemory(), ct);
            await writer.FlushAsync(ct);
            var loginResp = await ReadUntilTagAsync(reader, "A1", ct);
            if (!loginResp.Contains("OK", StringComparison.OrdinalIgnoreCase))
                return $"Error: IMAP login failed — {loginResp}";

            var result = await action(client.GetStream(), reader, writer, ct);

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
        await writer.WriteLineAsync($"A2 SELECT {folder}".AsMemory(), ct);
        await writer.FlushAsync(ct);
        await ReadUntilTagAsync(reader, "A2", ct);
    }

    private static async Task<int> ImapGetMessageCountAsync(StreamWriter writer, StreamReader reader, CancellationToken ct)
    {
        await writer.WriteLineAsync("A3 STATUS INBOX (MESSAGES)".AsMemory(), ct);
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

    private static async Task<(string Subject, string From, string Date)> ImapFetchHeadersAsync(
        StreamWriter writer, StreamReader reader, int msgNum, CancellationToken ct)
    {
        await writer.WriteLineAsync($"A5 FETCH {msgNum} (BODY.PEEK[HEADER.FIELDS (SUBJECT FROM DATE)])".AsMemory(), ct);
        await writer.FlushAsync(ct);

        string subject = "(no subject)", from = "(unknown)", date = "";
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
        }

        return (subject, from, date);
    }

    private static async Task<string> ImapFetchBodyAsync(
        StreamWriter writer, StreamReader reader, int msgNum, CancellationToken ct)
    {
        await writer.WriteLineAsync($"A6 FETCH {msgNum} (BODY.PEEK[TEXT])".AsMemory(), ct);
        await writer.FlushAsync(ct);

        var sb = new StringBuilder();
        string? line;
        var inBody = false;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.StartsWith("A6 ", StringComparison.Ordinal)) break;
            if (line.Contains("BODY[TEXT]", StringComparison.OrdinalIgnoreCase))
            {
                inBody = true;
                continue;
            }
            if (inBody)
            {
                if (line == ")")
                {
                    inBody = false;
                    continue;
                }
                sb.AppendLine(line);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "No body content found.";
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

    private static string? ResolveSecret(string? secretRef) => SecretResolver.Resolve(secretRef);

    public void Dispose() { }
}
