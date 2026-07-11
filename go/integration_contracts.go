// integration_contracts.go
//
// Ports CircleAI.Integration/Contracts.cs — the shared abstractions for the
// external-integration layer (calendar, email, news, weather, routing, home
// automation). Records become structs; the C# interfaces become Go interfaces.
//
// C# types ported here:
//   CalendarEvent              -> CalendarEvent
//   ICalendarConnector         -> ICalendarConnector
//   EmailMessage               -> EmailMessage
//   IEmailConnector            -> IEmailConnector
//   NewsItem                   -> NewsItem
//   INewsSource                -> INewsSource
//   WeatherSample              -> WeatherSample
//   IWeatherProvider           -> IWeatherProvider
//   RouteEstimate + (Lat,Lon)  -> RouteEstimate + GeoPoint
//   IRoutingProvider           -> IRoutingProvider
//   HaEntity                   -> HaEntity
//   IHomeAutomationConnector   -> IHomeAutomationConnector
//
// C# uses DateTimeOffset (UTC) — modelled as time.Time (callers pass UTC, as the
// connectors normalise to UTC). Uri fields become string (the connectors build /
// validate them). IReadOnlyList<T> -> []T; IReadOnlyDictionary -> map. The
// ValueTask + CancellationToken async surface becomes (context.Context, ...) with
// an error return, matching the rest of the Go tree.
//
// This file also carries the small JSON scalar helpers (tjInt/tjFloat/tjBool/
// tjStringElem/asJSONObject) the integration connectors need on top of the
// existing telephony wire helpers (parseJSONObject/tjString/tjArray/tjObject).

package circleai

import (
	"context"
	"encoding/json"
	"strconv"
	"time"
)

// ── Calendar ────────────────────────────────────────────────────────────────

// CalendarEvent is a single calendar event. Ports the CalendarEvent record.
// StartUtc/EndUtc are UTC instants; Attendees is an email list (never nil for a
// fully-built event, but callers should tolerate nil == empty).
type CalendarEvent struct {
	EventID     string
	CalendarID  string
	Title       string
	Description *string // nil when absent (C# string?)
	Location    *string // nil when absent (C# string?)
	StartUtc    time.Time
	EndUtc      time.Time
	IsAllDay    bool
	Attendees   []string
}

// ICalendarConnector is a calendar provider (Google, MS Graph, CalDAV). Ports
// ICalendarConnector.
type ICalendarConnector interface {
	// ProviderID is the stable provider identifier (e.g. "google-calendar").
	ProviderID() string
	// IsConfigured reports whether the connector has the credentials it needs.
	IsConfigured() bool
	// ListEvents returns events overlapping [fromUtc, toUtc].
	ListEvents(ctx context.Context, fromUtc, toUtc time.Time) ([]CalendarEvent, error)
	// CreateEvent creates ev and returns it with its provider-assigned EventID.
	CreateEvent(ctx context.Context, ev CalendarEvent) (CalendarEvent, error)
	// DeleteEvent deletes the event identified by (calendarID, eventID).
	DeleteEvent(ctx context.Context, calendarID, eventID string) error
}

// ── Email ───────────────────────────────────────────────────────────────────

// EmailMessage is a single mail message. Ports the EmailMessage record.
type EmailMessage struct {
	MessageID   string
	From        string
	To          []string
	Subject     string
	BodyText    string
	ReceivedUtc time.Time
	Unread      bool
	Labels      []string
}

// IEmailConnector is a mail provider (Gmail, IMAP, MS Graph). Ports
// IEmailConnector.
type IEmailConnector interface {
	// ProviderID is the stable provider identifier (e.g. "gmail").
	ProviderID() string
	// IsConfigured reports whether the connector has the credentials it needs.
	IsConfigured() bool
	// ListUnread returns up to max unread messages.
	ListUnread(ctx context.Context, max int) ([]EmailMessage, error)
	// Search returns up to max messages matching query.
	Search(ctx context.Context, query string, max int) ([]EmailMessage, error)
	// MarkRead marks the message identified by messageID as read.
	MarkRead(ctx context.Context, messageID string) error
}

// ── News + social feeds ─────────────────────────────────────────────────────

// NewsItem is a single news / social headline. Ports the NewsItem record. URL is
// the item's link (the connectors emit "about:blank" for a missing/invalid link,
// matching the C# new Uri("about:blank") fallback).
type NewsItem struct {
	ItemID       string
	SourceID     string
	Title        string
	Summary      string
	URL          string
	PublishedUtc time.Time
	Tags         []string
}

// INewsSource is a news / social feed source (RSS, NewsAPI, Mastodon, Bluesky).
// Ports INewsSource.
type INewsSource interface {
	// SourceID is the stable source identifier (e.g. "newsapi:bitcoin").
	SourceID() string
	// IsConfigured reports whether the source can be fetched.
	IsConfigured() bool
	// FetchLatest returns up to max latest items.
	FetchLatest(ctx context.Context, max int) ([]NewsItem, error)
}

// ── Weather ─────────────────────────────────────────────────────────────────

// WeatherSample is a single weather reading. Ports the WeatherSample record.
type WeatherSample struct {
	AtUtc      time.Time
	TempC      float64
	FeelsLikeC float64
	PrecipMm   float64
	WindKph    float64
	CloudPct   int
	Condition  string
}

