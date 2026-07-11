// integration_calendar_test.go
//
// Verifies the CircleAI.Integration.Calendar port (integration_calendar.go) over
// the injected FakeCarrierTransport — no real network. Covers CalDAV (REPORT +
// ICS parse, PUT create, DELETE status handling), Google (v3 events list/create/
// delete + cancelled skip + all-day), and MS Graph (calendarView list + create +
// delete), plus auth-failure and config gating.

package circleai_test

import (
	"context"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ---------------------------------------------------------------------------
// CalDAV
// ---------------------------------------------------------------------------

func TestCalDav_ListEventsParsesICS(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	multistatus := `<?xml version="1.0"?><D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
	<D:response><C:calendar-data>BEGIN:VCALENDAR
BEGIN:VEVENT
UID:evt-1
SUMMARY:Standup
DESCRIPTION:Daily sync
LOCATION:Room 5
DTSTART:20260711T090000Z
DTEND:20260711T093000Z
END:VEVENT
END:VCALENDAR</C:calendar-data></D:response></D:multistatus>`
	tr.EnqueueJSON(207, multistatus) // 207 Multi-Status body; EnqueueJSON just sets status+body

	c := mustCalDav(t, tr)
	if c.ProviderID() != "caldav" || !c.IsConfigured() {
		t.Fatalf("caldav id/configured wrong")
	}
	from := time.Date(2026, 7, 11, 0, 0, 0, 0, time.UTC)
	to := from.Add(24 * time.Hour)
	evs, err := c.ListEvents(context.Background(), from, to)
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(evs) != 1 {
		t.Fatalf("expected 1 event, got %d", len(evs))
	}
	e := evs[0]
	if e.EventID != "evt-1" || e.Title != "Standup" || e.Description == nil || *e.Description != "Daily sync" ||
		e.Location == nil || *e.Location != "Room 5" {
		t.Fatalf("parsed event wrong: %+v", e)
	}
	if !e.StartUtc.Equal(time.Date(2026, 7, 11, 9, 0, 0, 0, time.UTC)) ||
		!e.EndUtc.Equal(time.Date(2026, 7, 11, 9, 30, 0, 0, time.UTC)) {
		t.Fatalf("event times wrong: %v..%v", e.StartUtc, e.EndUtc)
	}
	if e.IsAllDay {
		t.Fatalf("timed event flagged all-day")
	}
	// Request wire: REPORT, Depth:1, Basic auth, time-range with stamps.
	req, _ := tr.LastRequest()
	if req.Method != "REPORT" || req.Headers["Depth"] != "1" || !strings.HasPrefix(req.Headers["Authorization"], "Basic ") {
		t.Fatalf("report request wrong: %+v", req)
	}
	if !strings.Contains(string(req.Body), `start="20260711T000000Z"`) || !strings.Contains(string(req.Body), `end="20260712T000000Z"`) {
		t.Fatalf("time-range body wrong: %s", req.Body)
	}
}

func TestCalDav_AllDayDetection(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	body := `<multistatus xmlns="DAV:"><response><cal:calendar-data xmlns:cal="urn:ietf:params:xml:ns:caldav">BEGIN:VCALENDAR
BEGIN:VEVENT
UID:allday
SUMMARY:Holiday
DTSTART;VALUE=DATE:20260711
DTEND;VALUE=DATE:20260712
END:VEVENT
END:VCALENDAR</cal:calendar-data></response></multistatus>`
	tr.EnqueueJSON(207, body)
	c := mustCalDav(t, tr)
	evs, err := c.ListEvents(context.Background(), time.Now().UTC(), time.Now().UTC().Add(48*time.Hour))
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(evs) != 1 || !evs[0].IsAllDay {
		t.Fatalf("all-day not detected: %+v", evs)
	}
}

func TestCalDav_CreateEventPutsICS(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueStatus(201)
	c := mustCalDav(t, tr)
	desc := "notes"
	start := time.Date(2026, 7, 12, 14, 0, 0, 0, time.UTC)
	created, err := c.CreateEvent(context.Background(), circleai.CalendarEvent{
		EventID: "my-uid", CalendarID: "cal", Title: "Lunch, meeting", Description: &desc,
		StartUtc: start, EndUtc: start.Add(time.Hour),
	})
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if created.EventID != "my-uid" {
		t.Fatalf("created id = %q", created.EventID)
	}
	req, _ := tr.LastRequest()
	if req.Method != "PUT" || req.Headers["If-None-Match"] != "*" ||
		!strings.HasSuffix(req.URL, "/personal/my-uid.ics") {
		t.Fatalf("PUT request wrong: %s %s hdr=%v", req.Method, req.URL, req.Headers)
	}
	ics := string(req.Body)
	// SUMMARY comma is ICS-escaped; UID + DTSTART present.
	if !strings.Contains(ics, `SUMMARY:Lunch\, meeting`) || !strings.Contains(ics, "UID:my-uid") ||
		!strings.Contains(ics, "DTSTART:20260712T140000Z") || !strings.Contains(ics, "DESCRIPTION:notes") {
		t.Fatalf("ICS body wrong:\n%s", ics)
	}
}

func TestCalDav_CreateEventGeneratesUID(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueStatus(201)
	c := mustCalDav(t, tr)
	created, err := c.CreateEvent(context.Background(), circleai.CalendarEvent{
		CalendarID: "cal", Title: "No UID", StartUtc: time.Now().UTC(), EndUtc: time.Now().UTC().Add(time.Hour),
	})
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if len(created.EventID) != 32 { // Guid "N" = 32 hex chars
		t.Fatalf("generated uid wrong: %q (len %d)", created.EventID, len(created.EventID))
	}
}

func TestCalDav_DeleteStatusHandling(t *testing.T) {
	for _, code := range []int{204, 200, 404} {
		tr := circleai.NewFakeCarrierTransport()
		tr.EnqueueStatus(code)
		c := mustCalDav(t, tr)
		if err := c.DeleteEvent(context.Background(), "cal", "evt-1"); err != nil {
			t.Fatalf("delete status %d should succeed: %v", code, err)
		}
		req, _ := tr.LastRequest()
		if req.Method != "DELETE" || !strings.HasSuffix(req.URL, "evt-1.ics") {
			t.Fatalf("delete request wrong: %s %s", req.Method, req.URL)
		}
	}
	// 500 -> error.
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueStatus(500)
	c := mustCalDav(t, tr)
	if err := c.DeleteEvent(context.Background(), "cal", "evt-1"); err == nil {
		t.Fatalf("delete 500 should error")
	}
	// blank eventId -> error, no request.
	if err := c.DeleteEvent(context.Background(), "cal", "  "); err == nil {
		t.Fatalf("blank eventId should error")
	}
}

func TestCalDav_Unconfigured(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	c, _ := circleai.NewCalDavCalendarConnector(tr, circleai.CalDavCalendarOptions{CalendarURI: "https://x/dav/", Username: "", Password: ""})
	if c.IsConfigured() {
		t.Fatalf("blank creds should be unconfigured")
	}
}

// ---------------------------------------------------------------------------
// Google Calendar
// ---------------------------------------------------------------------------

func mustGoogleCal(t *testing.T, tr *circleai.FakeCarrierTransport, tok string) *circleai.GoogleCalendarConnector {
	t.Helper()
	c, err := circleai.NewGoogleCalendarConnector(tr, circleai.GoogleCalendarOptions{AccessTokenProvider: fixedToken(tok)})
	if err != nil {
		t.Fatalf("new google cal: %v", err)
	}
	return c
}

func TestGoogleCal_ListEventsSkipsCancelledAndParsesAllDay(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"items":[
		{"id":"a","status":"confirmed","summary":"Timed","description":"d","location":"loc",
		 "start":{"dateTime":"2026-07-11T09:00:00Z"},"end":{"dateTime":"2026-07-11T10:00:00Z"},
		 "attendees":[{"email":"x@y"},{"email":"z@w"}]},
		{"id":"b","status":"cancelled","summary":"Gone"},
		{"id":"c","summary":"Holiday","start":{"date":"2026-07-12"},"end":{"date":"2026-07-13"}}
	]}`)
	c := mustGoogleCal(t, tr, "tok")
	if c.ProviderID() != "google-calendar" || !c.IsConfigured() {
		t.Fatalf("google id/configured wrong")
	}
	evs, err := c.ListEvents(context.Background(), time.Date(2026, 7, 11, 0, 0, 0, 0, time.UTC), time.Date(2026, 7, 14, 0, 0, 0, 0, time.UTC))
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(evs) != 2 {
		t.Fatalf("expected 2 (cancelled skipped), got %d: %+v", len(evs), evs)
	}
	if evs[0].EventID != "a" || evs[0].Title != "Timed" || len(evs[0].Attendees) != 2 || evs[0].IsAllDay {
		t.Fatalf("timed event wrong: %+v", evs[0])
	}
	if evs[1].EventID != "c" || !evs[1].IsAllDay ||
		!evs[1].StartUtc.Equal(time.Date(2026, 7, 12, 0, 0, 0, 0, time.UTC)) {
		t.Fatalf("all-day event wrong: %+v", evs[1])
	}
	// URL params carry the escaped calendar id + ISO time bounds.
	req := tr.Requests()[0]
	if req.Method != "GET" || !strings.Contains(req.URL, "calendars/primary/events") ||
		!strings.Contains(req.URL, "singleEvents=true") || !strings.HasPrefix(req.Headers["Authorization"], "Bearer ") {
		t.Fatalf("list request wrong: %s %s", req.Method, req.URL)
	}
}

func TestGoogleCal_CreateEventBodyAndId(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"id":"srv-99"}`)
	c := mustGoogleCal(t, tr, "tok")
	start := time.Date(2026, 7, 11, 9, 0, 0, 0, time.UTC)
	created, err := c.CreateEvent(context.Background(), circleai.CalendarEvent{
		CalendarID: "primary", Title: "Sync", StartUtc: start, EndUtc: start.Add(time.Hour), Attendees: []string{"a@b"},
	})
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if created.EventID != "srv-99" {
		t.Fatalf("created id = %q", created.EventID)
	}
	req, _ := tr.LastRequest()
	if req.Method != "POST" || !strings.Contains(req.URL, "calendars/primary/events") {
		t.Fatalf("create request wrong: %s %s", req.Method, req.URL)
	}
	body := string(req.Body)
	if !strings.Contains(body, `"summary":"Sync"`) || !strings.Contains(body, `"dateTime":"2026-07-11T09:00:00.0000000Z"`) ||
		!strings.Contains(body, `"email":"a@b"`) {
		t.Fatalf("create body wrong: %s", body)
	}
}

