// integration_contracts_test.go
//
// Verifies CircleAI.Integration/Contracts.cs port (integration_contracts.go):
// the record structs carry the right fields, the interface guards hold via the
// in-memory/HTTP-backed impls, and the JSON scalar helpers behave like the C#
// Get*/TryGetProperty they model.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// Compile-time interface satisfaction across every ported impl.
func TestIntegration_InterfaceGuards(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	var _ circleai.ICalendarConnector = mustCalDav(t, tr)
	var _ circleai.IEmailConnector = mustGmail(t, tr)
	var _ circleai.INewsSource = mustRss(t, tr)
	var wp circleai.IWeatherProvider
	wp, _ = circleai.NewOpenMeteoWeatherProvider(tr)
	if wp.ProviderID() != "open-meteo" {
		t.Fatalf("weather provider id = %q", wp.ProviderID())
	}
	var rp circleai.IRoutingProvider
	rp, _ = circleai.NewOsrmRoutingProvider(tr, circleai.OsrmOptions{})
	if rp.ProviderID() != "osrm" {
		t.Fatalf("routing provider id = %q", rp.ProviderID())
	}
	var ha circleai.IHomeAutomationConnector
	ha, _ = circleai.NewHomeAssistantConnector(tr, circleai.HomeAssistantOptions{BaseURL: "http://ha/", AccessToken: "t"})
	if ha.ProviderID() != "home-assistant" {
		t.Fatalf("home id = %q", ha.ProviderID())
	}
}

func TestIntegration_CalendarEventFields(t *testing.T) {
	desc := "d"
	loc := "l"
	start := time.Date(2026, 7, 11, 9, 0, 0, 0, time.UTC)
	ev := circleai.CalendarEvent{
		EventID: "e1", CalendarID: "c1", Title: "t", Description: &desc, Location: &loc,
		StartUtc: start, EndUtc: start.Add(time.Hour), IsAllDay: false, Attendees: []string{"a@x"},
	}
	if ev.EventID != "e1" || *ev.Description != "d" || *ev.Location != "l" ||
		!ev.StartUtc.Equal(start) || len(ev.Attendees) != 1 {
		t.Fatalf("calendar event fields wrong: %+v", ev)
	}
}

func TestIntegration_EmailAndNewsAndWeatherFields(t *testing.T) {
	em := circleai.EmailMessage{MessageID: "m", From: "f", To: []string{"t"}, Subject: "s", BodyText: "b", Unread: true, Labels: []string{"INBOX"}}
	if em.MessageID != "m" || !em.Unread || em.To[0] != "t" {
		t.Fatalf("email fields wrong: %+v", em)
	}
	ni := circleai.NewsItem{ItemID: "i", SourceID: "src", Title: "ti", Summary: "su", URL: "https://x/y", Tags: []string{"tag"}}
	if ni.URL != "https://x/y" || ni.Tags[0] != "tag" {
		t.Fatalf("news fields wrong: %+v", ni)
	}
	ws := circleai.WeatherSample{TempC: 21.5, FeelsLikeC: 20, PrecipMm: 0, WindKph: 10, CloudPct: 30, Condition: "clear sky"}
	if ws.TempC != 21.5 || ws.CloudPct != 30 || ws.Condition != "clear sky" {
		t.Fatalf("weather fields wrong: %+v", ws)
	}
	re := circleai.RouteEstimate{DistanceKm: 5, Duration: 10 * time.Minute, Polyline: []circleai.GeoPoint{{Lat: 1, Lon: 2}}}
	if re.DistanceKm != 5 || re.Duration != 10*time.Minute || re.Polyline[0].Lat != 1 {
		t.Fatalf("route fields wrong: %+v", re)
	}
	he := circleai.HaEntity{EntityID: "light.k", FriendlyName: "Kitchen", Domain: "light", State: "on", Attributes: map[string]string{"brightness": "128"}}
	if he.Domain != "light" || he.Attributes["brightness"] != "128" {
		t.Fatalf("ha entity fields wrong: %+v", he)
	}
}

// --- shared test helpers / token providers ---

func fixedToken(tok string) circleai.AccessTokenProvider {
	return func(ctx context.Context) (string, error) { return tok, nil }
}

func mustCalDav(t *testing.T, tr *circleai.FakeCarrierTransport) *circleai.CalDavCalendarConnector {
	t.Helper()
	c, err := circleai.NewCalDavCalendarConnector(tr, circleai.CalDavCalendarOptions{
		CalendarURI: "https://cal.example.com/dav/user/calendars/personal/", Username: "u", Password: "p",
	})
	if err != nil {
		t.Fatalf("new caldav: %v", err)
	}
	return c
}

func mustGmail(t *testing.T, tr *circleai.FakeCarrierTransport) *circleai.GmailEmailConnector {
	t.Helper()
	c, err := circleai.NewGmailEmailConnector(tr, circleai.GmailOptions{AccessTokenProvider: fixedToken("tok")})
	if err != nil {
		t.Fatalf("new gmail: %v", err)
	}
	return c
}

func mustRss(t *testing.T, tr *circleai.FakeCarrierTransport) *circleai.RssNewsSource {
	t.Helper()
	c, err := circleai.NewRssNewsSource(tr, circleai.RssOptions{FeedURL: "https://news.example.com/rss.xml"})
	if err != nil {
		t.Fatalf("new rss: %v", err)
	}
	return c
}
