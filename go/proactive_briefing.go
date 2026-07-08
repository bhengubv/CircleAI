// proactive_briefing.go
//
// Ported from CircleAI.Companion (ProactiveBriefingService.cs) — the C#
// reference. A scheduled service that assembles a "what's happening" briefing
// from registered calendar / email / news / weather connectors, runs the result
// through an LLM summariser, and pushes it through any registered notifier.
//
//   - ProactiveBriefingOptions   (config: fire-times, lat/lon, headline, address)
//   - IBriefingNotifier          (pluggable delivery)
//   - ProactiveBriefingService   (assemble → summarise → deliver + tick loop)
//
// The connector interfaces the service consumes (calendar/email/news/weather)
// are not (yet) ported to this Go tree, so the minimal read surface the service
// touches is modelled here as injected interfaces + records, mirroring
// CircleAI.Integration.Contracts. The C# IHostedService start/stop loop becomes
// Start/Stop over a goroutine. The LLM summariser is an injected dependency;
// when nil, the raw assembled context is delivered (the C# "_ai is null" path).

package circleai

import (
	"context"
	"fmt"
	"sort"
	"strings"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// Connector surface (mirrors CircleAI.Integration.Contracts — only the members
// the briefing service reads are modelled).
// ---------------------------------------------------------------------------

// BriefingCalendarEvent is a calendar event as read by the briefing.
// Mirrors the C# CalendarEvent (subset used by FireOnceAsync).
type BriefingCalendarEvent struct {
	Title    string
	Location string
	StartUTC time.Time
}

// BriefingCalendarConnector supplies calendar events for the briefing window.
// Mirrors CircleAI.Integration.ICalendarConnector (ProviderId + IsConfigured +
// ListEvents).
type BriefingCalendarConnector interface {
	ProviderID() string
	IsConfigured() bool
	ListEvents(ctx context.Context, fromUTC, toUTC time.Time) ([]BriefingCalendarEvent, error)
}

// BriefingEmailMessage is an unread email as read by the briefing.
type BriefingEmailMessage struct {
	From    string
	Subject string
}

// BriefingEmailConnector supplies unread email for the briefing.
// Mirrors CircleAI.Integration.IEmailConnector (subset).
type BriefingEmailConnector interface {
	ProviderID() string
	IsConfigured() bool
	ListUnread(ctx context.Context, max int) ([]BriefingEmailMessage, error)
}

// BriefingNewsItem is a news headline as read by the briefing.
type BriefingNewsItem struct {
	Title string
}

// BriefingNewsSource supplies latest news for the briefing.
// Mirrors CircleAI.Integration.INewsSource (subset).
type BriefingNewsSource interface {
	SourceID() string
	IsConfigured() bool
	FetchLatest(ctx context.Context, max int) ([]BriefingNewsItem, error)
}

// BriefingWeatherSample is a current-weather reading as read by the briefing.
type BriefingWeatherSample struct {
	TempC      float64
	FeelsLikeC float64
	WindKph    float64
	Condition  string
}

// BriefingWeatherProvider supplies current weather for the briefing.
// Mirrors CircleAI.Integration.IWeatherProvider (subset).
type BriefingWeatherProvider interface {
	ProviderID() string
	Current(ctx context.Context, lat, lon float64) (BriefingWeatherSample, error)
}

// BriefingSummariser turns the assembled context into a friendly summary.
// Mirrors the C# IAIService.ChatAsync call the service makes. When nil, the raw
// context is delivered.
type BriefingSummariser func(ctx context.Context, prompt string) (string, error)

// ---------------------------------------------------------------------------
// IBriefingNotifier
// ---------------------------------------------------------------------------

// IBriefingNotifier delivers a briefing. Hosts wire WhatsApp, Telegram, SMS,
// push, etc. Ported from the C# IBriefingNotifier.
type IBriefingNotifier interface {
	Deliver(ctx context.Context, headline, body string, address *string) error
}

// ---------------------------------------------------------------------------
// ProactiveBriefingOptions
// ---------------------------------------------------------------------------

// ProactiveBriefingOptions configures the ProactiveBriefingService. Ported from
// the C# ProactiveBriefingOptions.
type ProactiveBriefingOptions struct {
	// FireTimesUTC are UTC times-of-day at which to fire. Empty → default
	// 06:30 and 18:00 (applied by NewProactiveBriefingService).
	FireTimesUTC []time.Duration
	// Latitude for weather lookup. nil = skip weather.
	Latitude *float64
	// Longitude for weather lookup. nil = skip weather.
	Longitude *float64
	// Headline used by the notifier. Empty → "Your briefing".
	Headline string
	// DeliveryAddress is where to deliver (E.164 for SMS/WhatsApp, channel id
	// for Telegram, etc.). nil = notifier default.
	DeliveryAddress *string
}

// defaultFireTimes returns the C# default fire times (06:30 and 18:00 UTC).
func defaultFireTimes() []time.Duration {
	return []time.Duration{6*time.Hour + 30*time.Minute, 18 * time.Hour}
}

// ---------------------------------------------------------------------------
// ProactiveBriefingService
// ---------------------------------------------------------------------------

// ProactiveBriefingService assembles, summarises, and delivers a scheduled
// briefing. Ported from the C# ProactiveBriefingService. All connector lists may
// be empty; a nil summariser delivers the raw context.
type ProactiveBriefingService struct {
	opts       ProactiveBriefingOptions
	calendars  []BriefingCalendarConnector
	emails     []BriefingEmailConnector
	news       []BriefingNewsSource
	weather    BriefingWeatherProvider
	notifiers  []IBriefingNotifier
	summariser BriefingSummariser
	now        func() time.Time

	mu     sync.Mutex
	cancel context.CancelFunc
	done   chan struct{}
}

// ProactiveBriefingDeps bundles the optional collaborators for the briefing
// service (mirrors the C# constructor's optional parameters).
type ProactiveBriefingDeps struct {
	Calendars  []BriefingCalendarConnector
	Emails     []BriefingEmailConnector
	News       []BriefingNewsSource
	Weather    BriefingWeatherProvider
	Notifiers  []IBriefingNotifier
	Summariser BriefingSummariser
	// Now overrides the clock (tests). nil → time.Now().UTC().
	Now func() time.Time
}

// NewProactiveBriefingService builds the service. FireTimesUTC/Headline defaults
// are applied here, matching the C# option initialisers.
func NewProactiveBriefingService(opts ProactiveBriefingOptions, deps ProactiveBriefingDeps) *ProactiveBriefingService {
	if len(opts.FireTimesUTC) == 0 {
		opts.FireTimesUTC = defaultFireTimes()
	}
	if opts.Headline == "" {
		opts.Headline = "Your briefing"
	}
	now := deps.Now
	if now == nil {
		now = func() time.Time { return time.Now().UTC() }
	}
	return &ProactiveBriefingService{
		opts:       opts,
		calendars:  deps.Calendars,
		emails:     deps.Emails,
		news:       deps.News,
		weather:    deps.Weather,
		notifiers:  deps.Notifiers,
		summariser: deps.Summariser,
		now:        now,
	}
}

// Start begins the scheduled fire loop. Idempotent. The loop sleeps until the
// next fire time, fires once, and repeats until Stop or ctx cancellation.
func (s *ProactiveBriefingService) Start(ctx context.Context) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.cancel != nil {
		return nil
	}
	loopCtx, cancel := context.WithCancel(ctx)
	s.cancel = cancel
	s.done = make(chan struct{})
	go s.loop(loopCtx, s.done)
	return nil
}