func TestGoogleCal_DeleteAndAuthFailure(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueStatus(204)
	c := mustGoogleCal(t, tr, "tok")
	if err := c.DeleteEvent(context.Background(), "primary", "e1"); err != nil {
		t.Fatalf("delete 204: %v", err)
	}
	tr2 := circleai.NewFakeCarrierTransport()
	tr2.EnqueueStatus(410)
	c2 := mustGoogleCal(t, tr2, "tok")
	if err := c2.DeleteEvent(context.Background(), "primary", "e1"); err != nil {
		t.Fatalf("delete 410 (Gone) should succeed: %v", err)
	}
	// Blank token -> auth error, no request issued.
	tr3 := circleai.NewFakeCarrierTransport()
	c3 := mustGoogleCal(t, tr3, "")
	if _, err := c3.ListEvents(context.Background(), time.Now().UTC(), time.Now().UTC().Add(time.Hour)); err == nil {
		t.Fatalf("blank token should error")
	}
	if len(tr3.Requests()) != 0 {
		t.Fatalf("auth failure should not issue a request")
	}
}

// ---------------------------------------------------------------------------
// Microsoft Graph Calendar
// ---------------------------------------------------------------------------

func mustGraphCal(t *testing.T, tr *circleai.FakeCarrierTransport, tok string) *circleai.MsGraphCalendarConnector {
	t.Helper()
	c, err := circleai.NewMsGraphCalendarConnector(tr, circleai.MsGraphCalendarOptions{AccessTokenProvider: fixedToken(tok)})
	if err != nil {
		t.Fatalf("new graph cal: %v", err)
	}
	return c
}

