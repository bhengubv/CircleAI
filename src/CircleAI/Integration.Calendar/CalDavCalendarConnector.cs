// CalDavCalendarConnector.cs
//
// (Phase B1) Generic CalDAV connector — covers iCloud, Fastmail, Posteo,
// Nextcloud, ownCloud, every other CalDAV server. Authenticates via
// HTTP Basic (or app-specific password) and uses the standard CalDAV
// REPORT verb to fetch events in a time range.
//
// Note: this is a deliberately small, dependency-free CalDAV client.
// For full CalDAV semantics (recurrence expansion, ACLs, etags) use a
// library; for the Companion's read-mostly workload this is sufficient.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CircleAI.Integration;

namespace CircleAI.Integration.Calendar;

/// <param name="CalendarUri">Full URL of the calendar collection (e.g. https://calendar.example.com/dav/user/calendars/personal/).</param>
/// <param name="Username">CalDAV username.</param>
/// <param name="Password">CalDAV password (often an app-specific password).</param>
public sealed record CalDavCalendarOptions(Uri CalendarUri, string Username, string Password);

public sealed class CalDavCalendarConnector : ICalendarConnector
{
    private readonly HttpClient _http;
    private readonly CalDavCalendarOptions _opts;

    public CalDavCalendarConnector(CalDavCalendarOptions opts)
        : this(opts, new HttpClient()) { }

    public CalDavCalendarConnector(CalDavCalendarOptions opts, HttpClient http)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        var creds = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{opts.Username}:{opts.Password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
    }

    public string ProviderId   => "caldav";
    public bool   IsConfigured =>
        !string.IsNullOrWhiteSpace(_opts.Username) && !string.IsNullOrWhiteSpace(_opts.Password);

    public async ValueTask<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        // CalDAV REPORT with time-range filter.
        var xml = $$"""
            <?xml version="1.0" encoding="utf-8" ?>
            <C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop>
                <D:getetag/>
                <C:calendar-data/>
              </D:prop>
              <C:filter>
                <C:comp-filter name="VCALENDAR">
                  <C:comp-filter name="VEVENT">
                    <C:time-range start="{{fromUtc:yyyyMMddTHHmmssZ}}" end="{{toUtc:yyyyMMddTHHmmssZ}}"/>
                  </C:comp-filter>
                </C:comp-filter>
              </C:filter>
            </C:calendar-query>
            """;
        using var req = new HttpRequestMessage(new HttpMethod("REPORT"), _opts.CalendarUri)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        };
        req.Headers.Add("Depth", "1");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var doc = XDocument.Parse(body);
        XNamespace cal = "urn:ietf:params:xml:ns:caldav";
        var result = new List<CalendarEvent>();
        foreach (var calData in doc.Descendants(cal + "calendar-data"))
        {
            foreach (var ev in ParseIcs(calData.Value, _opts.CalendarUri.ToString()))
                result.Add(ev);
        }
        return result;
    }

    public async ValueTask<CalendarEvent> CreateEventAsync(CalendarEvent ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var uid = string.IsNullOrWhiteSpace(ev.EventId) ? Guid.NewGuid().ToString("N") : ev.EventId;
        var ics = BuildIcs(ev with { EventId = uid });
        var targetUri = new Uri(_opts.CalendarUri, uid + ".ics");

        using var req = new HttpRequestMessage(HttpMethod.Put, targetUri)
        {
            Content = new StringContent(ics, Encoding.UTF8, "text/calendar"),
        };
        req.Headers.Add("If-None-Match", "*");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return ev with { EventId = uid };
    }

    public async ValueTask DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required");
        var targetUri = new Uri(_opts.CalendarUri, eventId + ".ics");
        using var resp = await _http.DeleteAsync(targetUri, ct).ConfigureAwait(false);
        if (resp.StatusCode != System.Net.HttpStatusCode.NoContent
            && resp.StatusCode != System.Net.HttpStatusCode.OK
            && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            resp.EnsureSuccessStatusCode();
    }

    // ── Minimal ICS parser ───────────────────────────────────────────────

    private static IEnumerable<CalendarEvent> ParseIcs(string ics, string calendarId)
    {
        if (string.IsNullOrWhiteSpace(ics)) yield break;
        var rxEvent = new Regex(@"BEGIN:VEVENT(?<body>.*?)END:VEVENT", RegexOptions.Singleline);
        foreach (Match m in rxEvent.Matches(ics))
        {
            var body = m.Groups["body"].Value;
            string Get(string key)
            {
                var line = Regex.Match(body, $@"(?m)^{Regex.Escape(key)}(?:;[^:]*)?:(.*)$");
                return line.Success ? line.Groups[1].Value.Trim() : "";
            }
            DateTimeOffset Time(string key)
            {
                var v = Get(key);
                if (string.IsNullOrEmpty(v)) return DateTimeOffset.MinValue;
                if (DateTimeOffset.TryParseExact(v, "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                    return dt.ToUniversalTime();
                if (DateOnly.TryParseExact(v, "yyyyMMdd", out var dOnly))
                    return new DateTimeOffset(dOnly.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                return DateTimeOffset.MinValue;
            }
            var uid     = Get("UID");
            var title   = Get("SUMMARY");
            var desc    = Get("DESCRIPTION");
            var loc     = Get("LOCATION");
            var startUtc = Time("DTSTART");
            var endUtc   = Time("DTEND");
            yield return new CalendarEvent(
                EventId:     uid,
                CalendarId:  calendarId,
                Title:       title,
                Description: string.IsNullOrEmpty(desc) ? null : desc,
                Location:    string.IsNullOrEmpty(loc) ? null : loc,
                StartUtc:    startUtc,
                EndUtc:      endUtc,
                IsAllDay:    startUtc != DateTimeOffset.MinValue && startUtc.TimeOfDay == TimeSpan.Zero && endUtc.TimeOfDay == TimeSpan.Zero,
                Attendees:   Array.Empty<string>());
        }
    }

    private static string BuildIcs(CalendarEvent ev)
    {
        var dtStamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var dtStart = ev.StartUtc.UtcDateTime.ToString("yyyyMMddTHHmmssZ");
        var dtEnd   = ev.EndUtc.UtcDateTime.ToString("yyyyMMddTHHmmssZ");
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//CircleAI//Calendar//EN");
        sb.AppendLine("BEGIN:VEVENT");
        sb.AppendLine($"UID:{ev.EventId}");
        sb.AppendLine($"DTSTAMP:{dtStamp}");
        sb.AppendLine($"DTSTART:{dtStart}");
        sb.AppendLine($"DTEND:{dtEnd}");
        sb.AppendLine($"SUMMARY:{Escape(ev.Title)}");
        if (!string.IsNullOrEmpty(ev.Description)) sb.AppendLine($"DESCRIPTION:{Escape(ev.Description!)}");
        if (!string.IsNullOrEmpty(ev.Location))    sb.AppendLine($"LOCATION:{Escape(ev.Location!)}");
        sb.AppendLine("END:VEVENT");
        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace(",", "\\,").Replace(";", "\\;");
}
