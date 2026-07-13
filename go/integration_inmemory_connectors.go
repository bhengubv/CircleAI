// integration_inmemory_connectors.go
//
// Ports CircleAI.Integration/InMemoryIntegrationConnectors.cs — the
// deterministic, dependency-free in-memory reference implementations of the
// integration connector contracts (integration_contracts.go). These are the
// canonical offline/test doubles for ICalendarConnector / IEmailConnector /
// INewsSource / IWeatherProvider / IRoutingProvider / IHomeAutomationConnector,
// mirroring the InMemory* pattern every other package ships. The real provider
// bindings live in integration_calendar.go / integration_email.go /
// integration_news.go / integration_geo.go / integration_home.go.
//
// DETERMINISM: every numeric result is reproduced to match the C# byte-for-byte.
//   - Weather: pseudo-weather derived from lat/lon/hour with no randomness. The
//     C# uses Math.Round(x, 2) which is BANKER'S rounding (MidpointRounding.ToEven);
//     roundToEvenPlaces reproduces that. The epoch stamp is UnixEpoch + hourOffset.
//   - Routing: great-circle (haversine) distance on R=6371 km, a mode→speed table,
//     duration = km/kph hours, distance rounded to 3 places (also ToEven).
// The C# stores live in unordered ConcurrentDictionaries whose enumeration order is
// unspecified, so equal-key orderings are non-deterministic there; this port adds a
// stable id tiebreak so identical inputs always yield identical output (the same
// primary ordering as C#, deterministic on ties) — the documented port convention.

package circleai

import (
	"context"
	"math"
	"sort"
	"strings"
	"sync"
	"time"
)

// roundToEvenPlaces rounds v to the given number of decimal places using
// round-half-to-even (banker's rounding), reproducing .NET's default
// Math.Round(double, digits, MidpointRounding.ToEven). For the finite,
// small-magnitude values these connectors emit this equals the C# result.
func roundToEvenPlaces(v float64, places int) float64 {
	scale := math.Pow(10, float64(places))
	return math.RoundToEven(v*scale) / scale
}

// ── In-memory calendar ──────────────────────────────────────────────────────

// InMemoryCalendarConnector is a deterministic in-memory ICalendarConnector:
// events are held in a map; listing returns those overlapping the window, ordered
// by start. Ports InMemoryCalendarConnector. Safe for concurrent use.
type InMemoryCalendarConnector struct {
	mu     sync.Mutex
	events map[string]CalendarEvent
}

// NewInMemoryCalendarConnector constructs an empty connector.
func NewInMemoryCalendarConnector() *InMemoryCalendarConnector {
	return &InMemoryCalendarConnector{events: make(map[string]CalendarEvent)}
}

// ProviderID is "in-memory".
func (c *InMemoryCalendarConnector) ProviderID() string { return "in-memory" }

// IsConfigured is always true.
func (c *InMemoryCalendarConnector) IsConfigured() bool { return true }

// ListEvents returns events overlapping [fromUtc, toUtc) (StartUtc < toUtc &&
// EndUtc > fromUtc), ordered by StartUtc ascending (EventID tiebreak for
// determinism). Ports ListEventsAsync.
func (c *InMemoryCalendarConnector) ListEvents(_ context.Context, fromUtc, toUtc time.Time) ([]CalendarEvent, error) {
	c.mu.Lock()
	out := make([]CalendarEvent, 0)
	for _, e := range c.events {
		if e.StartUtc.Before(toUtc) && e.EndUtc.After(fromUtc) {
			out = append(out, e)
		}
	}
	c.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].StartUtc.Equal(out[j].StartUtc) {
			return out[i].StartUtc.Before(out[j].StartUtc)
		}
		return out[i].EventID < out[j].EventID
	})
	return out, nil
}

// CreateEvent stores (or replaces by EventID) ev and returns it. Ports
// CreateEventAsync.
func (c *InMemoryCalendarConnector) CreateEvent(_ context.Context, ev CalendarEvent) (CalendarEvent, error) {
	c.mu.Lock()
	c.events[ev.EventID] = ev
	c.mu.Unlock()
	return ev, nil
}

// DeleteEvent removes the event by eventID (calendarID is ignored, matching the
// C#). Ports DeleteEventAsync.
func (c *InMemoryCalendarConnector) DeleteEvent(_ context.Context, calendarID, eventID string) error {
	c.mu.Lock()
	delete(c.events, eventID)
	c.mu.Unlock()
	return nil
}

// ── In-memory email ─────────────────────────────────────────────────────────

