using System.Net.Http.Headers;
using System.Text;
using OpenClaw.Core.Models;

namespace OpenClaw.Channels;

internal sealed class TwilioSmsClient
{
    private readonly HttpClient _http;
    private readonly TwilioSmsConfig _config;
    private readonly string _authToken;

    public TwilioSmsClient(HttpClient http, TwilioSmsConfig config, string authToken)
    {
        _http = http;
        _config = config;
        _authToken = authToken;
    }

    public async Task<(bool Ok, string Message)> SendAsync(string toE164, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.AccountSid))
            return (false, "Twilio AccountSid is not configured.");

        if (string.IsNullOrWhiteSpace(_config.MessagingServiceSid) && string.IsNullOrWhiteSpace(_config.FromNumber))
            return (false, "Twilio MessagingServiceSid or FromNumber must be configured.");

        var url = $"https://api.twilio.com/2010-04-01/Accounts/{_config.AccountSid}/Messages.json";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = CreateBasicAuth(_config.AccountSid, _authToken);

        var pairs = new List<KeyValuePair<string, string>>
        {
            new("To", toE164),
            new("Body", body)
        };

        if (!string.IsNullOrWhiteSpace(_config.MessagingServiceSid))
            pairs.Add(new KeyValuePair<string, string>("MessagingServiceSid", _config.MessagingServiceSid));
        else
            pairs.Add(new KeyValuePair<string, string>("From", _config.FromNumber!));

        req.Content = new FormUrlEncodedContent(pairs);

        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);

        if (res.IsSuccessStatusCode)
            return (true, "ok");

        return (false, $"Twilio send failed ({(int)res.StatusCode}): {text}");
    }

    private static AuthenticationHeaderValue CreateBasicAuth(string accountSid, string authToken)
    {
        var raw = $"{accountSid}:{authToken}";
        var b64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(raw));
        return new AuthenticationHeaderValue("Basic", b64);
    }
}