func TestGraphCal_ListEvents(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"value":[
		{"id":"g1","subject":"Review","bodyPreview":"prep","isAllDay":false,
		 "location":{"displayName":"HQ"},
		 "start":{"dateTime":"2026-07-11T13:00:00.0000000","timeZone":"UTC"},
		 "end":{"dateTime":"2026-07-11T14:00:00.0000000","timeZone":"UTC"},
		 "attendees":[{"emailAddress":{"address":"p@q"}}]}
	]}`)
	c := mustGraphCal(t, tr, "tok")
	if c.ProviderID() != "ms-graph-calendar" {
		t.Fatalf("graph id wrong")
	}
	evs, err := c.ListEvents(context.Background(), time.Date(2026, 7, 11, 0, 0, 0, 0, time.UTC), time.Date(2026, 7, 12, 0, 0, 0, 0, time.UTC))
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(evs) != 1 {
		t.Fatalf("expected 1, got %d", len(evs))
	}
	e := evs[0]
	if e.EventID != "g1" || e.Title != "Review" || e.Description == nil || *e.Description != "prep" ||
		e.Location == nil || *e.Location != "HQ" || len(e.Attendees) != 1 || e.Attendees[0] != "p@q" {
		t.Fatalf("graph event wrong: %+v (loc=%v)", e, e.Location)
	}
	if !e.StartUtc.Equal(time.Date(2026, 7, 11, 13, 0, 0, 0, time.UTC)) {
		t.Fatalf("graph start wrong: %v", e.StartUtc)
	}
	req := tr.Requests()[0]
	if !strings.Contains(req.URL, "me/calendar/calendarView") || !strings.Contains(req.URL, "$orderby=start/dateTime") {
		t.Fatalf("graph list url wrong: %s", req.URL)
	}
}

func TestGraphCal_CreateAndDelete(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(201, `{"id":"new-1"}`)
	c := mustGraphCal(t, tr, "tok")
	start := time.Date(2026, 7, 11, 9, 0, 0, 0, time.UTC)
	created, err := c.CreateEvent(context.Background(), circleai.CalendarEvent{CalendarID: "primary", Title: "T", StartUtc: start, EndUtc: start.Add(time.Hour)})
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if created.EventID != "new-1" {
		t.Fatalf("created id = %q", created.EventID)
	}
	req, _ := tr.LastRequest()
	if req.Method != "POST" || !strings.HasSuffix(req.URL, "me/events") || !strings.Contains(string(req.Body), `"subject":"T"`) {
		t.Fatalf("create request wrong: %s %s body=%s", req.Method, req.URL, req.Body)
	}

	tr2 := circleai.NewFakeCarrierTransport()
	tr2.EnqueueStatus(204)
	c2 := mustGraphCal(t, tr2, "tok")
	if err := c2.DeleteEvent(context.Background(), "primary", "new-1"); err != nil {
		t.Fatalf("delete: %v", err)
	}
	req2, _ := tr2.LastRequest()
	if req2.Method != "DELETE" || !strings.Contains(req2.URL, "me/events/new-1") {
		t.Fatalf("delete request wrong: %s %s", req2.Method, req2.URL)
	}
}
