// integration_calendar.go
//
// Ports CircleAI.Integration.Calendar:
//   CalDavCalendarOptions / CalDavCalendarConnector -> CalDavCalendarOptions / CalDavCalendarConnector
//   GoogleCalendarOptions / GoogleCalendarConnector -> GoogleCalendarOptions / GoogleCalendarConnector
//   MsGraphCalendarOptions / MsGraphCalendarConnector -> MsGraphCalendarOptions / MsGraphCalendarConnector
//
// Each connector is an ICalendarConnector speaking a real REST/CalDAV protocol.
// Per the porting rules the live HttpClient is replaced by the injected
// CarrierHTTP seam so the connectors are deterministic and make no real network
// calls; every wire detail (paths, query params, verbs, headers, JSON/ICS bodies,
// and the response field extraction) is reproduced from the C# faithfully. A
// FakeCarrierTransport drives them in tests.

package circleai

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/url"
	"regexp"
	"strings"
	"time"
)

// ── CalDAV ──────────────────────────────────────────────────────────────────

// CalDavCalendarOptions configures the generic CalDAV connector. Ports
// CalDavCalendarOptions. CalendarURI is the full collection URL.
type CalDavCalendarOptions struct {
	CalendarURI string
	Username    string
	Password    string
}

// CalDavCalendarConnector is a dependency-free CalDAV client over the injected
// CarrierHTTP. Ports CalDavCalendarConnector.
type CalDavCalendarConnector struct {
	http    CarrierHTTP
	opts    CalDavCalendarOptions
	authHdr string
	baseURL *url.URL
}

// NewCalDavCalendarConnector constructs the connector over an injected transport.
// http is required (the C# ctor throws on null http/opts). The Basic auth header
// is precomputed from Username:Password, matching the C# constructor.
func NewCalDavCalendarConnector(http CarrierHTTP, opts CalDavCalendarOptions) (*CalDavCalendarConnector, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	base, err := url.Parse(opts.CalendarURI)
	if err != nil {
		return nil, err
	}
	creds := base64.StdEncoding.EncodeToString([]byte(opts.Username + ":" + opts.Password))
	return &CalDavCalendarConnector{
		http:    http,
		opts:    opts,
		authHdr: "Basic " + creds,
		baseURL: base,
	}, nil
}

// ProviderID is "caldav".
func (c *CalDavCalendarConnector) ProviderID() string { return "caldav" }

// IsConfigured is true when Username and Password are both non-blank.
func (c *CalDavCalendarConnector) IsConfigured() bool {
	return stringsTrimSpaceNonEmpty(c.opts.Username) && stringsTrimSpaceNonEmpty(c.opts.Password)
}

func (c *CalDavCalendarConnector) headers(contentType string) map[string]string {
	h := map[string]string{"Authorization": c.authHdr}
	if contentType != "" {
		h["Content-Type"] = contentType
	}
	return h
}

// ListEvents ports ListEventsAsync: a CalDAV REPORT with a VEVENT time-range
// filter, Depth:1, whose multistatus response carries calendar-data ICS blobs
// that are parsed into events.
func (c *CalDavCalendarConnector) ListEvents(_ context.Context, fromUtc, toUtc time.Time) ([]CalendarEvent, error) {
	xml := "<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n" +
		"<C:calendar-query xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\">\n" +
		"  <D:prop>\n" +
		"    <D:getetag/>\n" +
		"    <C:calendar-data/>\n" +
		"  </D:prop>\n" +
		"  <C:filter>\n" +
		"    <C:comp-filter name=\"VCALENDAR\">\n" +
		"      <C:comp-filter name=\"VEVENT\">\n" +
		"        <C:time-range start=\"" + caldavStamp(fromUtc) + "\" end=\"" + caldavStamp(toUtc) + "\"/>\n" +
		"      </C:comp-filter>\n" +
		"    </C:comp-filter>\n" +
		"  </C:filter>\n" +
		"</C:calendar-query>"

	h := c.headers("application/xml")
	h["Depth"] = "1"
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "REPORT",
		URL:     c.opts.CalendarURI,
		Headers: h,
		Body:    []byte(xml),
	})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("CalDAV REPORT", resp.StatusCode)
	}

	result := []CalendarEvent{}
	for _, blob := range extractCaldavCalendarData(string(resp.Body)) {
		result = append(result, parseICS(blob, c.opts.CalendarURI)...)
	}
	return result, nil
}

