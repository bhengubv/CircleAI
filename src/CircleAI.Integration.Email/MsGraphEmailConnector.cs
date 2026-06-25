// MsGraphEmailConnector.cs
//
// (Phase B2) Microsoft Graph v1.0 client for Outlook / Microsoft 365 mail.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Integration;

namespace CircleAI.Integration.Email;

public sealed record MsGraphEmailOptions(Func<CancellationToken, ValueTask<string?>> AccessTokenProvider);

public sealed class MsGraphEmailConnector : IEmailConnector
{
    private const string BaseUri = "https://graph.microsoft.com/v1.0/";
    private readonly HttpClient _http;
    private readonly MsGraphEmailOptions _opts;

    public MsGraphEmailConnector(MsGraphEmailOptions opts)
        : this(opts, new HttpClient { BaseAddress = new Uri(BaseUri) }) { }

    public MsGraphEmailConnector(MsGraphEmailOptions opts, HttpClient http)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(BaseUri);
    }

    public string ProviderId   => "ms-graph-mail";
    public bool   IsConfigured => _opts.AccessTokenProvider is not null;

    public async ValueTask<IReadOnlyList<EmailMessage>> ListUnreadAsync(int max, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        var path = $"me/mailFolders('Inbox')/messages?$filter=isRead+eq+false&$top={Math.Min(max, 50)}&$orderby=receivedDateTime+desc";
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);
        return ReadMessages(doc.RootElement);
    }

    public async ValueTask<IReadOnlyList<EmailMessage>> SearchAsync(string query, int max, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query required");
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        var path = $"me/messages?$search={Uri.EscapeDataString(query)}&$top={Math.Min(max, 50)}";
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);
        return ReadMessages(doc.RootElement);
    }

    public async ValueTask MarkReadAsync(string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("messageId required");
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"me/messages/{Uri.EscapeDataString(messageId)}")
        {
            Content = JsonContent.Create(new { isRead = true }),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private async ValueTask EnsureAuthAsync(CancellationToken ct)
    {
        var token = await _opts.AccessTokenProvider(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Microsoft Graph access token unavailable; refresh OAuth.");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static IReadOnlyList<EmailMessage> ReadMessages(JsonElement root)
    {
        var list = new List<EmailMessage>();
        if (!root.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in arr.EnumerateArray())
        {
            var to = new List<string>();
            if (m.TryGetProperty("toRecipients", out var rcpts) && rcpts.ValueKind == JsonValueKind.Array)
                foreach (var r in rcpts.EnumerateArray())
                    if (r.TryGetProperty("emailAddress", out var ea) && ea.TryGetProperty("address", out var addr))
                        to.Add(addr.GetString() ?? "");
            var fromAddr = "";
            if (m.TryGetProperty("from", out var fr) && fr.TryGetProperty("emailAddress", out var fea)
                && fea.TryGetProperty("address", out var fAddr)) fromAddr = fAddr.GetString() ?? "";
            DateTimeOffset received = DateTimeOffset.MinValue;
            if (m.TryGetProperty("receivedDateTime", out var rd) && rd.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(rd.GetString(), out var dto)) received = dto.ToUniversalTime();
            var labels = new List<string>();
            if (m.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                foreach (var c in cats.EnumerateArray()) labels.Add(c.GetString() ?? "");
            var body = "";
            if (m.TryGetProperty("body", out var b) && b.TryGetProperty("content", out var bc))
                body = bc.GetString() ?? "";
            else if (m.TryGetProperty("bodyPreview", out var bp))
                body = bp.GetString() ?? "";
            list.Add(new EmailMessage(
                MessageId:    m.GetProperty("id").GetString() ?? "",
                From:         fromAddr,
                To:           to,
                Subject:      m.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "",
                BodyText:     body,
                ReceivedUtc:  received,
                Unread:       m.TryGetProperty("isRead", out var ir) && ir.ValueKind == JsonValueKind.False,
                Labels:       labels));
        }
        return list;
    }
}
