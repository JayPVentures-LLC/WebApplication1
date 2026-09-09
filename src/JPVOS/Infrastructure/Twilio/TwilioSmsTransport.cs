using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JPVOS.Services.Outbound;

namespace JPVOS.Infrastructure.Twilio;

public static class TwilioRequestSignature
{
    public static string Compute(string url, IReadOnlyDictionary<string, string> form, string authToken)
    {
        var payload = new StringBuilder(url);
        foreach (var pair in form.OrderBy(x => x.Key, StringComparer.Ordinal)) payload.Append(pair.Key).Append(pair.Value);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    public static bool Validate(string url, IReadOnlyDictionary<string, string> form, string suppliedSignature, string authToken)
    {
        if (string.IsNullOrWhiteSpace(suppliedSignature) || string.IsNullOrWhiteSpace(authToken)) return false;
        var expected = Compute(url, form, authToken);
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(suppliedSignature);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

public static class TwilioCallbackUrl
{
    public static bool TryBuild(string? callbackBase, string callbackKind, out string callbackUrl)
    {
        callbackUrl = string.Empty;
        if (!Uri.TryCreate(callbackBase, UriKind.Absolute, out var baseUri)) return false;
        if (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || baseUri.IsLoopback) return false;
        var prefix = baseUri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(baseUri) { Path = $"{prefix}/api/outbound/providers/twilio/{callbackKind}", Query = string.Empty, Fragment = string.Empty };
        callbackUrl = builder.Uri.ToString();
        return true;
    }
}

public sealed class TwilioSmsTransport : ISmsTransport
{
    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;

    public TwilioSmsTransport(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _configuration = configuration;
    }

    public async Task<SmsSendResult> SendAsync(SmsSendCommand command, CancellationToken cancellationToken)
    {
        var accountSid = _configuration["TWILIO_ACCOUNT_SID"];
        var authToken = _configuration["TWILIO_AUTH_TOKEN"];
        var messagingServiceSid = _configuration["TWILIO_MESSAGING_SERVICE_SID"];
        var fromNumber = _configuration["TWILIO_FROM_NUMBER"];
        var callbackBase = _configuration["JPV_OUTBOUND_WEBHOOK_BASE_URL"];
        if (string.IsNullOrWhiteSpace(accountSid) || string.IsNullOrWhiteSpace(authToken) || (string.IsNullOrWhiteSpace(messagingServiceSid) && string.IsNullOrWhiteSpace(fromNumber)))
            return SmsSendResult.Failed("provider_unavailable");
        if (!TwilioCallbackUrl.TryBuild(callbackBase, "status", out var statusCallback)) return SmsSendResult.Failed("provider_unavailable");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(accountSid)}/Messages.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{accountSid}:{authToken}")));
        var values = new Dictionary<string, string> { ["To"] = command.DestinationE164, ["Body"] = command.Body };
        if (!string.IsNullOrWhiteSpace(messagingServiceSid)) values["MessagingServiceSid"] = messagingServiceSid;
        else values["From"] = fromNumber!;
        values["StatusCallback"] = statusCallback;
        request.Content = new FormUrlEncodedContent(values);

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) return SmsSendResult.Failed("provider_rejected");

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var sid = root.TryGetProperty("sid", out var sidNode) ? sidNode.GetString() : null;
        var status = root.TryGetProperty("status", out var statusNode) ? statusNode.GetString() : "queued";
        if (string.IsNullOrWhiteSpace(sid)) return SmsSendResult.Failed("provider_rejected");
        return SmsSendResult.Accepted(sid, NormalizeStatus(status));
    }

    public static OutboundMessageState NormalizeStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "accepted" or "queued" => OutboundMessageState.Queued,
        "sending" or "sent" => OutboundMessageState.Sent,
        "delivered" => OutboundMessageState.Delivered,
        "failed" or "undelivered" or "canceled" => OutboundMessageState.Failed,
        _ => OutboundMessageState.Queued
    };

    public bool ValidateWebhook(string url, IReadOnlyDictionary<string, string> form, string signature)
        => TwilioRequestSignature.Validate(url, form, signature, _configuration["TWILIO_AUTH_TOKEN"] ?? string.Empty);
}
