// TelnyxCarrier.cs
//
// (3.3.0) Telnyx v2 REST API adapter. Speaks Bearer-token auth, the
// /v2 namespace, and Telnyx's Call Control surface for number
// provisioning + outbound dial + termination + transfer.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Telephony.Telnyx;

/// <summary>
/// (3.3.0) <see cref="ITelephonyCarrier"/> backed by Telnyx's v2 REST API.
/// Fail-soft when credentials are missing.
/// </summary>
public sealed class TelnyxCarrier : ITelephonyCarrier
{
    private readonly HttpClient _http;
    private readonly TelnyxOptions _options;
    private readonly ILogger _logger;

    public TelnyxCarrier(HttpClient http, TelnyxOptions options, ILogger<TelnyxCarrier>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
        if (IsConfigured)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public string CarrierId    => "telnyx";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<ProvisionedNumber> ProvisionNumberAsync(
        string             countryCode,
        string?            areaCode = null,
        CancellationToken  ct       = default)
    {
        EnsureConfigured();

        // 1) Search availability.
        var searchPath = $"/v2/available_phone_numbers?filter[country_code]={countryCode}&filter[limit]=1";
        if (!string.IsNullOrWhiteSpace(areaCode))
        {
            searchPath += $"&filter[national_destination_code]={Uri.EscapeDataString(areaCode)}";
        }

        using var searchResp = await _http.GetAsync(searchPath, ct).ConfigureAwait(false);
        searchResp.EnsureSuccessStatusCode();
        using var searchDoc = await JsonDocument.ParseAsync(
            await searchResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var data = searchDoc.RootElement.GetProperty("data");
        var first = data.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException(
                $"Telnyx has no available numbers in country='{countryCode}', areaCode='{areaCode}'.");
        }

        var phoneNumber = first.GetProperty("phone_number").GetString()!;

        // 2) Place a Number Order to purchase it.
        var orderBody = $$"""{"phone_numbers":[{"phone_number":"{{phoneNumber}}"}]}""";
        using var orderResp = await _http.PostAsync(
            "/v2/number_orders",
            new StringContent(orderBody, Encoding.UTF8, "application/json"),
            ct).ConfigureAwait(false);
        orderResp.EnsureSuccessStatusCode();

        return new ProvisionedNumber(
            PhoneNumber:           phoneNumber,
            CarrierId:             CarrierId,
            ProvisionedAtUtc:      DateTimeOffset.UtcNow,
            MonthlyRecurringCost:  ParseMonthlyCost(first) ?? 0m);
    }

    public async ValueTask ConfigureInboundWebhookAsync(
        string             phoneNumber,
        Uri                inboundWebhook,
        CancellationToken  ct = default)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(_options.CallControlConnectionId))
        {
            throw new InvalidOperationException(
                "Telnyx ConfigureInboundWebhook requires CallControlConnectionId on TelnyxOptions.");
        }

