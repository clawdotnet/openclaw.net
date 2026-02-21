using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw calendar plugin.
/// Integrates with Google Calendar via REST API.
/// Supports listing, creating, updating, and deleting events.
/// </summary>
public sealed class CalendarTool : ITool, IDisposable
{
    private readonly CalendarConfig _config;
    private readonly HttpClient _http;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry;

    public CalendarTool(CalendarConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _http = httpClient ?? HttpClientFactory.Create();
    }

    public string Name => "calendar";
    public string Description =>
        "Manage calendar events. Supports listing upcoming events, creating, updating, and deleting events.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "description": "Action to perform",
              "enum": ["list", "create", "update", "delete", "search"]
            },
            "query": {
              "type": "string",
              "description": "Search query (for search action) or event title filter (for list)"
            },
            "title": {
              "type": "string",
              "description": "Event title (for create/update)"
            },
            "start": {
              "type": "string",
              "description": "Start datetime in ISO 8601 format (e.g., 2026-02-20T10:00:00-05:00)"
            },
            "end": {
              "type": "string",
              "description": "End datetime in ISO 8601 format"
            },
            "description": {
              "type": "string",
              "description": "Event description/notes"
            },
            "location": {
              "type": "string",
              "description": "Event location"
            },
            "event_id": {
              "type": "string",
              "description": "Event ID (for update/delete)"
            },
            "days_ahead": {
              "type": "integer",
              "description": "Number of days ahead to list events (default: 7)",
              "default": 7
            }
          },
          "required": ["action"]
        }
        """;

    private const string CalendarApiBase = "https://www.googleapis.com/calendar/v3";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var action = args.RootElement.GetProperty("action").GetString()!.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(_config.CredentialsPath) || !File.Exists(_config.CredentialsPath))
            return "Error: Calendar credentials not configured. Set Calendar.CredentialsPath to a valid service account JSON key file.";

        try
        {
            await EnsureAccessTokenAsync(ct);
        }
        catch (Exception ex)
        {
            return $"Error: Failed to authenticate with Google Calendar — {ex.Message}";
        }

        try
        {
            return action switch
            {
                "list" => await ListEventsAsync(args.RootElement, ct),
                "search" => await SearchEventsAsync(args.RootElement, ct),
                "create" => await CreateEventAsync(args.RootElement, ct),
                "update" => await UpdateEventAsync(args.RootElement, ct),
                "delete" => await DeleteEventAsync(args.RootElement, ct),
                _ => $"Error: Unsupported calendar action '{action}'. Use: list, search, create, update, delete."
            };
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Calendar API request failed — {ex.Message}";
        }
    }

    private async Task<string> ListEventsAsync(JsonElement args, CancellationToken ct)
    {
        var daysAhead = args.TryGetProperty("days_ahead", out var d) ? d.GetInt32() : 7;
        var now = DateTimeOffset.UtcNow;
        var until = now.AddDays(daysAhead);

        var url = $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(_config.CalendarId)}/events" +
                  $"?timeMin={Uri.EscapeDataString(now.ToString("o"))}" +
                  $"&timeMax={Uri.EscapeDataString(until.ToString("o"))}" +
                  $"&maxResults={_config.MaxEvents}" +
                  $"&singleEvents=true&orderBy=startTime";

        var query = args.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (!string.IsNullOrWhiteSpace(query))
            url += $"&q={Uri.EscapeDataString(query)}";

        using var request = CreateAuthRequest(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return FormatEventList(doc.RootElement);
    }

    private async Task<string> SearchEventsAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
            return "Error: 'query' parameter is required for search.";

        var url = $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(_config.CalendarId)}/events" +
                  $"?q={Uri.EscapeDataString(query)}" +
                  $"&maxResults={_config.MaxEvents}" +
                  $"&singleEvents=true&orderBy=startTime";

        using var request = CreateAuthRequest(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return FormatEventList(doc.RootElement);
    }

    private async Task<string> CreateEventAsync(JsonElement args, CancellationToken ct)
    {
        var title = args.TryGetProperty("title", out var t) ? t.GetString() : null;
        var start = args.TryGetProperty("start", out var s) ? s.GetString() : null;
        var end = args.TryGetProperty("end", out var e) ? e.GetString() : null;

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(start))
            return "Error: 'title' and 'start' are required to create an event.";

        // Default end = start + 1 hour
        end ??= DateTimeOffset.Parse(start).AddHours(1).ToString("o");

        var body = BuildEventJson(title, start, end,
            args.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            args.TryGetProperty("location", out var loc) ? loc.GetString() : null);

        var url = $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(_config.CalendarId)}/events";
        using var request = CreateAuthRequest(HttpMethod.Post, url);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            return $"Error: Failed to create event (HTTP {(int)response.StatusCode}): {err[..Math.Min(err.Length, 300)]}";
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var eventId = doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : "unknown";
        var htmlLink = doc.RootElement.TryGetProperty("htmlLink", out var link) ? link.GetString() : "";

        return $"Event created successfully.\nID: {eventId}\nTitle: {title}\nStart: {start}\nEnd: {end}\nLink: {htmlLink}";
    }

    private async Task<string> UpdateEventAsync(JsonElement args, CancellationToken ct)
    {
        var eventId = args.TryGetProperty("event_id", out var eid) ? eid.GetString() : null;
        if (string.IsNullOrWhiteSpace(eventId))
            return "Error: 'event_id' is required to update an event.";

        var title = args.TryGetProperty("title", out var t) ? t.GetString() : null;
        var start = args.TryGetProperty("start", out var s) ? s.GetString() : null;
        var end = args.TryGetProperty("end", out var e) ? e.GetString() : null;
        var description = args.TryGetProperty("description", out var desc) ? desc.GetString() : null;
        var location = args.TryGetProperty("location", out var loc) ? loc.GetString() : null;

        var body = BuildPartialEventJson(title, start, end, description, location);

        var url = $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(_config.CalendarId)}/events/{Uri.EscapeDataString(eventId)}";
        using var request = CreateAuthRequest(HttpMethod.Patch, url);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            return $"Error: Failed to update event (HTTP {(int)response.StatusCode}): {err[..Math.Min(err.Length, 300)]}";
        }

        return $"Event '{eventId}' updated successfully.";
    }

    private async Task<string> DeleteEventAsync(JsonElement args, CancellationToken ct)
    {
        var eventId = args.TryGetProperty("event_id", out var eid) ? eid.GetString() : null;
        if (string.IsNullOrWhiteSpace(eventId))
            return "Error: 'event_id' is required to delete an event.";

        var url = $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(_config.CalendarId)}/events/{Uri.EscapeDataString(eventId)}";
        using var request = CreateAuthRequest(HttpMethod.Delete, url);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            return $"Error: Failed to delete event (HTTP {(int)response.StatusCode}): {err[..Math.Min(err.Length, 300)]}";
        }

        return $"Event '{eventId}' deleted successfully.";
    }

    private static string FormatEventList(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
            return "No events found.";

        var sb = new StringBuilder();
        var count = 0;

        foreach (var ev in items.EnumerateArray())
        {
            count++;
            var summary = ev.TryGetProperty("summary", out var s) ? s.GetString() : "(no title)";
            var eventId = ev.TryGetProperty("id", out var id) ? id.GetString() : "";
            var location = ev.TryGetProperty("location", out var loc) ? loc.GetString() : null;

            string? startStr = null;
            if (ev.TryGetProperty("start", out var start))
            {
                startStr = start.TryGetProperty("dateTime", out var dt) ? dt.GetString()
                    : start.TryGetProperty("date", out var d) ? d.GetString()
                    : null;
            }

            string? endStr = null;
            if (ev.TryGetProperty("end", out var end))
            {
                endStr = end.TryGetProperty("dateTime", out var dt) ? dt.GetString()
                    : end.TryGetProperty("date", out var d) ? d.GetString()
                    : null;
            }

            sb.AppendLine($"[{count}] {summary}");
            sb.AppendLine($"    ID: {eventId}");
            if (startStr is not null)
                sb.AppendLine($"    Start: {startStr}");
            if (endStr is not null)
                sb.AppendLine($"    End: {endStr}");
            if (location is not null)
                sb.AppendLine($"    Location: {location}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private HttpRequestMessage CreateAuthRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {_accessToken}");
        return request;
    }

    /// <summary>
    /// Obtain an access token from a Google service account JSON key file.
    /// Uses a self-signed JWT (no external library needed).
    /// </summary>
    private async Task EnsureAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
            return;

        // Read service account credentials
        var credJson = await File.ReadAllTextAsync(_config.CredentialsPath!, ct);
        using var cred = JsonDocument.Parse(credJson);
        var clientEmail = cred.RootElement.GetProperty("client_email").GetString()!;
        var privateKeyPem = cred.RootElement.GetProperty("private_key").GetString()!;
        var tokenUri = cred.RootElement.TryGetProperty("token_uri", out var tu)
            ? tu.GetString()! : "https://oauth2.googleapis.com/token";

        // Build JWT
        var now = DateTimeOffset.UtcNow;
        var exp = now.AddHours(1);

        var headerJson = """{"alg":"RS256","typ":"JWT"}""";
        var claimsJson = BuildJwtClaims(clientEmail, tokenUri, now.ToUnixTimeSeconds(), exp.ToUnixTimeSeconds());

        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var claimsB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(claimsJson));
        var signingInput = $"{headerB64}.{claimsB64}";

        // Sign with RSA
        var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        var jwt = $"{signingInput}.{Base64UrlEncode(signature)}";

        // Exchange JWT for access token
        var tokenRequest = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
            new KeyValuePair<string, string>("assertion", jwt)
        ]);

        using var response = await _http.PostAsync(tokenUri, tokenRequest, ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60); // 60s buffer
    }

    private static string BuildJwtClaims(string clientEmail, string tokenUri, long iat, long exp)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("iss", clientEmail);
            w.WriteString("scope", "https://www.googleapis.com/auth/calendar");
            w.WriteString("aud", tokenUri);
            w.WriteNumber("iat", iat);
            w.WriteNumber("exp", exp);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildEventJson(string title, string start, string end, string? description, string? location)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("summary", title);
            w.WriteStartObject("start");
            w.WriteString("dateTime", start);
            w.WriteEndObject();
            w.WriteStartObject("end");
            w.WriteString("dateTime", end);
            w.WriteEndObject();
            if (description is not null) w.WriteString("description", description);
            if (location is not null) w.WriteString("location", location);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildPartialEventJson(string? title, string? start, string? end, string? description, string? location)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            if (title is not null) w.WriteString("summary", title);
            if (start is not null) { w.WriteStartObject("start"); w.WriteString("dateTime", start); w.WriteEndObject(); }
            if (end is not null) { w.WriteStartObject("end"); w.WriteString("dateTime", end); w.WriteEndObject(); }
            if (description is not null) w.WriteString("description", description);
            if (location is not null) w.WriteString("location", location);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public void Dispose() => _http.Dispose();
}