// InMemoryEmailConnector is a deterministic in-memory IEmailConnector: seeded
// with messages; unread + search read newest-first, MarkRead flips the flag.
// Ports InMemoryEmailConnector. Safe for concurrent use.
type InMemoryEmailConnector struct {
	mu       sync.Mutex
	messages map[string]EmailMessage
}

// NewInMemoryEmailConnector constructs the connector, seeded with the given
// messages (nil seed == empty), keyed by MessageID. Ports the C# ctor's optional
// seed.
func NewInMemoryEmailConnector(seed []EmailMessage) *InMemoryEmailConnector {
	c := &InMemoryEmailConnector{messages: make(map[string]EmailMessage)}
	for _, m := range seed {
		c.messages[m.MessageID] = m
	}
	return c
}

// ProviderID is "in-memory".
func (c *InMemoryEmailConnector) ProviderID() string { return "in-memory" }

// IsConfigured is always true.
func (c *InMemoryEmailConnector) IsConfigured() bool { return true }

// ListUnread returns up to max unread messages, newest-first (ReceivedUtc desc,
// MessageID tiebreak). A negative max yields nothing (mirrors Math.Max(0, max)).
// Ports ListUnreadAsync.
func (c *InMemoryEmailConnector) ListUnread(_ context.Context, max int) ([]EmailMessage, error) {
	c.mu.Lock()
	out := make([]EmailMessage, 0)
	for _, m := range c.messages {
		if m.Unread {
			out = append(out, m)
		}
	}
	c.mu.Unlock()
	sortEmailsNewestFirst(out)
	return capEmails(out, max), nil
}

// Search returns up to max messages whose Subject or BodyText contains query
// (case-insensitive), newest-first. A nil query is treated as "" (matches all).
// Ports SearchAsync.
func (c *InMemoryEmailConnector) Search(_ context.Context, query string, max int) ([]EmailMessage, error) {
	needle := strings.ToLower(query)
	c.mu.Lock()
	out := make([]EmailMessage, 0)
	for _, m := range c.messages {
		if strings.Contains(strings.ToLower(m.Subject), needle) ||
			strings.Contains(strings.ToLower(m.BodyText), needle) {
			out = append(out, m)
		}
	}
	c.mu.Unlock()
	sortEmailsNewestFirst(out)
	return capEmails(out, max), nil
}

// MarkRead clears the Unread flag on messageID (no-op if unknown), copying the
// stored value like the C# `m with { Unread = false }`. Ports MarkReadAsync.
func (c *InMemoryEmailConnector) MarkRead(_ context.Context, messageID string) error {
	c.mu.Lock()
	if m, ok := c.messages[messageID]; ok {
		m.Unread = false
		c.messages[messageID] = m
	}
	c.mu.Unlock()
	return nil
}

// sortEmailsNewestFirst orders by ReceivedUtc descending with a MessageID
// ascending tiebreak (C# OrderByDescending is stable over an unordered source; the
// tiebreak makes ties deterministic here).
func sortEmailsNewestFirst(xs []EmailMessage) {
	sort.SliceStable(xs, func(i, j int) bool {
		if !xs[i].ReceivedUtc.Equal(xs[j].ReceivedUtc) {
			return xs[i].ReceivedUtc.After(xs[j].ReceivedUtc)
		}
		return xs[i].MessageID < xs[j].MessageID
	})
}

// capEmails takes the first max entries (max<=0 yields an empty slice, mirroring
// Take(Math.Max(0, max))).
func capEmails(xs []EmailMessage, max int) []EmailMessage {
	if max <= 0 {
		return []EmailMessage{}
	}
	if len(xs) > max {
		return xs[:max]
	}
	return xs
}

// ── In-memory news ──────────────────────────────────────────────────────────

// InMemoryNewsSource is a deterministic in-memory INewsSource: seeded items,
// newest-first. Ports InMemoryNewsSource. Safe for concurrent use.
type InMemoryNewsSource struct {
	mu    sync.Mutex
	items map[string]NewsItem
}

// NewInMemoryNewsSource constructs the source, seeded with the given items (nil
// seed == empty), keyed by ItemID. Ports the C# ctor's optional seed.
func NewInMemoryNewsSource(seed []NewsItem) *InMemoryNewsSource {
	s := &InMemoryNewsSource{items: make(map[string]NewsItem)}
	for _, i := range seed {
		s.items[i.ItemID] = i
	}
	return s
}

// SourceID is "in-memory".
func (s *InMemoryNewsSource) SourceID() string { return "in-memory" }