// CreateEvent ports CreateEventAsync: PUT the built ICS to <collection>/<uid>.ics
// with If-None-Match:*.
func (c *CalDavCalendarConnector) CreateEvent(_ context.Context, ev CalendarEvent) (CalendarEvent, error) {
	uid := ev.EventID
	if !stringsTrimSpaceNonEmpty(uid) {
		uid = newGUIDHex()
	}
	created := ev
	created.EventID = uid
	ics := buildICS(created)

	target, err := c.resolveICS(uid)
	if err != nil {
		return CalendarEvent{}, err
	}
	h := c.headers("text/calendar")
	h["If-None-Match"] = "*"
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "PUT",
		URL:     target,
		Headers: h,
		Body:    []byte(ics),
	})
	if err != nil {
		return CalendarEvent{}, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return CalendarEvent{}, statusError("CalDAV PUT", resp.StatusCode)
	}
	return created, nil
}

// DeleteEvent ports DeleteEventAsync: DELETE <collection>/<eventId>.ics; treats
// 204/200/404 as success and only fails a non-2xx otherwise.
func (c *CalDavCalendarConnector) DeleteEvent(_ context.Context, _ string, eventID string) error {
	if !stringsTrimSpaceNonEmpty(eventID) {
		return errors.New("eventId required")
	}
	target, err := c.resolveICS(eventID)
	if err != nil {
		return err
	}
	resp, err := c.http.Do(&CarrierHTTPRequest{Method: "DELETE", URL: target, Headers: c.headers("")})
	if err != nil {
		return err
	}
	switch resp.StatusCode {
	case 204, 200, 404:
		return nil
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return statusError("CalDAV DELETE", resp.StatusCode)
	}
	return nil
}

// resolveICS resolves "<name>.ics" relative to the collection URI (C# new
// Uri(base, name+".ics")).
func (c *CalDavCalendarConnector) resolveICS(name string) (string, error) {
	ref, err := url.Parse(name + ".ics")
	if err != nil {
		return "", err
	}
	return c.baseURL.ResolveReference(ref).String(), nil
}

// extractCaldavCalendarData pulls every <...:calendar-data> element's inner text
// from a CalDAV multistatus body. The C# uses XDocument.Descendants(cal +
// "calendar-data").Value against the urn:ietf:params:xml:ns:caldav namespace; the
// element is conventionally the "C:" / "cal:" prefix. A namespace-agnostic
// local-name match reproduces that without pulling in a full XML DOM.
func extractCaldavCalendarData(body string) []string {
	rx := regexp.MustCompile(`(?s)<(?:[A-Za-z0-9_.-]+:)?calendar-data[^>]*>(.*?)</(?:[A-Za-z0-9_.-]+:)?calendar-data>`)
	var out []string
	for _, m := range rx.FindAllStringSubmatch(body, -1) {
		out = append(out, xmlUnescape(m[1]))
	}
	return out
}

// xmlUnescape reverses the five predefined XML entities in text content, matching
// what an XML reader returns for XElement.Value.
func xmlUnescape(s string) string {
	r := strings.NewReplacer("&lt;", "<", "&gt;", ">", "&quot;", "\"", "&apos;", "'", "&amp;", "&")
	return r.Replace(s)
}

// ── Minimal ICS parser (ports ParseIcs / BuildIcs / Escape) ─────────────────

var rxVEvent = regexp.MustCompile(`(?s)BEGIN:VEVENT(.*?)END:VEVENT`)

