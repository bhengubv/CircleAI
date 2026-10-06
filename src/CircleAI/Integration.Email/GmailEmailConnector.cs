// GmailEmailConnector.cs
//
// (Phase B2) Gmail API v1 client. Uses host-supplied OAuth tokens.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Integration;

namespace CircleAI.Integration.Email;

public sealed record GmailOptions(Func<CancellationToken, ValueTask<string?>> AccessTokenProvider);

public sealed class GmailEmailConnector : IEmailConnector
{
    private const string BaseUri = "https://gmail.googleapis.com/gmail/v1/users/me/";
    private readonly HttpClient _http;
    private readonly GmailOptions _opts;

    public GmailEmailConnector(GmailOptions opts) : this(opts, new HttpClient { BaseAddress = new Uri(BaseUri) }) { }

    public GmailEmailConnector(GmailOptions opts, HttpClient http)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(BaseUri);
    }

    public string ProviderId   => "gmail";
    public bool   IsConfigured => _opts.AccessTokenProvider is not null;

    public ValueTask<IReadOnlyList<EmailMessage>> ListUnreadAsync(int max, CancellationToken ct = default)
        => SearchAsync("is:unread", max, ct);

    public async ValueTask<IReadOnlyList<EmailMessage>> SearchAsync(string query, int max, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query required");
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
        await EnsureAuthAsync(ct).ConfigureAwait(false);

        var listPath = $"messages?q={Uri.EscapeDataString(query)}&maxResults={Math.Min(max, 100)}";
        using var listResp = await _http.GetAsync(listPath, ct).ConfigureAwait(false);
        listResp.EnsureSuccessStatusCode();
        using var listDoc = await JsonDocument.ParseAsync(
            await listResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var ids = new List<string>();
        if (listDoc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            foreach (var m in msgs.EnumerateArray())
                if (m.TryGetProperty("id", out var id)) ids.Add(id.GetString() ?? "");

        var result = new List<EmailMessage>(ids.Count);
        foreach (var id in ids)
        {
            using var getResp = await _http.GetAsync($"messages/{Uri.EscapeDataString(id)}?format=full", ct).ConfigureAwait(false);
            if (!getResp.IsSuccessStatusCode) continue;
            using var doc = await JsonDocument.ParseAsync(
                await getResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                cancellationToken: ct).ConfigureAwait(false);
            result.Add(ParseGmailMessage(doc.RootElement));
        }
        return result;
    }

    public async ValueTask MarkReadAsync(string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("messageId required");
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        using var resp = await _http.PostAsJsonAsync(
            $"messages/{Uri.EscapeDataString(messageId)}/modify",
            new { removeLabelIds = new[] { "UNREAD" } }, cancellationToken: ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private async ValueTask EnsureAuthAsync(CancellationToken ct)
    {
        var token = await _opts.AccessTokenProvider(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Gmail access token unavailable; refresh OAuth.");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static EmailMessage ParseGmailMessage(JsonElement msg)
    {
        var id        = msg.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var labels    = new List<string>();
        if (msg.TryGetProperty("labelIds", out var labs) && labs.ValueKind == JsonValueKind.Array)
            foreach (var l in labs.EnumerateArray()) labels.Add(l.GetString() ?? "");
        var unread    = labels.Contains("UNREAD", StringComparer.OrdinalIgnoreCase);
        var headers   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (msg.TryGetProperty("payload", out var payload))
        {
            if (payload.TryGetProperty("headers", out var hs) && hs.ValueKind == JsonValueKind.Array)
                foreach (var h in hs.EnumerateArray())
                    if (h.TryGetProperty("name", out var name) && h.TryGetProperty("value", out var val))
                        headers[name.GetString() ?? ""] = val.GetString() ?? "";
        }
        var bodyText = ExtractBody(msg.TryGetProperty("payload", out var p2) ? p2 : default);
        var receivedMs = msg.TryGetProperty("internalDate", out var dateEl) && long.TryParse(dateEl.GetString(), out var ms) ? ms : 0;
        return new EmailMessage(
            MessageId:    id,
            From:         headers.TryGetValue("From",    out var f) ? f : "",
            To:           headers.TryGetValue("To",      out var t) ? t.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray() : Array.Empty<string>(),
            Subject:      headers.TryGetValue("Subject", out var s) ? s : "",
            BodyText:     bodyText,
            ReceivedUtc:  DateTimeOffset.FromUnixTimeMilliseconds(receivedMs).UtcDateTime,
            Unread:       unread,
            Labels:       labels);
    }

    private static string ExtractBody(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return "";
        if (payload.TryGetProperty("body", out var body)
            && body.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.String)
        {
            return DecodeBase64Url(data.GetString() ?? "");
        }
        if (payload.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                var mime = part.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;
                if (string.Equals(mime, "text/plain", StringComparison.OrdinalIgnoreCase))
                    return ExtractBody(part);
            }
            foreach (var part in parts.EnumerateArray())
            {
                var content = ExtractBody(part);
                if (!string.IsNullOrEmpty(content)) return content;
            }
        }
        return "";
    }

    private static string DecodeBase64Url(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('-', '+').Replace('_', '/');
        var padding = s.Length % 4;
        if (padding > 0) s = s.PadRight(s.Length + 4 - padding, '=');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return ""; }
    }
}
