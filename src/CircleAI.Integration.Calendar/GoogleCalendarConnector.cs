// GoogleCalendarConnector.cs
//
// (Phase B1) Google Calendar v3 client using a host-supplied OAuth bearer
// token. The host owns the OAuth flow (web redirect, refresh, scope
// granting); this connector just lifts events through the v3 REST API.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Integration;

namespace CircleAI.Integration.Calendar;

/// <summary>(Phase B1) Optional config for the Google Calendar connector.</summary>
/// <param name="AccessTokenProvider">Async callback returning a fresh Bearer token.</param>
/// <param name="CalendarId">Calendar to read/write. Default "primary".</param>
public sealed record GoogleCalendarOptions(
    Func<CancellationToken, ValueTask<string?>> AccessTokenProvider,
    string CalendarId = "primary");

public sealed class GoogleCalendarConnector : ICalendarConnector
{
    private const string BaseUri = "https://www.googleapis.com/calendar/v3/";
    private readonly HttpClient _http;
    private readonly GoogleCalendarOptions _opts;
    private readonly bool _ownsHttp;

    public GoogleCalendarConnector(GoogleCalendarOptions opts)
        : this(opts, new HttpClient { BaseAddress = new Uri(BaseUri) }, owned: true) { }

    public GoogleCalendarConnector(GoogleCalendarOptions opts, HttpClient http, bool owned = false)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(BaseUri);
        _ownsHttp = owned;
    }

    public string ProviderId   => "google-calendar";
    public bool   IsConfigured => _opts.AccessTokenProvider is not null;

    public async ValueTask<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        var path = $"calendars/{Uri.EscapeDataString(_opts.CalendarId)}/events"
                 + $"?timeMin={Uri.EscapeDataString(fromUtc.ToString("O", CultureInfo.InvariantCulture))}"
                 + $"&timeMax={Uri.EscapeDataString(toUtc.ToString("O",   CultureInfo.InvariantCulture))}"
                 + $"&singleEvents=true&orderBy=startTime&maxResults=250";

        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var list = new List<CalendarEvent>();
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in items.EnumerateArray())
            {
                if (ev.TryGetProperty("status", out var status)
                    && string.Equals(status.GetString(), "cancelled", StringComparison.Ordinal))
                    continue;

                var (startUtc, isAllDay) = ParseTime(ev, "start");
                var (endUtc,   _)        = ParseTime(ev, "end");

                var attendees = new List<string>();
                if (ev.TryGetProperty("attendees", out var atts) && atts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in atts.EnumerateArray())
                        if (a.TryGetProperty("email", out var em)) attendees.Add(em.GetString() ?? "");
                }

                list.Add(new CalendarEvent(
                    EventId:     ev.GetProperty("id").GetString() ?? "",
                    CalendarId:  _opts.CalendarId,
                    Title:       ev.TryGetProperty("summary",     out var s) ? s.GetString() ?? "" : "",
                    Description: ev.TryGetProperty("description", out var d) ? d.GetString() : null,
                    Location:    ev.TryGetProperty("location",    out var l) ? l.GetString() : null,
                    StartUtc:    startUtc,
                    EndUtc:      endUtc,
                    IsAllDay:    isAllDay,
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
            summary     = ev.Title,
            description = ev.Description,
            location    = ev.Location,
            start = ev.IsAllDay
                ? (object)new { date     = ev.StartUtc.UtcDateTime.ToString("yyyy-MM-dd") }
                :          new { dateTime = ev.StartUtc.ToString("O", CultureInfo.InvariantCulture), timeZone = "UTC" },
            end = ev.IsAllDay
                ? (object)new { date     = ev.EndUtc.UtcDateTime.ToString("yyyy-MM-dd") }
                :          new { dateTime = ev.EndUtc.ToString("O",   CultureInfo.InvariantCulture), timeZone = "UTC" },
            attendees = ev.Attendees.Select(a => new { email = a }).ToArray(),
        };
        using var resp = await _http.PostAsJsonAsync(
            $"calendars/{Uri.EscapeDataString(ev.CalendarId)}/events", body, cancellationToken: ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);
        var created = ev with { EventId = doc.RootElement.GetProperty("id").GetString() ?? "" };
        return created;
    }

    public async ValueTask DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("calendarId required");
        if (string.IsNullOrWhiteSpace(eventId))    throw new ArgumentException("eventId required");
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        using var resp = await _http.DeleteAsync(
            $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", ct).ConfigureAwait(false);
        if (resp.StatusCode != System.Net.HttpStatusCode.NoContent && resp.StatusCode != System.Net.HttpStatusCode.Gone)
            resp.EnsureSuccessStatusCode();
    }

    private async ValueTask EnsureAuthAsync(CancellationToken ct)
    {
        var token = await _opts.AccessTokenProvider(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Google Calendar access token unavailable; refresh OAuth.");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static (DateTimeOffset Utc, bool AllDay) ParseTime(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var node)) return (DateTimeOffset.MinValue, false);
        if (node.TryGetProperty("dateTime", out var dt) && dt.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(dt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return (dto.ToUniversalTime(), false);
        }
        if (node.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String)
        {
            if (DateOnly.TryParse(d.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return (new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), true);
        }
        return (DateTimeOffset.MinValue, false);
    }
}