// IsConfigured is always true.
func (s *InMemoryNewsSource) IsConfigured() bool { return true }

// FetchLatest returns up to max items, newest-first (PublishedUtc desc, ItemID
// tiebreak). A negative max yields nothing (mirrors Math.Max(0, max)). Ports
// FetchLatestAsync.
func (s *InMemoryNewsSource) FetchLatest(_ context.Context, max int) ([]NewsItem, error) {
	s.mu.Lock()
	out := make([]NewsItem, 0, len(s.items))
	for _, i := range s.items {
		out = append(out, i)
	}
	s.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].PublishedUtc.Equal(out[j].PublishedUtc) {
			return out[i].PublishedUtc.After(out[j].PublishedUtc)
		}
		return out[i].ItemID < out[j].ItemID
	})
	if max <= 0 {
		return []NewsItem{}, nil
	}
	if len(out) > max {
		out = out[:max]
	}
	return out, nil
}

// ── In-memory weather ───────────────────────────────────────────────────────

// InMemoryWeatherProvider is a deterministic in-memory IWeatherProvider:
// pseudo-weather derived from coordinates + hour (no randomness, reproducible
// across platforms). Ports InMemoryWeatherProvider.
type InMemoryWeatherProvider struct{}

// NewInMemoryWeatherProvider constructs the provider.
func NewInMemoryWeatherProvider() *InMemoryWeatherProvider { return &InMemoryWeatherProvider{} }

// ProviderID is "in-memory".
func (p *InMemoryWeatherProvider) ProviderID() string { return "in-memory" }

// Current returns the sample at hour offset 0. Ports CurrentAsync.
func (p *InMemoryWeatherProvider) Current(_ context.Context, lat, lon float64) (WeatherSample, error) {
	return inMemoryWeatherSample(lat, lon, 0), nil
}

// Hourly returns one sample per hour for [0, hours) (negative hours yields
// nothing, mirroring Math.Max(0, hours)). Ports HourlyAsync.
func (p *InMemoryWeatherProvider) Hourly(_ context.Context, lat, lon float64, hours int) ([]WeatherSample, error) {
	if hours < 0 {
		hours = 0
	}
	out := make([]WeatherSample, 0, hours)
	for h := 0; h < hours; h++ {
		out = append(out, inMemoryWeatherSample(lat, lon, h))
	}
	return out, nil
}

// inMemoryWeatherSample reproduces the C# Sample(lat, lon, hourOffset):
//
//	tempC = Round(15 + 10*Cos((lat+hourOffset) * PI/12), 2)
//	sample(UnixEpoch + hourOffset h, tempC, Round(tempC-1.5, 2), 0, 12, 40, "Clear")
//
// Rounding is banker's (ToEven), matching .NET Math.Round(x, 2).
func inMemoryWeatherSample(lat, lon float64, hourOffset int) WeatherSample {
	tempC := roundToEvenPlaces(15.0+10.0*math.Cos((lat+float64(hourOffset))*math.Pi/12.0), 2)
	return WeatherSample{
		AtUtc:      time.Unix(0, 0).UTC().Add(time.Duration(hourOffset) * time.Hour),
		TempC:      tempC,
		FeelsLikeC: roundToEvenPlaces(tempC-1.5, 2),
		PrecipMm:   0.0,
		WindKph:    12.0,
		CloudPct:   40,
		Condition:  "Clear",
	}
}

// ── In-memory routing ───────────────────────────────────────────────────────

// InMemoryRoutingProvider is a deterministic in-memory IRoutingProvider:
// great-circle distance and a mode-based speed give a deterministic estimate with
// a 2-point polyline. Ports InMemoryRoutingProvider.
type InMemoryRoutingProvider struct{}

// NewInMemoryRoutingProvider constructs the provider.
func NewInMemoryRoutingProvider() *InMemoryRoutingProvider { return &InMemoryRoutingProvider{} }

// ProviderID is "in-memory".
func (p *InMemoryRoutingProvider) ProviderID() string { return "in-memory" }