// parseICS extracts VEVENTs from an ICS blob into CalendarEvents. Ports ParseIcs.
func parseICS(ics, calendarID string) []CalendarEvent {
	if !stringsTrimSpaceNonEmpty(ics) {
		return nil
	}
	var out []CalendarEvent
	for _, m := range rxVEvent.FindAllStringSubmatch(ics, -1) {
		body := m[1]
		get := func(key string) string { return icsGet(body, key) }
		timeOf := func(key string) time.Time { return parseCaldavTime(get(key)) }

		uid := get("UID")
		title := get("SUMMARY")
		desc := get("DESCRIPTION")
		loc := get("LOCATION")
		startUtc := timeOf("DTSTART")
		endUtc := timeOf("DTEND")

		ev := CalendarEvent{
			EventID:    uid,
			CalendarID: calendarID,
			Title:      title,
			StartUtc:   startUtc,
			EndUtc:     endUtc,
			IsAllDay: !startUtc.IsZero() &&
				isMidnightUTC(startUtc) && isMidnightUTC(endUtc),
			Attendees: []string{},
		}
		if desc != "" {
			ev.Description = strPtr(desc)
		}
		if loc != "" {
			ev.Location = strPtr(loc)
		}
		out = append(out, ev)
	}
	return out
}

// icsGet returns the trimmed value of the first line "KEY(;params)?:value".
// Ports the inner Get() regex (multiline, key-escaped).
func icsGet(body, key string) string {
	rx := regexp.MustCompile(`(?m)^` + regexp.QuoteMeta(key) + `(?:;[^:]*)?:(.*)$`)
	m := rx.FindStringSubmatch(body)
	if m == nil {
		return ""
	}
	return strings.TrimSpace(m[1])
}

// isMidnightUTC reports whether t's UTC time-of-day is exactly 00:00:00.000.
// Mirrors DateTimeOffset.TimeOfDay == TimeSpan.Zero.
func isMidnightUTC(t time.Time) bool {
	u := t.UTC()
	return u.Hour() == 0 && u.Minute() == 0 && u.Second() == 0 && u.Nanosecond() == 0
}

// buildICS renders a VEVENT ICS document. Ports BuildIcs (using \r\n line breaks
// as StringBuilder.AppendLine emits Environment.NewLine; the wire value is passed
// straight to the fake transport, so the exact separator is preserved).
func buildICS(ev CalendarEvent) string {
	dtStamp := caldavStamp(nowUTCFunc())
	dtStart := caldavStamp(ev.StartUtc)
	dtEnd := caldavStamp(ev.EndUtc)
	var sb strings.Builder
	appendLine(&sb, "BEGIN:VCALENDAR")
	appendLine(&sb, "VERSION:2.0")
	appendLine(&sb, "PRODID:-//CircleAI//Calendar//EN")
	appendLine(&sb, "BEGIN:VEVENT")
	appendLine(&sb, "UID:"+ev.EventID)
	appendLine(&sb, "DTSTAMP:"+dtStamp)
	appendLine(&sb, "DTSTART:"+dtStart)
	appendLine(&sb, "DTEND:"+dtEnd)
	appendLine(&sb, "SUMMARY:"+icsEscape(ev.Title))
	if ev.Description != nil && *ev.Description != "" {
		appendLine(&sb, "DESCRIPTION:"+icsEscape(*ev.Description))
	}
	if ev.Location != nil && *ev.Location != "" {
		appendLine(&sb, "LOCATION:"+icsEscape(*ev.Location))
	}
	appendLine(&sb, "END:VEVENT")
	appendLine(&sb, "END:VCALENDAR")
	return sb.String()
}

func appendLine(sb *strings.Builder, s string) {
	sb.WriteString(s)
	sb.WriteString("\r\n")
}

