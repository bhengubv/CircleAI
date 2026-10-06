// MsGraphCalendarConnector.cs
//
// (Phase B1) Microsoft Graph 1.0 client for Outlook / Microsoft 365
// calendar. Same shape as the Google connector — host supplies access
// tokens via callback.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Integration;

namespace CircleAI.Integration.Calendar;

/// <param name="AccessTokenProvider">Async callback returning a fresh Bearer token.</param>
/// <param name="CalendarId">Calendar to read/write. Default "primary" → the user's default calendar.</param>
public sealed record MsGraphCalendarOptions(
    Func<CancellationToken, ValueTask<string?>> AccessTokenProvider,
    string CalendarId = "primary");

public sealed class MsGraphCalendarConnector : ICalendarConnector
{
    private const string BaseUri = "https://graph.microsoft.com/v1.0/";
    private readonly HttpClient _http;
    private readonly MsGraphCalendarOptions _opts;

    public MsGraphCalendarConnector(MsGraphCalendarOptions opts)
        : this(opts, new HttpClient { BaseAddress = new Uri(BaseUri) }) { }

    public MsGraphCalendarConnector(MsGraphCalendarOptions opts, HttpClient http)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(BaseUri);
    }

    public string ProviderId   => "ms-graph-calendar";
    public bool   IsConfigured => _opts.AccessTokenProvider is not null;

    public async ValueTask<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        var path = $"me/calendar/calendarView"
                 + $"?startDateTime={Uri.EscapeDataString(fromUtc.ToString("O", CultureInfo.InvariantCulture))}"
                 + $"&endDateTime={Uri.EscapeDataString(toUtc.ToString("O",   CultureInfo.InvariantCulture))}"
                 + $"&$top=250&$orderby=start/dateTime";
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var list = new List<CalendarEvent>();
        if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in arr.EnumerateArray())
            {
                var attendees = new List<string>();
                if (ev.TryGetProperty("attendees", out var atts) && atts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in atts.EnumerateArray())
                        if (a.TryGetProperty("emailAddress", out var em)
                            && em.TryGetProperty("address", out var addr))
                            attendees.Add(addr.GetString() ?? "");
                }

                var startUtc = ParseGraphTime(ev, "start");
                var endUtc   = ParseGraphTime(ev, "end");
                var allDay   = ev.TryGetProperty("isAllDay", out var ad) && ad.GetBoolean();

                list.Add(new CalendarEvent(
                    EventId:     ev.GetProperty("id").GetString() ?? "",
                    CalendarId:  _opts.CalendarId,
                    Title:       ev.TryGetProperty("subject",  out var s) ? s.GetString() ?? "" : "",
                    Description: ev.TryGetProperty("bodyPreview", out var d) ? d.GetString() : null,
                    Location:    ev.TryGetProperty("location", out var loc)
                                  && loc.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                    StartUtc:    startUtc,
                    EndUtc:      endUtc,
                    IsAllDay:    allDay,
                    Attendees:   attendees));
            }
        }
        return list;
    }

    public async ValueTask<CalendarEvent> CreateEventAsync(CalendarEvent ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        var body = new
        {
            subject = ev.Title,
            body    = new { contentType = "text", content = ev.Description ?? "" },
            start   = new { dateTime = ev.StartUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), timeZone = "UTC" },
            end     = new { dateTime = ev.EndUtc.UtcDateTime.ToString("O",   CultureInfo.InvariantCulture), timeZone = "UTC" },
            isAllDay = ev.IsAllDay,
            location = new { displayName = ev.Location ?? "" },
            attendees = ev.Attendees
                .Select(a => new { emailAddress = new { address = a }, type = "required" })
                .ToArray(),
        };
        using var resp = await _http.PostAsJsonAsync("me/events", body, cancellationToken: ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);
        return ev with { EventId = doc.RootElement.GetProperty("id").GetString() ?? "" };
    }

    public async ValueTask DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required");
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        using var resp = await _http.DeleteAsync($"me/events/{Uri.EscapeDataString(eventId)}", ct).ConfigureAwait(false);
        if (resp.StatusCode != System.Net.HttpStatusCode.NoContent)
            resp.EnsureSuccessStatusCode();
    }

    private async ValueTask EnsureAuthAsync(CancellationToken ct)
    {
        var token = await _opts.AccessTokenProvider(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Microsoft Graph access token unavailable; refresh OAuth.");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static DateTimeOffset ParseGraphTime(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var node)) return DateTimeOffset.MinValue;
        var dt = node.TryGetProperty("dateTime", out var v) ? v.GetString() : null;
        if (string.IsNullOrEmpty(dt)) return DateTimeOffset.MinValue;
        if (DateTimeOffset.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.ToUniversalTime();
        return DateTimeOffset.MinValue;
    }
}