// Route estimates from (fromLat,fromLon) to (toLat,toLon) for mode. Distance is
// the haversine km rounded to 3 places (ToEven); speed comes from the mode table
// (walk 5, bike 18, transit 30, else 60 kph); duration = km/kph hours (0 when
// kph<=0). The polyline is the two endpoints. Ports RouteAsync — note the C#
// default mode is "car" (empty/unknown mode maps to 60 kph here).
func (p *InMemoryRoutingProvider) Route(_ context.Context, fromLat, fromLon, toLat, toLon float64, mode string) (RouteEstimate, error) {
	km := inMemoryHaversine(fromLat, fromLon, toLat, toLon)
	var kph float64
	switch mode {
	case "walk":
		kph = 5.0
	case "bike":
		kph = 18.0
	case "transit":
		kph = 30.0
	default:
		kph = 60.0
	}
	var dur time.Duration
	if kph > 0 {
		dur = time.Duration((km / kph) * float64(time.Hour))
	}
	return RouteEstimate{
		DistanceKm: roundToEvenPlaces(km, 3),
		Duration:   dur,
		Polyline:   []GeoPoint{{Lat: fromLat, Lon: fromLon}, {Lat: toLat, Lon: toLon}},
	}, nil
}

// inMemoryHaversine reproduces the C# Haversine(lat1,lon1,lat2,lon2) on R=6371 km
// exactly: same operation order over math.Sin/Cos/Sqrt/Atan2 (IEEE-754 identical).
func inMemoryHaversine(lat1, lon1, lat2, lon2 float64) float64 {
	const r = 6371.0
	dLat := (lat2 - lat1) * math.Pi / 180.0
	dLon := (lon2 - lon1) * math.Pi / 180.0
	a := math.Sin(dLat/2)*math.Sin(dLat/2) +
		math.Cos(lat1*math.Pi/180.0)*math.Cos(lat2*math.Pi/180.0)*
			math.Sin(dLon/2)*math.Sin(dLon/2)
	return r * 2 * math.Atan2(math.Sqrt(a), math.Sqrt(1-a))
}

// ── In-memory home automation ───────────────────────────────────────────────

// InMemoryHomeAutomationConnector is a deterministic in-memory
// IHomeAutomationConnector: seeded entities; turn_on/turn_off/toggle
// deterministically mutate matching-domain entity state. Ports
// InMemoryHomeAutomationConnector. Safe for concurrent use.
type InMemoryHomeAutomationConnector struct {
	mu       sync.Mutex
	entities map[string]HaEntity
}

// NewInMemoryHomeAutomationConnector constructs the connector, seeded with the
// given entities (nil seed == empty), keyed by EntityID. Ports the C# ctor's
// optional seed.
func NewInMemoryHomeAutomationConnector(seed []HaEntity) *InMemoryHomeAutomationConnector {
	c := &InMemoryHomeAutomationConnector{entities: make(map[string]HaEntity)}
	for _, e := range seed {
		c.entities[e.EntityID] = e
	}
	return c
}

// ProviderID is "in-memory".
func (c *InMemoryHomeAutomationConnector) ProviderID() string { return "in-memory" }

// IsConfigured is always true.
func (c *InMemoryHomeAutomationConnector) IsConfigured() bool { return true }

// ListEntities returns all entities ordered by EntityID. Ports ListEntitiesAsync.
func (c *InMemoryHomeAutomationConnector) ListEntities(_ context.Context) ([]HaEntity, error) {
	c.mu.Lock()
	out := make([]HaEntity, 0, len(c.entities))
	for _, e := range c.entities {
		out = append(out, e)
	}
	c.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].EntityID < out[j].EntityID })
	return out, nil
}

// CallService applies service to every entity whose Domain matches
// (case-insensitive): turn_on→"on", turn_off→"off", toggle flips on/off, any
// other service leaves the state unchanged. The stored entity is copied with the
// new State (Attributes preserved), mirroring the C# `e with { State = newState }`.
// The data payload is accepted but unused, matching the C#. Ports CallServiceAsync.
func (c *InMemoryHomeAutomationConnector) CallService(_ context.Context, domain, service string, data map[string]interface{}) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	for id, e := range c.entities {
		if !strings.EqualFold(e.Domain, domain) {
			continue
		}
		var newState string
		switch service {
		case "turn_on":
			newState = "on"
		case "turn_off":
			newState = "off"
		case "toggle":
			if e.State == "on" {
				newState = "off"
			} else {
				newState = "on"
			}
		default:
			newState = e.State
		}
		e.State = newState
		c.entities[id] = e
	}
	return nil
}

// Interface guards.
var (
	_ ICalendarConnector       = (*InMemoryCalendarConnector)(nil)
	_ IEmailConnector          = (*InMemoryEmailConnector)(nil)
	_ INewsSource              = (*InMemoryNewsSource)(nil)
	_ IWeatherProvider         = (*InMemoryWeatherProvider)(nil)
	_ IRoutingProvider         = (*InMemoryRoutingProvider)(nil)
	_ IHomeAutomationConnector = (*InMemoryHomeAutomationConnector)(nil)
)
