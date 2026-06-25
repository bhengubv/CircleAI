// PlivoCarrier.cs
//
// (3.3.0) Plivo v1 REST API adapter. Speaks Basic auth (AuthId +
// AuthToken), the /v1/Account/{AuthId}/ namespace, and the
// AnswerUrl-driven Audio Streaming flow.

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

namespace CircleAI.Telephony.Plivo;

/// <summary>
/// (3.3.0) <see cref="ITelephonyCarrier"/> backed by Plivo's v1 REST API.
/// Fail-soft when credentials missing.
/// </summary>
public sealed class PlivoCarrier : ITelephonyCarrier
{
    private readonly HttpClient _http;
    private readonly PlivoOptions _options;
    private readonly ILogger _logger;

    public PlivoCarrier(HttpClient http, PlivoOptions options, ILogger<PlivoCarrier>? logger = null)
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
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.AuthId}:{_options.AuthToken}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }
    }

    public string CarrierId    => "plivo";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.AuthId)
                              && !string.IsNullOrWhiteSpace(_options.AuthToken);

    public async ValueTask<ProvisionedNumber> ProvisionNumberAsync(
        string             countryCode,
        string?            areaCode = null,
        CancellationToken  ct       = default)
    {
        EnsureConfigured();

        // GET /v1/Account/{Sid}/PhoneNumber/?country_iso={cc}&limit=1[&pattern={area}]
        var path = $"/v1/Account/{_options.AuthId}/PhoneNumber/?country_iso={countryCode}&limit=1";
        if (!string.IsNullOrWhiteSpace(areaCode))
        {
            path += $"&pattern={Uri.EscapeDataString(areaCode)}";
        }

        using var searchResp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        searchResp.EnsureSuccessStatusCode();
        using var searchDoc = await JsonDocument.ParseAsync(
            await searchResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var objects = searchDoc.RootElement.GetProperty("objects");
        var first = objects.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException(
                $"Plivo has no available numbers in country='{countryCode}', areaCode='{areaCode}'.");
        }

        var phoneNumber = first.GetProperty("number").GetString()!;

        // POST /v1/Account/{Sid}/PhoneNumber/{number}/  — buy it.
        var buyPath = $"/v1/Account/{_options.AuthId}/PhoneNumber/{phoneNumber}/";
        var buyForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("app_id", ""),
        });
        using var buyResp = await _http.PostAsync(buyPath, buyForm, ct).ConfigureAwait(false);
        buyResp.EnsureSuccessStatusCode();

        return new ProvisionedNumber(
            PhoneNumber:           phoneNumber,
            CarrierId:             CarrierId,
            ProvisionedAtUtc:      DateTimeOffset.UtcNow,
            MonthlyRecurringCost:  ParseDecimal(first, "monthly_rental_rate") ?? 0m);
    }

    public async ValueTask ConfigureInboundWebhookAsync(
        string             phoneNumber,
        Uri                inboundWebhook,
        CancellationToken  ct = default)
    {
        EnsureConfigured();

        // PATCH-equivalent (Plivo uses POST for updates on the Number/ resource).
        var path = $"/v1/Account/{_options.AuthId}/Number/{phoneNumber}/";
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("answer_url",     inboundWebhook.ToString()),
            new KeyValuePair<string, string>("answer_method",  "POST"),
        });
        using var resp = await _http.PostAsync(path, form, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async ValueTask<ICallSession> DialAsync(
        string             fromNumber,
        string             toNumber,
        Uri                streamUrl,
        OutboundDialOptions? options = null,
        CancellationToken  ct       = default)
    {
        EnsureConfigured();
        if (_options.AnswerUrlBase is null)
        {
            throw new InvalidOperationException(
                "Plivo DialAsync requires PlivoOptions.AnswerUrlBase. The host must serve XML containing a <Stream/> verb pointing to the streamUrl.");
        }
        var opts = options ?? new OutboundDialOptions();

        // Compose the answer URL with the stream wss:// embedded as a query param.
        var answerUrl = new UriBuilder(_options.AnswerUrlBase);
        var existingQuery = answerUrl.Query?.TrimStart('?') ?? "";
        var separator = string.IsNullOrEmpty(existingQuery) ? "" : "&";
        answerUrl.Query = existingQuery + separator + "stream=" + Uri.EscapeDataString(streamUrl.ToString());

        var formPairs = new List<KeyValuePair<string, string>>
        {
            new("from",          opts.CallerIdOverride ?? fromNumber),
            new("to",            toNumber),
            new("answer_url",    answerUrl.Uri.ToString()),
            new("answer_method", "POST"),
            new("ring_timeout",  opts.RingTimeoutSeconds.ToString(CultureInfo.InvariantCulture)),
        };
        if (opts.DetectAnsweringMachine)
        {
            formPairs.Add(new("machine_detection", "true"));
        }

        var path = $"/v1/Account/{_options.AuthId}/Call/";
        using var resp = await _http.PostAsync(path, new FormUrlEncodedContent(formPairs), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var requestUuid = doc.RootElement.GetProperty("request_uuid").GetString()!;

        var pending = new PlivoPendingMediaStream(new CallInfo(
            CallId:        requestUuid,
            Direction:     CallDirection.Outbound,
            From:          fromNumber,
            To:            toNumber,
            CarrierId:     CarrierId,
            MediaFormat:   CallMediaFormat.Mulaw8000,
            StartedAtUtc:  DateTimeOffset.UtcNow));
        return new PlivoCallSession(pending, this);
    }

    public async ValueTask<IReadOnlyList<ProvisionedNumber>> ListNumbersAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<ProvisionedNumber>();

        var path = $"/v1/Account/{_options.AuthId}/Number/?limit=100";
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Plivo ListNumbers returned {Status}", resp.StatusCode);
            return Array.Empty<ProvisionedNumber>();
        }

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var list = new List<ProvisionedNumber>();
        if (doc.RootElement.TryGetProperty("objects", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var pn = item.GetProperty("number").GetString()!;
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
    internal async ValueTask EndCallAsync(string callUuid, CancellationToken ct = default)
    {
        if (!IsConfigured) return;
        using var resp = await _http.DeleteAsync(
            $"/v1/Account/{_options.AuthId}/Call/{callUuid}/",
            ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Plivo Hangup {Uuid} returned {Status}", callUuid, resp.StatusCode);
        }
    }

    /// <summary>(3.3.0) Transfer an in-progress call by replaying the answer XML.</summary>
    internal async ValueTask TransferCallAsync(string callUuid, string targetNumber, CancellationToken ct = default)
    {
        EnsureConfigured();
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("aleg_url", $"data:application/xml,{Uri.EscapeDataString($"<Response><Dial><Number>{targetNumber}</Number></Dial></Response>")}"),
            new KeyValuePair<string, string>("aleg_method", "POST"),
        });
        using var resp = await _http.PostAsync(
            $"/v1/Account/{_options.AuthId}/Call/{callUuid}/",
            form, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Plivo Transfer {Uuid} returned {Status}", callUuid, resp.StatusCode);
        }
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Plivo carrier is not configured. Set PlivoOptions.AuthId and AuthToken before calling REST operations.");
        }
    }

    private static decimal? ParseDecimal(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }
}