// IWeatherProvider is a weather provider (Open-Meteo). Ports IWeatherProvider.
type IWeatherProvider interface {
	// ProviderID is the stable provider identifier (e.g. "open-meteo").
	ProviderID() string
	// Current returns the current conditions at (lat, lon).
	Current(ctx context.Context, lat, lon float64) (WeatherSample, error)
	// Hourly returns the next `hours` hourly samples at (lat, lon).
	Hourly(ctx context.Context, lat, lon float64, hours int) ([]WeatherSample, error)
}

// ── Routing / traffic ───────────────────────────────────────────────────────

// GeoPoint is a (lat, lon) pair. Ports the C# tuple (double Lat, double Lon).
type GeoPoint struct {
	Lat float64
	Lon float64
}

// RouteEstimate is a routing result. Ports the RouteEstimate record. Duration is
// a time.Duration (C# TimeSpan).
type RouteEstimate struct {
	DistanceKm float64
	Duration   time.Duration
	Polyline   []GeoPoint
}

// IRoutingProvider is a routing / traffic provider (OSRM). Ports IRoutingProvider.
type IRoutingProvider interface {
	// ProviderID is the stable provider identifier (e.g. "osrm").
	ProviderID() string
	// Route estimates a route from (fromLat,fromLon) to (toLat,toLon) for mode
	// ("car"/"bike"/"foot"). mode == "" is treated as "car" by callers.
	Route(ctx context.Context, fromLat, fromLon, toLat, toLon float64, mode string) (RouteEstimate, error)
}

// ── Home automation ─────────────────────────────────────────────────────────

// HaEntity is a home-automation entity + its attributes. Ports the HaEntity
// record.
type HaEntity struct {
	EntityID     string
	FriendlyName string
	Domain       string
	State        string
	Attributes   map[string]string
}

// IHomeAutomationConnector is a home-automation provider (Home Assistant). Ports
// IHomeAutomationConnector.
type IHomeAutomationConnector interface {
	// ProviderID is the stable provider identifier (e.g. "home-assistant").
	ProviderID() string
	// IsConfigured reports whether the connector has the credentials it needs.
	IsConfigured() bool
	// ListEntities returns all entities and their current state.
	ListEntities(ctx context.Context) ([]HaEntity, error)
	// CallService calls domain.service with the given (optional) data payload.
	CallService(ctx context.Context, domain, service string, data map[string]interface{}) error
}

// ── JSON scalar helpers (extend the telephony wire helpers) ─────────────────

// tjInt returns the int at key. Mirrors GetProperty(name).GetInt32(): a JSON
// number is truncated to an int; a numeric JSON string parses; else (0,false).
func tjInt(obj map[string]interface{}, key string) (int, bool) {
	if obj == nil {
		return 0, false
	}
	v, ok := obj[key]
	if !ok {
		return 0, false
	}
	return tjIntRaw(v)
}

// tjIntRaw converts a decoded JSON value to an int.
func tjIntRaw(v interface{}) (int, bool) {
	switch t := v.(type) {
	case json.Number:
		if f, err := t.Float64(); err == nil {
			return int(f), true
		}
		return 0, false
	case float64:
		return int(t), true
	case int:
		return t, true
	case string:
		if f, err := strconv.ParseFloat(t, 64); err == nil {
			return int(f), true
		}
		return 0, false
	default:
		return 0, false
	}
}

// tjFloat returns the float64 at key. Mirrors GetProperty(name).GetDouble(): a
// JSON number → its value; a numeric JSON string parses; else (0,false).
func tjFloat(obj map[string]interface{}, key string) (float64, bool) {
	if obj == nil {
		return 0, false
	}
	v, ok := obj[key]
	if !ok {
		return 0, false
	}
	return tjFloatRaw(v)
}

// tjFloatRaw converts a decoded JSON value to a float64.
func tjFloatRaw(v interface{}) (float64, bool) {
	switch t := v.(type) {
	case json.Number:
		if f, err := t.Float64(); err == nil {
			return f, true
		}
		return 0, false
	case float64:
		return t, true
	case int:
		return float64(t), true
	case string:
		if f, err := strconv.ParseFloat(t, 64); err == nil {
			return f, true
		}
		return 0, false
	default:
		return 0, false
	}
}

// tjBool returns the bool at key. Mirrors GetProperty(name).GetBoolean() and the
// isRead == JsonValueKind.False checks; a JSON true/false → its value; else
// (false,false).
func tjBool(obj map[string]interface{}, key string) (bool, bool) {
	if obj == nil {
		return false, false
	}
	v, ok := obj[key]
	if !ok {
		return false, false
	}
	b, ok := v.(bool)
	return b, ok
}

// tjStringElem returns the string form of a decoded JSON array element or object
// value, mirroring the HA attribute stringification (String -> raw, Number ->
// text, True/False -> "true"/"false", else its textual form).
func tjStringElem(v interface{}) string {
	switch t := v.(type) {
	case string:
		return t
	case json.Number:
		return t.String()
	case float64:
		return strconv.FormatFloat(t, 'f', -1, 64)
	case bool:
		if t {
			return "true"
		}
		return "false"
	case nil:
		return ""
	default:
		b, err := json.Marshal(t)
		if err != nil {
			return ""
		}
		return string(b)
	}
}

// asJSONObject asserts v to a JSON object map. Mirrors "element is an object".
func asJSONObject(v interface{}) (map[string]interface{}, bool) {
	m, ok := v.(map[string]interface{})
	return m, ok
}