// icsEscape escapes an ICS text value. Ports Escape (\\, \n, comma, semicolon).
func icsEscape(s string) string {
	r := strings.NewReplacer(`\`, `\\`, "\n", `\n`, ",", `\,`, ";", `\;`)
	return r.Replace(s)
}

// ── Google Calendar ─────────────────────────────────────────────────────────

// googleCalendarBaseURI is the Google Calendar v3 base.
const googleCalendarBaseURI = "https://www.googleapis.com/calendar/v3/"

// AccessTokenProvider returns a fresh Bearer token (or "" when unavailable).
// Ports Func<CancellationToken, ValueTask<string?>>.
type AccessTokenProvider func(ctx context.Context) (string, error)

// GoogleCalendarOptions configures the Google Calendar connector. Ports
// GoogleCalendarOptions. CalendarID defaults to "primary" (use "" to mean
// default; NewGoogleCalendarConnector normalises).
type GoogleCalendarOptions struct {
	AccessTokenProvider AccessTokenProvider
	CalendarID          string
}

// GoogleCalendarConnector is a Google Calendar v3 client over the injected
// CarrierHTTP. Ports GoogleCalendarConnector.
type GoogleCalendarConnector struct {
	http CarrierHTTP
	opts GoogleCalendarOptions
	base string
}

// NewGoogleCalendarConnector constructs the connector. http is required; an empty
// CalendarID defaults to "primary" (matching the C# record default).
func NewGoogleCalendarConnector(http CarrierHTTP, opts GoogleCalendarOptions) (*GoogleCalendarConnector, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	if opts.CalendarID == "" {
		opts.CalendarID = "primary"
	}
	return &GoogleCalendarConnector{http: http, opts: opts, base: googleCalendarBaseURI}, nil
}

// ProviderID is "google-calendar".
func (c *GoogleCalendarConnector) ProviderID() string { return "google-calendar" }

// IsConfigured is true when an AccessTokenProvider is set.
func (c *GoogleCalendarConnector) IsConfigured() bool { return c.opts.AccessTokenProvider != nil }

// ensureAuth resolves a token and returns the Authorization header value. Ports
// EnsureAuthAsync (throws when the token is blank).
func (c *GoogleCalendarConnector) ensureAuth(ctx context.Context) (string, error) {
	if c.opts.AccessTokenProvider == nil {
		return "", errors.New("Google Calendar access token unavailable; refresh OAuth.")
	}
	token, err := c.opts.AccessTokenProvider(ctx)
	if err != nil {
		return "", err
	}
	if !stringsTrimSpaceNonEmpty(token) {
		return "", errors.New("Google Calendar access token unavailable; refresh OAuth.")
	}
	return "Bearer " + token, nil
}

// ListEvents ports ListEventsAsync: GET calendars/{id}/events with
// timeMin/timeMax/singleEvents/orderBy/maxResults, skipping cancelled items.
func (c *GoogleCalendarConnector) ListEvents(ctx context.Context, fromUtc, toUtc time.Time) ([]CalendarEvent, error) {
	auth, err := c.ensureAuth(ctx)
	if err != nil {
		return nil, err
	}
	path := "calendars/" + escapeDataString(c.opts.CalendarID) + "/events" +
		"?timeMin=" + escapeDataString(isoRoundTrip(fromUtc)) +
		"&timeMax=" + escapeDataString(isoRoundTrip(toUtc)) +
		"&singleEvents=true&orderBy=startTime&maxResults=250"

	resp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, path), Headers: map[string]string{"Authorization": auth}})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("Google Calendar events", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return nil, err
	}
	list := []CalendarEvent{}
	items, ok := tjArray(root, "items")
	if !ok {
		return list, nil
	}
	for _, it := range items {
		ev, ok := asJSONObject(it)
		if !ok {
			continue
		}
		if status, ok := tjString(ev, "status"); ok && status == "cancelled" {
			continue
		}
		startUtc, _ := googleParseTime(ev, "start")
		endUtc, _ := googleParseTime(ev, "end")

		attendees := []string{}
		if atts, ok := tjArray(ev, "attendees"); ok {
			for _, a := range atts {
				if am, ok := asJSONObject(a); ok {
					em, _ := tjString(am, "email")
					attendees = append(attendees, em)
				}
			}
		}
		id, _ := tjString(ev, "id")
		title, _ := tjString(ev, "summary")
		ce := CalendarEvent{
			EventID:    id,
			CalendarID: c.opts.CalendarID,
			Title:      title,
			StartUtc:   startUtc,
			EndUtc:     endUtc,
			IsAllDay:   startAllDay(ev),
			Attendees:  attendees,
		}
		if d, ok := tjString(ev, "description"); ok {
			ce.Description = strPtr(d)
		}
		if l, ok := tjString(ev, "location"); ok {
			ce.Location = strPtr(l)
		}
		list = append(list, ce)
	}
	return list, nil
}

// startAllDay reports whether the "start" node carries a "date" (all-day) rather
// than a "dateTime". Mirrors the isAllDay bool from ParseTime(ev,"start").
func startAllDay(ev map[string]interface{}) bool {
	_, allDay := googleParseTime(ev, "start")
	return allDay
}

// CreateEvent ports CreateEventAsync: POST the event JSON to calendars/{id}/events
// and read the assigned id.
func (c *GoogleCalendarConnector) CreateEvent(ctx context.Context, ev CalendarEvent) (CalendarEvent, error) {
	auth, err := c.ensureAuth(ctx)
	if err != nil {
		return CalendarEvent{}, err
	}
	body := map[string]interface{}{
		"summary":     ev.Title,
		"description": derefOrNil(ev.Description),
		"location":    derefOrNil(ev.Location),
		"attendees":   googleAttendees(ev.Attendees),
	}
	if ev.IsAllDay {
		body["start"] = map[string]interface{}{"date": isoDateOnly(ev.StartUtc)}
		body["end"] = map[string]interface{}{"date": isoDateOnly(ev.EndUtc)}
	} else {
		body["start"] = map[string]interface{}{"dateTime": isoRoundTrip(ev.StartUtc), "timeZone": "UTC"}
		body["end"] = map[string]interface{}{"dateTime": isoRoundTrip(ev.EndUtc), "timeZone": "UTC"}
	}
	payload, _ := json.Marshal(body)
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, "calendars/"+escapeDataString(ev.CalendarID)+"/events"),
		Headers: map[string]string{"Authorization": auth, "Content-Type": "application/json"},
		Body:    payload,
	})
	if err != nil {
		return CalendarEvent{}, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return CalendarEvent{}, statusError("Google Calendar create", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return CalendarEvent{}, err
	}
	id, _ := tjString(root, "id")
	created := ev
	created.EventID = id
	return created, nil
}

// DeleteEvent ports DeleteEventAsync: DELETE calendars/{id}/events/{eventId};
// treats 204/410 as success.
func (c *GoogleCalendarConnector) DeleteEvent(ctx context.Context, calendarID, eventID string) error {
	if !stringsTrimSpaceNonEmpty(calendarID) {
		return errors.New("calendarId required")
	}
	if !stringsTrimSpaceNonEmpty(eventID) {
		return errors.New("eventId required")
	}
	auth, err := c.ensureAuth(ctx)
	if err != nil {
		return err
	}
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "DELETE",
		URL:     joinBaseAndPath(c.base, "calendars/"+escapeDataString(calendarID)+"/events/"+escapeDataString(eventID)),
		Headers: map[string]string{"Authorization": auth},
	})
	if err != nil {
		return err
	}
	if resp.StatusCode == 204 || resp.StatusCode == 410 {
		return nil
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return statusError("Google Calendar delete", resp.StatusCode)
	}
	return nil
}

func googleAttendees(emails []string) []interface{} {
	out := make([]interface{}, 0, len(emails))
	for _, a := range emails {
		out = append(out, map[string]interface{}{"email": a})
	}
	return out
}

// googleParseTime extracts (utc, isAllDay) from a start/end node. Ports ParseTime:
// a "dateTime" string → parsed UTC; a "date" string → midnight UTC + allDay=true.
func googleParseTime(parent map[string]interface{}, property string) (time.Time, bool) {
	node, ok := tjObject(parent, property)
	if !ok {
		return time.Time{}, false
	}
	if dt, ok := tjString(node, "dateTime"); ok {
		return parseDateTimeOffsetUTC(dt), false
	}
	if d, ok := tjString(node, "date"); ok {
		if t := parseCaldavDate(d); !t.IsZero() {
			return t, true
		}
		return time.Time{}, true
	}
	return time.Time{}, false
}

// parseCaldavDate parses "yyyy-MM-dd" as midnight UTC (Google all-day date).
func parseCaldavDate(s string) time.Time {
	if t, err := time.ParseInLocation(dateOnlyLayout, strings.TrimSpace(s), time.UTC); err == nil {
		return t.UTC()
	}
	return time.Time{}
}

// ── Microsoft Graph Calendar ────────────────────────────────────────────────

// msGraphBaseURI is the Microsoft Graph v1.0 base (shared by calendar + mail).
const msGraphBaseURI = "https://graph.microsoft.com/v1.0/"

// MsGraphCalendarOptions configures the MS Graph calendar connector. Ports
// MsGraphCalendarOptions.
type MsGraphCalendarOptions struct {
	AccessTokenProvider AccessTokenProvider
	CalendarID          string
}

// MsGraphCalendarConnector is a Microsoft Graph 1.0 calendar client over the
// injected CarrierHTTP. Ports MsGraphCalendarConnector.
type MsGraphCalendarConnector struct {
	http CarrierHTTP
	opts MsGraphCalendarOptions
	base string
}

// NewMsGraphCalendarConnector constructs the connector. http is required; an empty
// CalendarID defaults to "primary".
func NewMsGraphCalendarConnector(http CarrierHTTP, opts MsGraphCalendarOptions) (*MsGraphCalendarConnector, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	if opts.CalendarID == "" {
		opts.CalendarID = "primary"
	}
	return &MsGraphCalendarConnector{http: http, opts: opts, base: msGraphBaseURI}, nil
}

// ProviderID is "ms-graph-calendar".
func (c *MsGraphCalendarConnector) ProviderID() string { return "ms-graph-calendar" }

// IsConfigured is true when an AccessTokenProvider is set.
func (c *MsGraphCalendarConnector) IsConfigured() bool { return c.opts.AccessTokenProvider != nil }

func (c *MsGraphCalendarConnector) ensureAuth(ctx context.Context) (string, error) {
	if c.opts.AccessTokenProvider == nil {
		return "", errors.New("Microsoft Graph access token unavailable; refresh OAuth.")
	}
	token, err := c.opts.AccessTokenProvider(ctx)
	if err != nil {
		return "", err
	}
	if !stringsTrimSpaceNonEmpty(token) {
		return "", errors.New("Microsoft Graph access token unavailable; refresh OAuth.")
	}
	return "Bearer " + token, nil
}

// ListEvents ports ListEventsAsync: GET me/calendar/calendarView with
// startDateTime/endDateTime/$top/$orderby.
func (c *MsGraphCalendarConnector) ListEvents(ctx context.Context, fromUtc, toUtc time.Time) ([]CalendarEvent, error) {
	auth, err := c.ensureAuth(ctx)
	if err != nil {
		return nil, err
	}
	path := "me/calendar/calendarView" +
		"?startDateTime=" + escapeDataString(isoRoundTrip(fromUtc)) +
		"&endDateTime=" + escapeDataString(isoRoundTrip(toUtc)) +
		"&$top=250&$orderby=start/dateTime"
	resp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, path), Headers: map[string]string{"Authorization": auth}})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("MS Graph calendarView", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return nil, err
	}
	list := []CalendarEvent{}
	arr, ok := tjArray(root, "value")
	if !ok {
		return list, nil
	}
	for _, it := range arr {
		ev, ok := asJSONObject(it)
		if !ok {
			continue
		}
		attendees := []string{}
		if atts, ok := tjArray(ev, "attendees"); ok {
			for _, a := range atts {
				if am, ok := asJSONObject(a); ok {
					if ea, ok := tjObject(am, "emailAddress"); ok {
						if addr, ok := tjString(ea, "address"); ok {
							attendees = append(attendees, addr)
						}
					}
				}
			}
		}
		startUtc := msGraphParseTime(ev, "start")
		endUtc := msGraphParseTime(ev, "end")
		allDay, _ := tjBool(ev, "isAllDay")
		id, _ := tjString(ev, "id")
		title, _ := tjString(ev, "subject")
		ce := CalendarEvent{
			EventID:    id,
			CalendarID: c.opts.CalendarID,
			Title:      title,
			StartUtc:   startUtc,
			EndUtc:     endUtc,
			IsAllDay:   allDay,
			Attendees:  attendees,
		}
		if d, ok := tjString(ev, "bodyPreview"); ok {
			ce.Description = strPtr(d)
		}
		if loc, ok := tjObject(ev, "location"); ok {
			if dn, ok := tjString(loc, "displayName"); ok {
				ce.Location = strPtr(dn)
			}
		}
		list = append(list, ce)
	}
	return list, nil
}

// CreateEvent ports CreateEventAsync: POST me/events with the graph event body.
func (c *MsGraphCalendarConnector) CreateEvent(ctx context.Context, ev CalendarEvent) (CalendarEvent, error) {
	auth, err := c.ensureAuth(ctx)
	if err != nil {
		return CalendarEvent{}, err
	}
	body := map[string]interface{}{
		"subject":   ev.Title,
		"body":      map[string]interface{}{"contentType": "text", "content": derefOr(ev.Description, "")},
		"start":     map[string]interface{}{"dateTime": isoRoundTrip(ev.StartUtc), "timeZone": "UTC"},
		"end":       map[string]interface{}{"dateTime": isoRoundTrip(ev.EndUtc), "timeZone": "UTC"},
		"isAllDay":  ev.IsAllDay,
		"location":  map[string]interface{}{"displayName": derefOr(ev.Location, "")},
		"attendees": msGraphAttendees(ev.Attendees),
	}
	payload, _ := json.Marshal(body)
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, "me/events"),
		Headers: map[string]string{"Authorization": auth, "Content-Type": "application/json"},
		Body:    payload,
	})
	if err != nil {
		return CalendarEvent{}, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return CalendarEvent{}, statusError("MS Graph create", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return CalendarEvent{}, err
	}
	id, _ := tjString(root, "id")
	created := ev
	created.EventID = id
	return created, nil
}

// DeleteEvent ports DeleteEventAsync: DELETE me/events/{eventId}; 204 is success.
func (c *MsGraphCalendarConnector) DeleteEvent(ctx context.Context, _ string, eventID string) error {
	if !stringsTrimSpaceNonEmpty(eventID) {
		return errors.New("eventId required")
	}
	auth, err := c.ensureAuth(ctx)
	if err != nil {
		return err
	}
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "DELETE",
		URL:     joinBaseAndPath(c.base, "me/events/"+escapeDataString(eventID)),
		Headers: map[string]string{"Authorization": auth},
	})
	if err != nil {
		return err
	}
	if resp.StatusCode == 204 {
		return nil
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return statusError("MS Graph delete", resp.StatusCode)
	}
	return nil
}

func msGraphAttendees(emails []string) []interface{} {
	out := make([]interface{}, 0, len(emails))
	for _, a := range emails {
		out = append(out, map[string]interface{}{
			"emailAddress": map[string]interface{}{"address": a},
			"type":         "required",
		})
	}
	return out
}

// msGraphParseTime extracts a UTC instant from a start/end node's "dateTime".
// Ports ParseGraphTime.
func msGraphParseTime(parent map[string]interface{}, property string) time.Time {
	node, ok := tjObject(parent, property)
	if !ok {
		return time.Time{}
	}
	dt, ok := tjString(node, "dateTime")
	if !ok || dt == "" {
		return time.Time{}
	}
	return parseDateTimeOffsetUTC(dt)
}

var (
	_ ICalendarConnector = (*CalDavCalendarConnector)(nil)
	_ ICalendarConnector = (*GoogleCalendarConnector)(nil)
	_ ICalendarConnector = (*MsGraphCalendarConnector)(nil)
)