// Stop halts the fire loop and waits for it to exit. Idempotent.
func (s *ProactiveBriefingService) Stop(ctx context.Context) error {
	s.mu.Lock()
	cancel := s.cancel
	done := s.done
	s.cancel = nil
	s.done = nil
	s.mu.Unlock()
	if cancel == nil {
		return nil
	}
	cancel()
	if done != nil {
		select {
		case <-done:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	return nil
}

func (s *ProactiveBriefingService) loop(ctx context.Context, done chan struct{}) {
	defer close(done)
	for {
		sleep := s.TimeUntilNextFire(s.now())
		timer := time.NewTimer(sleep)
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.C:
		}
		// A fire failure is swallowed (logged in C#); the loop continues.
		_ = s.FireOnce(ctx)
	}
}

// TimeUntilNextFire computes the time until the next configured fire moment,
// always more than 30s out to avoid double-fires. Ported from the C#
// TimeUntilNextFire.
func (s *ProactiveBriefingService) TimeUntilNextFire(now time.Time) time.Duration {
	if len(s.opts.FireTimesUTC) == 0 {
		return time.Hour
	}
	now = now.UTC()
	todayBase := time.Date(now.Year(), now.Month(), now.Day(), 0, 0, 0, 0, time.UTC)
	var best time.Duration
	haveBest := false
	for _, tod := range s.opts.FireTimesUTC {
		candidate := todayBase.Add(tod)
		if !candidate.After(now.Add(30 * time.Second)) {
			candidate = candidate.AddDate(0, 0, 1)
		}
		gap := candidate.Sub(now)
		if !haveBest || gap < best {
			best = gap
			haveBest = true
		}
	}
	if !haveBest {
		return time.Hour
	}
	return best
}

// FireOnce assembles the briefing context, summarises it, and delivers it.
// Ported from the C# FireOnceAsync. If no signal is gathered, it returns without
// delivering.
func (s *ProactiveBriefingService) FireOnce(ctx context.Context) error {
	var ctxParts []string

	// Calendar — next 24 hours.
	now := s.now()
	for _, cal := range s.calendars {
		if !cal.IsConfigured() {
			continue
		}
		events, err := cal.ListEvents(ctx, now, now.Add(24*time.Hour))
		if err != nil {
			continue // per-connector failure is skipped (LogDebug in C#).
		}
		if len(events) > 0 {
			ctxParts = append(ctxParts, fmt.Sprintf("### Calendar (%s)", cal.ProviderID()))
			ordered := make([]BriefingCalendarEvent, len(events))
			copy(ordered, events)
			sort.SliceStable(ordered, func(i, j int) bool { return ordered[i].StartUTC.Before(ordered[j].StartUTC) })
			if len(ordered) > 8 {
				ordered = ordered[:8]
			}
			for _, e := range ordered {
				line := fmt.Sprintf("- %s %s", e.StartUTC.Local().Format("15:04"), e.Title)
				if e.Location != "" {
					line += " @ " + e.Location
				}
				ctxParts = append(ctxParts, line)
			}
		}
	}

	// Email — unread.
	for _, em := range s.emails {
		if !em.IsConfigured() {
			continue
		}
		unread, err := em.ListUnread(ctx, 5)
		if err != nil {
			continue
		}
		if len(unread) > 0 {
			ctxParts = append(ctxParts, fmt.Sprintf("### Unread email (%s)", em.ProviderID()))
			for _, m := range unread {
				ctxParts = append(ctxParts, fmt.Sprintf("- %s: %s", m.From, m.Subject))
			}
		}
	}

	// News — latest from each source.
	for _, src := range s.news {
		if !src.IsConfigured() {
			continue
		}
		items, err := src.FetchLatest(ctx, 5)
		if err != nil {
			continue
		}
		if len(items) > 0 {
			ctxParts = append(ctxParts, fmt.Sprintf("### News (%s)", src.SourceID()))
			for _, i := range items {
				ctxParts = append(ctxParts, "- "+i.Title)
			}
		}
	}

	// Weather — if location configured.
	if s.weather != nil && s.opts.Latitude != nil && s.opts.Longitude != nil {
		if w, err := s.weather.Current(ctx, *s.opts.Latitude, *s.opts.Longitude); err == nil {
			ctxParts = append(ctxParts, fmt.Sprintf("### Weather (%s)", s.weather.ProviderID()))
			ctxParts = append(ctxParts, fmt.Sprintf("- %.0f°C %s, feels %.0f°C, wind %.0f km/h",
				w.TempC, w.Condition, w.FeelsLikeC, w.WindKph))
		}
	}

	if len(ctxParts) == 0 {
		return nil // no signals; skip.
	}

	context := strings.Join(ctxParts, "\n")
	prompt := "Summarise the user's morning briefing in 80 words or less. Warm but factual. End with the one thing they should do first today.\n\n" + context

	summary := context
	if s.summariser != nil {
		if out, err := s.summariser(ctx, prompt); err == nil {
			summary = out
		} // on failure, fall back to raw context (C# behaviour).
	}

	for _, notifier := range s.notifiers {
		_ = notifier.Deliver(ctx, s.opts.Headline, summary, s.opts.DeliveryAddress)
	}
	return nil
}
