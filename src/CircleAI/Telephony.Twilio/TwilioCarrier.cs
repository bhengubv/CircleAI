// TwilioCarrier.cs
//
// (3.3.0) Twilio REST API adapter. Speaks to
// https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/... for
// number provisioning, webhook configuration, outbound dial, and call
// termination. Authenticates via HTTP Basic with AccountSid + AuthToken.

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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Telephony.Twilio;

/// <summary>
/// (3.3.0) <see cref="ITelephonyCarrier"/> backed by Twilio's REST API.
/// Fail-soft when credentials are missing.
/// </summary>
public sealed class TwilioCarrier : ITelephonyCarrier
{
    private readonly HttpClient _http;
    private readonly TwilioOptions _options;
    private readonly ILogger _logger;

    public TwilioCarrier(HttpClient http, TwilioOptions options, ILogger<TwilioCarrier>? logger = null)
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
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.AccountSid}:{_options.AuthToken}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }
    }

    public string CarrierId   => "twilio";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.AccountSid)
                              && !string.IsNullOrWhiteSpace(_options.AuthToken);

    public async ValueTask<ProvisionedNumber> ProvisionNumberAsync(
        string             countryCode,
        string?            areaCode = null,
        CancellationToken  ct       = default)
    {
        EnsureConfigured();

        // POST /2010-04-01/Accounts/{Sid}/IncomingPhoneNumbers.json
        // Find one available first via AvailablePhoneNumbers/{Country}/Local.json
        var path = $"/2010-04-01/Accounts/{_options.AccountSid}/AvailablePhoneNumbers/{countryCode}/Local.json";
        if (!string.IsNullOrWhiteSpace(areaCode))
        {
            path += $"?AreaCode={Uri.EscapeDataString(areaCode)}&Limit=1";
        }
        else
        {
            path += "?Limit=1";
        }

        using var availableResp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        availableResp.EnsureSuccessStatusCode();
        using var availableDoc = await JsonDocument.ParseAsync(
            await availableResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var first = availableDoc.RootElement.GetProperty("available_phone_numbers").EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException(
                $"Twilio has no available numbers in country='{countryCode}', areaCode='{areaCode}'.");
        }

        var phoneNumber = first.GetProperty("phone_number").GetString()!;

        // Reserve it on the account.
        var reservePath = $"/2010-04-01/Accounts/{_options.AccountSid}/IncomingPhoneNumbers.json";
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("PhoneNumber", phoneNumber),
        });
        using var reserveResp = await _http.PostAsync(reservePath, form, ct).ConfigureAwait(false);
        reserveResp.EnsureSuccessStatusCode();

        return new ProvisionedNumber(
            PhoneNumber:           phoneNumber,
            CarrierId:             CarrierId,
            ProvisionedAtUtc:      DateTimeOffset.UtcNow,
            MonthlyRecurringCost:  ParseDecimal(first, "price") ?? 0m);
    }

    public async ValueTask ConfigureInboundWebhookAsync(
        string             phoneNumber,
        Uri                inboundWebhook,
        CancellationToken  ct = default)
    {
        EnsureConfigured();

        // Find the SID of the IncomingPhoneNumber resource for this E.164 number.
        var listPath = $"/2010-04-01/Accounts/{_options.AccountSid}/IncomingPhoneNumbers.json?PhoneNumber={Uri.EscapeDataString(phoneNumber)}";
        using var listResp = await _http.GetAsync(listPath, ct).ConfigureAwait(false);
        listResp.EnsureSuccessStatusCode();
        using var listDoc = await JsonDocument.ParseAsync(
            await listResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var numberEntry = listDoc.RootElement.GetProperty("incoming_phone_numbers").EnumerateArray().FirstOrDefault();
        if (numberEntry.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"Phone number '{phoneNumber}' is not owned on this Twilio account.");
        }

        var sid = numberEntry.GetProperty("sid").GetString()!;
        var configPath = $"/2010-04-01/Accounts/{_options.AccountSid}/IncomingPhoneNumbers/{sid}.json";

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("VoiceUrl", inboundWebhook.ToString()),
            new KeyValuePair<string, string>("VoiceMethod", "POST"),
        });
        using var updateResp = await _http.PostAsync(configPath, form, ct).ConfigureAwait(false);
        updateResp.EnsureSuccessStatusCode();
    }

    public async ValueTask<ICallSession> DialAsync(
        string             fromNumber,
        string             toNumber,
        Uri                streamUrl,
        OutboundDialOptions? options = null,
        CancellationToken  ct       = default)
    {
        EnsureConfigured();
        var opts = options ?? new OutboundDialOptions();

        // POST /2010-04-01/Accounts/{Sid}/Calls.json
        // Twiml inline tells Twilio to <Connect><Stream url='wss://...'/></Connect>.
        var twiml = $"<Response><Connect><Stream url='{System.Net.WebUtility.HtmlEncode(streamUrl.ToString())}'/></Connect></Response>";

        var formPairs = new List<KeyValuePair<string, string>>
        {
            new("From", opts.CallerIdOverride ?? fromNumber),
            new("To",   toNumber),
            new("Twiml", twiml),
            new("Timeout", opts.RingTimeoutSeconds.ToString(CultureInfo.InvariantCulture)),
        };
        if (opts.DetectAnsweringMachine)
        {
            formPairs.Add(new("MachineDetection", "Enable"));
        }
        var form = new FormUrlEncodedContent(formPairs);

        var callsPath = $"/2010-04-01/Accounts/{_options.AccountSid}/Calls.json";
        using var resp = await _http.PostAsync(callsPath, form, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var callSid = doc.RootElement.GetProperty("sid").GetString()!;

        // The actual ICallSession is materialised by the host once the
        // Twilio Media Streams WebSocket connects to streamUrl. We hand
        // back a session shell rooted on a PendingMediaStream that will
        // be completed by the host's stream handler.
        var pending = new PendingMediaStream(new CallInfo(
            CallId:        callSid,
            Direction:     CallDirection.Outbound,
            From:          fromNumber,
            To:            toNumber,
            CarrierId:     CarrierId,
            MediaFormat:   CallMediaFormat.Mulaw8000,
            StartedAtUtc:  DateTimeOffset.UtcNow));
        return new TwilioCallSession(pending, this);
    }

    public async ValueTask<IReadOnlyList<ProvisionedNumber>> ListNumbersAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<ProvisionedNumber>();

        var path = $"/2010-04-01/Accounts/{_options.AccountSid}/IncomingPhoneNumbers.json?PageSize=100";
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Twilio ListNumbers returned {Status}", resp.StatusCode);
            return Array.Empty<ProvisionedNumber>();
        }

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var list = new List<ProvisionedNumber>();
        if (doc.RootElement.TryGetProperty("incoming_phone_numbers", out var arr) && arr.ValueKind == JsonValueKind.Array)
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

    /// <summary>(3.3.0) Redirect an in-progress call to fresh TwiML. Used by sessions on cold transfer.</summary>
    internal async ValueTask RedirectCallAsync(string callSid, string twiml, CancellationToken ct = default)
    {
        EnsureConfigured();
        var path = $"/2010-04-01/Accounts/{_options.AccountSid}/Calls/{callSid}.json";
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Twiml", twiml),
        });
        using var resp = await _http.PostAsync(path, form, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Twilio RedirectCall {Sid} returned {Status}", callSid, resp.StatusCode);
        }
    }

    /// <summary>(3.3.0) End a call by Twilio CallSid via the REST API. Used by sessions on HangUp.</summary>
    internal async ValueTask EndCallAsync(string callSid, CancellationToken ct = default)
    {
        if (!IsConfigured) return;
        var path = $"/2010-04-01/Accounts/{_options.AccountSid}/Calls/{callSid}.json";
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Status", "completed"),
        });
        using var resp = await _http.PostAsync(path, form, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Twilio EndCall {Sid} returned {Status}", callSid, resp.StatusCode);
        }
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Twilio carrier is not configured. Set TwilioOptions.AccountSid and AuthToken before calling REST operations.");
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
