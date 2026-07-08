// proactive_briefing_test.go
//
// Verifies ProactiveBriefingService (ported from ProactiveBriefingService.cs):
// TimeUntilNextFire scheduling math, and FireOnce assembly → summarise →
// deliver (with connector fakes). Behavioural — the C# service is IHostedService;
// FireOnce is the public unit the C# also exposes.

package circleai_test

import (
	"context"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── connector fakes ──────────────────────────────────────────────────────────

type fakeCalendar struct {
	id         string
	configured bool
	events     []circleai.BriefingCalendarEvent
}

func (c fakeCalendar) ProviderID() string { return c.id }
func (c fakeCalendar) IsConfigured() bool { return c.configured }
func (c fakeCalendar) ListEvents(context.Context, time.Time, time.Time) ([]circleai.BriefingCalendarEvent, error) {
	return c.events, nil
}

type fakeEmail struct {
	id         string
	configured bool
	unread     []circleai.BriefingEmailMessage
}

func (e fakeEmail) ProviderID() string { return e.id }
func (e fakeEmail) IsConfigured() bool { return e.configured }
func (e fakeEmail) ListUnread(context.Context, int) ([]circleai.BriefingEmailMessage, error) {
	return e.unread, nil
}

type fakeNews struct {
	id         string
	configured bool
	items      []circleai.BriefingNewsItem
}

func (n fakeNews) SourceID() string   { return n.id }
func (n fakeNews) IsConfigured() bool { return n.configured }
func (n fakeNews) FetchLatest(context.Context, int) ([]circleai.BriefingNewsItem, error) {
	return n.items, nil
}

type fakeWeather struct {
	id     string
	sample circleai.BriefingWeatherSample
}

func (w fakeWeather) ProviderID() string { return w.id }
func (w fakeWeather) Current(context.Context, float64, float64) (circleai.BriefingWeatherSample, error) {
	return w.sample, nil
}

type captureNotifier struct {
	headline string
	body     string
	address  *string
	called   int
}

func (c *captureNotifier) Deliver(_ context.Context, headline, body string, address *string) error {
	c.headline = headline
	c.body = body
	c.address = address
	c.called++
	return nil
}

// ── TimeUntilNextFire ────────────────────────────────────────────────────────

func TestProactiveBriefing_TimeUntilNextFire(t *testing.T) {
	lat, lon := 0.0, 0.0
	svc := circleai.NewProactiveBriefingService(circleai.ProactiveBriefingOptions{
		FireTimesUTC: []time.Duration{6*time.Hour + 30*time.Minute, 18 * time.Hour},
		Latitude:     &lat,
		Longitude:    &lon,
	}, circleai.ProactiveBriefingDeps{})

	// 05:00 → next fire 06:30 (1h30m away).
	now := time.Date(2026, 7, 8, 5, 0, 0, 0, time.UTC)
	if got := svc.TimeUntilNextFire(now); got != 90*time.Minute {
		t.Errorf("05:00 → next: got %v want 1h30m", got)
	}
	// 07:00 → next fire 18:00 (11h away).
	now = time.Date(2026, 7, 8, 7, 0, 0, 0, time.UTC)
	if got := svc.TimeUntilNextFire(now); got != 11*time.Hour {
		t.Errorf("07:00 → next: got %v want 11h", got)
	}
	// 18:30 → both today are past; next is 06:30 tomorrow (12h away).
	now = time.Date(2026, 7, 8, 18, 30, 0, 0, time.UTC)
	if got := svc.TimeUntilNextFire(now); got != 12*time.Hour {
		t.Errorf("18:30 → next: got %v want 12h", got)
	}

	// A moment within 30s before a fire rolls to the next day (no double-fire).
	now = time.Date(2026, 7, 8, 6, 29, 45, 0, time.UTC) // 15s before 06:30
	got := svc.TimeUntilNextFire(now)
	if got < 11*time.Hour {
		t.Errorf("within 30s guard should skip to 18:00, got %v", got)
	}
}

// ── FireOnce ─────────────────────────────────────────────────────────────────

func TestProactiveBriefing_FireOnce_AssemblesAndDelivers(t *testing.T) {
	ctx := context.Background()
	addr := "+27820000000"
	lat, lon := -29.85, 31.02
	notifier := &captureNotifier{}

	svc := circleai.NewProactiveBriefingService(circleai.ProactiveBriefingOptions{
		Headline:        "Morning",
		DeliveryAddress: &addr,
		Latitude:        &lat,
		Longitude:       &lon,
	}, circleai.ProactiveBriefingDeps{
		Calendars: []circleai.BriefingCalendarConnector{
			fakeCalendar{id: "gcal", configured: true, events: []circleai.BriefingCalendarEvent{
				{Title: "Standup", StartUTC: time.Date(2026, 7, 8, 9, 0, 0, 0, time.UTC)},
			}},
			fakeCalendar{id: "unconfigured", configured: false},
		},
		Emails: []circleai.BriefingEmailConnector{
			fakeEmail{id: "gmail", configured: true, unread: []circleai.BriefingEmailMessage{
				{From: "boss@x.com", Subject: "Roadmap"},
			}},
		},
		News: []circleai.BriefingNewsSource{
			fakeNews{id: "hn", configured: true, items: []circleai.BriefingNewsItem{{Title: "Go 2 announced"}}},
		},
		Weather: fakeWeather{id: "owm", sample: circleai.BriefingWeatherSample{
			TempC: 21, FeelsLikeC: 20, WindKph: 12, Condition: "Clear",
		}},
		Notifiers: []circleai.IBriefingNotifier{notifier},
		// Summariser echoes a marker + the raw context so we can assert assembly.
		Summariser: func(_ context.Context, prompt string) (string, error) {
			return "SUMMARY::" + prompt, nil
		},
	})

	if err := svc.FireOnce(ctx); err != nil {
		t.Fatalf("FireOnce: %v", err)
	}
	if notifier.called != 1 {
		t.Fatalf("notifier called %d times, want 1", notifier.called)
	}
	if notifier.headline != "Morning" {
		t.Errorf("headline: got %q", notifier.headline)
	}
	if notifier.address == nil || *notifier.address != addr {
		t.Errorf("address: got %v", notifier.address)
	}
	body := notifier.body
	for _, want := range []string{"SUMMARY::", "### Calendar (gcal)", "Standup", "### Unread email (gmail)", "boss@x.com", "### News (hn)", "Go 2 announced", "### Weather (owm)", "Clear"} {
		if !strings.Contains(body, want) {
			t.Errorf("briefing body missing %q:\n%s", want, body)
		}
	}
	// The unconfigured calendar must not appear.
	if strings.Contains(body, "unconfigured") {
		t.Errorf("unconfigured connector leaked into body:\n%s", body)
	}
}

func TestProactiveBriefing_FireOnce_NoSignalsNoDelivery(t *testing.T) {
	ctx := context.Background()
	notifier := &captureNotifier{}
	svc := circleai.NewProactiveBriefingService(circleai.ProactiveBriefingOptions{},
		circleai.ProactiveBriefingDeps{
			// All connectors unconfigured / empty.
			Calendars: []circleai.BriefingCalendarConnector{fakeCalendar{id: "c", configured: false}},
			Notifiers: []circleai.IBriefingNotifier{notifier},
		})
	if err := svc.FireOnce(ctx); err != nil {
		t.Fatalf("FireOnce: %v", err)
	}
	if notifier.called != 0 {
		t.Errorf("no signals should mean no delivery, got %d calls", notifier.called)
	}
}

func TestProactiveBriefing_NoSummariserDeliversRawContext(t *testing.T) {
	ctx := context.Background()
	notifier := &captureNotifier{}
	svc := circleai.NewProactiveBriefingService(circleai.ProactiveBriefingOptions{},
		circleai.ProactiveBriefingDeps{
			News: []circleai.BriefingNewsSource{
				fakeNews{id: "rss", configured: true, items: []circleai.BriefingNewsItem{{Title: "Headline A"}}},
			},
			Notifiers: []circleai.IBriefingNotifier{notifier},
			// No summariser → raw context delivered.
		})
	if err := svc.FireOnce(ctx); err != nil {
		t.Fatalf("FireOnce: %v", err)
	}
	if notifier.called != 1 {
		t.Fatalf("expected 1 delivery, got %d", notifier.called)
	}
	if !strings.Contains(notifier.body, "### News (rss)") || !strings.Contains(notifier.body, "Headline A") {
		t.Errorf("raw-context body: %q", notifier.body)
	}
	if strings.HasPrefix(notifier.body, "SUMMARY::") {
		t.Errorf("no summariser should mean no summary prefix: %q", notifier.body)
	}
}

func TestProactiveBriefing_StartStop(t *testing.T) {
	ctx := context.Background()
	svc := circleai.NewProactiveBriefingService(circleai.ProactiveBriefingOptions{
		// A far-future fire time so the loop just sleeps.
		FireTimesUTC: []time.Duration{23 * time.Hour},
	}, circleai.ProactiveBriefingDeps{})
	if err := svc.Start(ctx); err != nil {
		t.Fatalf("Start: %v", err)
	}
	// Start is idempotent.
	if err := svc.Start(ctx); err != nil {
		t.Fatalf("second Start: %v", err)
	}
	if err := svc.Stop(ctx); err != nil {
		t.Fatalf("Stop: %v", err)
	}
	if err := svc.Stop(ctx); err != nil {
		t.Fatalf("second Stop: %v", err)
	}
}