        // Update the Call Control Application's webhook URL.
        var path = $"/v2/call_control_applications/{_options.CallControlConnectionId}";
        var body = $$"""{"webhook_event_url":"{{inboundWebhook}}"}""";
        using var req = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // Ensure the number is assigned to this connection.
        var assignBody = $$"""{"connection_id":"{{_options.CallControlConnectionId}}"}""";
        var assignPath = $"/v2/phone_numbers/{Uri.EscapeDataString(phoneNumber)}";
        using var assignReq = new HttpRequestMessage(HttpMethod.Patch, assignPath)
        {
            Content = new StringContent(assignBody, Encoding.UTF8, "application/json"),
        };
        using var assignResp = await _http.SendAsync(assignReq, ct).ConfigureAwait(false);
        if (!assignResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Telnyx assign number {Number} returned {Status} (may already be assigned)",
                phoneNumber, assignResp.StatusCode);
        }
    }

    public async ValueTask<ICallSession> DialAsync(
        string             fromNumber,
        string             toNumber,
        Uri                streamUrl,
        OutboundDialOptions? options = null,
        CancellationToken  ct       = default)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(_options.CallControlConnectionId))
        {
            throw new InvalidOperationException(
                "Telnyx DialAsync requires CallControlConnectionId on TelnyxOptions.");
        }
        var opts = options ?? new OutboundDialOptions();

        var body = new StringBuilder("{");
        body.Append($"\"connection_id\":\"{_options.CallControlConnectionId}\",");
        body.Append($"\"to\":\"{toNumber}\",");
        body.Append($"\"from\":\"{opts.CallerIdOverride ?? fromNumber}\",");
        body.Append($"\"stream_url\":\"{streamUrl}\",");
        body.Append("\"stream_track\":\"both_tracks\",");
        body.Append($"\"timeout_secs\":{opts.RingTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}");
        if (opts.DetectAnsweringMachine)
        {
            body.Append(",\"answering_machine_detection\":\"detect\"");
        }
        body.Append('}');

        using var resp = await _http.PostAsync(
            "/v2/calls",
            new StringContent(body.ToString(), Encoding.UTF8, "application/json"),
            ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var callControlId = doc.RootElement.GetProperty("data").GetProperty("call_control_id").GetString()!;

        var pending = new TelnyxPendingMediaStream(new CallInfo(
            CallId:        callControlId,
            Direction:     CallDirection.Outbound,
            From:          fromNumber,
            To:            toNumber,
            CarrierId:     CarrierId,
            MediaFormat:   CallMediaFormat.Pcm16000,
            StartedAtUtc:  DateTimeOffset.UtcNow));
        return new TelnyxCallSession(pending, this);
    }

    public async ValueTask<IReadOnlyList<ProvisionedNumber>> ListNumbersAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<ProvisionedNumber>();

        var path = "/v2/phone_numbers?page[size]=100";
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Telnyx ListNumbers returned {Status}", resp.StatusCode);
            return Array.Empty<ProvisionedNumber>();
        }

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var list = new List<ProvisionedNumber>();
        if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var pn = item.GetProperty("phone_number").GetString()!;
                list.Add(new ProvisionedNumber(
                    PhoneNumber:           pn,
                    CarrierId:             CarrierId,
                    ProvisionedAtUtc:      DateTimeOffset.UtcNow,
                    MonthlyRecurringCost:  0m));
            }
        }
        return list;
    }

    /// <summary>(3.3.0) Hang up an in-progress call. Used by sessions on HangUp.</summary>
    internal async ValueTask EndCallAsync(string callControlId, CancellationToken ct = default)
    {
        if (!IsConfigured) return;
        using var resp = await _http.PostAsync(
            $"/v2/calls/{callControlId}/actions/hangup",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Telnyx Hangup {Id} returned {Status}", callControlId, resp.StatusCode);
        }
    }

    /// <summary>(3.3.0) Transfer an in-progress call to a new destination.</summary>
    internal async ValueTask TransferCallAsync(
        string             callControlId,
        string             targetNumber,
        CancellationToken  ct = default)
    {
        EnsureConfigured();
        var body = $$"""{"to":"{{targetNumber}}"}""";
        using var resp = await _http.PostAsync(
            $"/v2/calls/{callControlId}/actions/transfer",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Telnyx Transfer {Id} returned {Status}", callControlId, resp.StatusCode);
        }
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Telnyx carrier is not configured. Set TelnyxOptions.ApiKey before calling REST operations.");
        }
    }

    private static decimal? ParseMonthlyCost(JsonElement el)
    {
        if (el.TryGetProperty("cost_information", out var cost) &&
            cost.TryGetProperty("monthly_cost", out var monthly))
        {
            return monthly.ValueKind switch
            {
                JsonValueKind.Number => monthly.GetDecimal(),
                JsonValueKind.String when decimal.TryParse(monthly.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
                _ => null,
            };
        }
        return null;
    }
}
