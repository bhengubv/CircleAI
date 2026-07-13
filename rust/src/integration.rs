//! integration — CircleAI integration reference connectors.
//!
//! Full Rust port of `src/CircleAI.Integration/InMemoryIntegrationConnectors.cs`
//! together with the connector contracts they implement: deterministic,
//! dependency-free in-memory reference implementations of the integration
//! connector seams. These are the canonical offline/test doubles for calendar,
//! email, news, weather, routing, and home-automation, usable without any
//! external provider.
//!
//! The numeric connectors ([`InMemoryWeatherProvider`], [`InMemoryRoutingProvider`])
//! reproduce the C# formulas byte-for-byte, including .NET's banker's rounding
//! (`Math.Round`, round-half-to-even) via [`round_even`].
//!
//! Sync-only (the C# `ValueTask` methods collapse to plain returns, matching this
//! crate's other ports); `DateTimeOffset` → [`chrono::DateTime<Utc>`]; `TimeSpan`
//! → [`chrono::Duration`].
//!
//! Note: the briefing service in [`crate::companion`] carries its own reduced
//! connector seams tailored to that pipeline; these are the full-contract
//! reference connectors mirroring `CircleAI.Integration`.

use std::collections::HashMap;
use std::f64::consts::PI;
use std::sync::Mutex;

use chrono::{DateTime, Duration, TimeZone, Utc};

/// Rounds `value` to `digits` decimal places using round-half-to-even (banker's
/// rounding), matching .NET's default `Math.Round(double, int)`.
pub fn round_even(value: f64, digits: u32) -> f64 {
    let factor = 10f64.powi(digits as i32);
    let scaled = value * factor;
    let floor = scaled.floor();
    let diff = scaled - floor;
    let rounded = if (diff - 0.5).abs() < f64::EPSILON {
        // Exactly halfway: round to the even neighbour.
        if (floor as i64) % 2 == 0 {
            floor
        } else {
            floor + 1.0
        }
    } else {
        scaled.round()
    };
    rounded / factor
}

// ─────────────────────────────────────────────────────────────────────────────
// Calendar
// ─────────────────────────────────────────────────────────────────────────────

/// (Integration) A calendar event.
///
/// Mirrors the `CalendarEvent` shape consumed by `ICalendarConnector`
/// (`EventId`, `StartUtc`, `EndUtc`, `Title`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CalendarEvent {
    pub event_id: String,
    pub title: String,
    pub start_utc: DateTime<Utc>,
    pub end_utc: DateTime<Utc>,
}

impl CalendarEvent {
    /// Constructs a calendar event.
    pub fn new(
        event_id: impl Into<String>,
        title: impl Into<String>,
        start_utc: DateTime<Utc>,
        end_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            event_id: event_id.into(),
            title: title.into(),
            start_utc,
            end_utc,
        }
    }
}

/// (Integration) A calendar connector.
///
/// Mirrors `interface ICalendarConnector`.
pub trait ICalendarConnector {
    /// A stable provider identifier.
    fn provider_id(&self) -> String;
    /// Whether the connector is configured.
    fn is_configured(&self) -> bool;
    /// Events overlapping `[from_utc, to_utc)`, ordered by start.
    fn list_events(&self, from_utc: DateTime<Utc>, to_utc: DateTime<Utc>) -> Vec<CalendarEvent>;
    /// Creates (or overwrites) an event and returns it.
    fn create_event(&self, ev: CalendarEvent) -> CalendarEvent;
    /// Deletes an event by id (calendar id is ignored by the in-memory double).
    fn delete_event(&self, calendar_id: &str, event_id: &str);
}

/// (Integration) In-memory [`ICalendarConnector`]: events held in a map; listing
/// returns those overlapping the window, ordered by start.
///
/// Mirrors `sealed class InMemoryCalendarConnector`.
pub struct InMemoryCalendarConnector {
    events: Mutex<HashMap<String, CalendarEvent>>,
}

impl InMemoryCalendarConnector {
    /// Creates an empty connector.
    pub fn new() -> Self {
        Self {
            events: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryCalendarConnector {
    fn default() -> Self {
        Self::new()
    }
}

impl ICalendarConnector for InMemoryCalendarConnector {
    fn provider_id(&self) -> String {
        "in-memory".to_string()
    }

    fn is_configured(&self) -> bool {
        true
    }

    fn list_events(&self, from_utc: DateTime<Utc>, to_utc: DateTime<Utc>) -> Vec<CalendarEvent> {
        let mut hits: Vec<CalendarEvent> = self
            .events
            .lock()
            .unwrap()
            .values()
            .filter(|e| e.start_utc < to_utc && e.end_utc > from_utc)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.start_utc.cmp(&b.start_utc));
        hits
    }

    fn create_event(&self, ev: CalendarEvent) -> CalendarEvent {
        self.events
            .lock()
            .unwrap()
            .insert(ev.event_id.clone(), ev.clone());
        ev
    }

    fn delete_event(&self, _calendar_id: &str, event_id: &str) {
        self.events.lock().unwrap().remove(event_id);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Email
// ─────────────────────────────────────────────────────────────────────────────

/// (Integration) An email message.
///
/// Mirrors the `EmailMessage` shape consumed by `IEmailConnector` (`MessageId`,
/// `Subject`, `BodyText`, `Unread`, `ReceivedUtc`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EmailMessage {
    pub message_id: String,
    pub subject: String,
    pub body_text: String,
    pub unread: bool,
    pub received_utc: DateTime<Utc>,
}

impl EmailMessage {
    /// Constructs an email message.
    pub fn new(
        message_id: impl Into<String>,
        subject: impl Into<String>,
        body_text: impl Into<String>,
        unread: bool,
        received_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            message_id: message_id.into(),
            subject: subject.into(),
            body_text: body_text.into(),
            unread,
            received_utc,
        }
    }
}

/// (Integration) An email connector.
///
/// Mirrors `interface IEmailConnector`.
pub trait IEmailConnector {
    fn provider_id(&self) -> String;
    fn is_configured(&self) -> bool;
    /// Unread messages, newest first, capped at `max` (`max < 0` yields nothing).
    fn list_unread(&self, max: i32) -> Vec<EmailMessage>;
    /// Messages whose subject or body contains `query` (case-insensitive), newest
    /// first, capped at `max`.
    fn search(&self, query: &str, max: i32) -> Vec<EmailMessage>;
    /// Marks a message read (no-op for an unknown id).
    fn mark_read(&self, message_id: &str);
}

/// (Integration) In-memory [`IEmailConnector`]: seeded with messages; unread +
/// search read newest-first, [`mark_read`](IEmailConnector::mark_read) flips the
/// flag.
///
/// Mirrors `sealed class InMemoryEmailConnector`.
pub struct InMemoryEmailConnector {
    messages: Mutex<HashMap<String, EmailMessage>>,
}

impl InMemoryEmailConnector {
    /// Creates an empty connector.
    pub fn new() -> Self {
        Self {
            messages: Mutex::new(HashMap::new()),
        }
    }

    /// Creates a connector seeded with `seed`.
    pub fn seeded(seed: impl IntoIterator<Item = EmailMessage>) -> Self {
        let mut map = HashMap::new();
        for m in seed {
            map.insert(m.message_id.clone(), m);
        }
        Self {
            messages: Mutex::new(map),
        }
    }
}

impl Default for InMemoryEmailConnector {
    fn default() -> Self {
        Self::new()
    }
}

impl IEmailConnector for InMemoryEmailConnector {
    fn provider_id(&self) -> String {
        "in-memory".to_string()
    }

    fn is_configured(&self) -> bool {
        true
    }

    fn list_unread(&self, max: i32) -> Vec<EmailMessage> {
        let take = max.max(0) as usize;
        let mut hits: Vec<EmailMessage> = self
            .messages
            .lock()
            .unwrap()
            .values()
            .filter(|m| m.unread)
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.received_utc.cmp(&a.received_utc));
        hits.truncate(take);
        hits
    }

    fn search(&self, query: &str, max: i32) -> Vec<EmailMessage> {
        let take = max.max(0) as usize;
        let needle = query.to_lowercase();
        let mut hits: Vec<EmailMessage> = self
            .messages
            .lock()
            .unwrap()
            .values()
            .filter(|m| {
                m.subject.to_lowercase().contains(&needle)
                    || m.body_text.to_lowercase().contains(&needle)
            })
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.received_utc.cmp(&a.received_utc));
        hits.truncate(take);
        hits
    }

    fn mark_read(&self, message_id: &str) {
        if let Some(m) = self.messages.lock().unwrap().get_mut(message_id) {
            m.unread = false;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// News
// ─────────────────────────────────────────────────────────────────────────────

/// (Integration) A news item.
///
/// Mirrors the `NewsItem` shape consumed by `INewsSource` (`ItemId`,
/// `PublishedUtc`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NewsItem {
    pub item_id: String,
    pub title: String,
    pub published_utc: DateTime<Utc>,
}

impl NewsItem {
    /// Constructs a news item.
    pub fn new(
        item_id: impl Into<String>,
        title: impl Into<String>,
        published_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            item_id: item_id.into(),
            title: title.into(),
            published_utc,
        }
    }
}

/// (Integration) A news source.
///
/// Mirrors `interface INewsSource`.
pub trait INewsSource {
    fn source_id(&self) -> String;
    fn is_configured(&self) -> bool;
    /// The latest items, newest first, capped at `max`.
    fn fetch_latest(&self, max: i32) -> Vec<NewsItem>;
}

/// (Integration) In-memory [`INewsSource`]: seeded items, newest-first.
///
/// Mirrors `sealed class InMemoryNewsSource`.
pub struct InMemoryNewsSource {
    items: Mutex<HashMap<String, NewsItem>>,
}

impl InMemoryNewsSource {
    /// Creates an empty source.
    pub fn new() -> Self {
        Self {
            items: Mutex::new(HashMap::new()),
        }
    }

    /// Creates a source seeded with `seed`.
    pub fn seeded(seed: impl IntoIterator<Item = NewsItem>) -> Self {
        let mut map = HashMap::new();
        for i in seed {
            map.insert(i.item_id.clone(), i);
        }
        Self {
            items: Mutex::new(map),
        }
    }
}

impl Default for InMemoryNewsSource {
    fn default() -> Self {
        Self::new()
    }
}

impl INewsSource for InMemoryNewsSource {
    fn source_id(&self) -> String {
        "in-memory".to_string()
    }

    fn is_configured(&self) -> bool {
        true
    }

    fn fetch_latest(&self, max: i32) -> Vec<NewsItem> {
        let take = max.max(0) as usize;
        let mut hits: Vec<NewsItem> = self.items.lock().unwrap().values().cloned().collect();
        hits.sort_by(|a, b| b.published_utc.cmp(&a.published_utc));
        hits.truncate(take);
        hits
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Weather
// ─────────────────────────────────────────────────────────────────────────────

/// (Integration) A weather sample.
///
/// Mirrors `sealed record WeatherSample(DateTimeOffset AtUtc, double TempC,
/// double FeelsLikeC, double PrecipMm, double WindKph, int HumidityPct,
/// string Condition)`.
#[derive(Debug, Clone, PartialEq)]
pub struct WeatherSample {
    pub at_utc: DateTime<Utc>,
    pub temp_c: f64,
    pub feels_like_c: f64,
    pub precip_mm: f64,
    pub wind_kph: f64,
    pub humidity_pct: i32,
    pub condition: String,
}

impl WeatherSample {
    /// Constructs a weather sample.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        at_utc: DateTime<Utc>,
        temp_c: f64,
        feels_like_c: f64,
        precip_mm: f64,
        wind_kph: f64,
        humidity_pct: i32,
        condition: impl Into<String>,
    ) -> Self {
        Self {
            at_utc,
            temp_c,
            feels_like_c,
            precip_mm,
            wind_kph,
            humidity_pct,
            condition: condition.into(),
        }
    }
}

/// (Integration) A weather provider.
///
/// Mirrors `interface IWeatherProvider`.
pub trait IWeatherProvider {
    fn provider_id(&self) -> String;
    /// Current conditions for a lat/lon.
    fn current(&self, lat: f64, lon: f64) -> WeatherSample;
    /// `hours` samples starting now for a lat/lon (`hours < 0` yields nothing).
    fn hourly(&self, lat: f64, lon: f64, hours: i32) -> Vec<WeatherSample>;
}

/// (Integration) In-memory [`IWeatherProvider`]: deterministic pseudo-weather
/// derived from coordinates + hour (no randomness, reproducible across platforms).
///
/// Mirrors `sealed class InMemoryWeatherProvider`.
pub struct InMemoryWeatherProvider;

impl InMemoryWeatherProvider {
    /// Creates the provider.
    pub fn new() -> Self {
        Self
    }

    /// The deterministic sample for a coordinate + hour offset. Mirrors the C#
    /// `Sample`: `tempC = round(15 + 10·cos((lat + hourOffset)·π/12), 2)`,
    /// `feelsLike = round(tempC − 1.5, 2)`, timestamp = Unix epoch + `hourOffset`.
    fn sample(lat: f64, _lon: f64, hour_offset: i32) -> WeatherSample {
        let temp_c = round_even(
            15.0 + 10.0 * ((lat + hour_offset as f64) * PI / 12.0).cos(),
            2,
        );
        let at_utc = Utc.timestamp_opt(0, 0).unwrap() + Duration::hours(hour_offset as i64);
        WeatherSample::new(
            at_utc,
            temp_c,
            round_even(temp_c - 1.5, 2),
            0.0,
            12.0,
            40,
            "Clear",
        )
    }
}

impl Default for InMemoryWeatherProvider {
    fn default() -> Self {
        Self::new()
    }
}

impl IWeatherProvider for InMemoryWeatherProvider {
    fn provider_id(&self) -> String {
        "in-memory".to_string()
    }

    fn current(&self, lat: f64, lon: f64) -> WeatherSample {
        Self::sample(lat, lon, 0)
    }

    fn hourly(&self, lat: f64, lon: f64, hours: i32) -> Vec<WeatherSample> {
        (0..hours.max(0)).map(|h| Self::sample(lat, lon, h)).collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Routing
// ─────────────────────────────────────────────────────────────────────────────

/// (Integration) A route estimate.
///
/// Mirrors `sealed record RouteEstimate(double DistanceKm, TimeSpan Duration,
/// IReadOnlyList<(double, double)> Polyline)`.
#[derive(Debug, Clone, PartialEq)]
pub struct RouteEstimate {
    pub distance_km: f64,
    pub duration: Duration,
    pub polyline: Vec<(f64, f64)>,
}

impl RouteEstimate {
    /// Constructs a route estimate.
    pub fn new(distance_km: f64, duration: Duration, polyline: Vec<(f64, f64)>) -> Self {
        Self {
            distance_km,
            duration,
            polyline,
        }
    }
}

/// (Integration) A routing provider.
///
/// Mirrors `interface IRoutingProvider`.
pub trait IRoutingProvider {
    fn provider_id(&self) -> String;
    /// A deterministic great-circle route estimate for `mode`
    /// (`"walk"`/`"bike"`/`"transit"`, else car).
    fn route(
        &self,
        from_lat: f64,
        from_lon: f64,
        to_lat: f64,
        to_lon: f64,
        mode: &str,
    ) -> RouteEstimate;
}

/// (Integration) In-memory [`IRoutingProvider`]: great-circle distance and a
/// mode-based speed give a deterministic estimate with a 2-point polyline.
///
/// Mirrors `sealed class InMemoryRoutingProvider`.
pub struct InMemoryRoutingProvider;

impl InMemoryRoutingProvider {
    /// Creates the provider.
    pub fn new() -> Self {
        Self
    }

    /// Great-circle distance (km) between two coordinates. Mirrors the C#
    /// `Haversine` exactly (`r = 6371`).
    pub fn haversine(lat1: f64, lon1: f64, lat2: f64, lon2: f64) -> f64 {
        const R: f64 = 6371.0;
        let d_lat = (lat2 - lat1) * PI / 180.0;
        let d_lon = (lon2 - lon1) * PI / 180.0;
        let a = (d_lat / 2.0).sin() * (d_lat / 2.0).sin()
            + (lat1 * PI / 180.0).cos()
                * (lat2 * PI / 180.0).cos()
                * (d_lon / 2.0).sin()
                * (d_lon / 2.0).sin();
        R * 2.0 * a.sqrt().atan2((1.0 - a).sqrt())
    }
}

impl Default for InMemoryRoutingProvider {
    fn default() -> Self {
        Self::new()
    }
}

impl IRoutingProvider for InMemoryRoutingProvider {
    fn provider_id(&self) -> String {
        "in-memory".to_string()
    }

    fn route(
        &self,
        from_lat: f64,
        from_lon: f64,
        to_lat: f64,
        to_lon: f64,
        mode: &str,
    ) -> RouteEstimate {
        let km = Self::haversine(from_lat, from_lon, to_lat, to_lon);
        let kph = match mode {
            "walk" => 5.0,
            "bike" => 18.0,
            "transit" => 30.0,
            _ => 60.0,
        };
        let hours = if kph <= 0.0 { 0.0 } else { km / kph };
        // TimeSpan.FromHours(hours) → whole milliseconds (chrono Duration is
        // integral); the C# TimeSpan carries sub-ms ticks, but the port keeps ms
        // resolution consistent with the rest of the crate.
        let duration = Duration::milliseconds((hours * 3_600_000.0).round() as i64);
        RouteEstimate::new(
            round_even(km, 3),
            duration,
            vec![(from_lat, from_lon), (to_lat, to_lon)],
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Home automation
// ─────────────────────────────────────────────────────────────────────────────

/// (Integration) A home-automation entity.
///
/// Mirrors `sealed record HaEntity(string EntityId, string Domain, string State,
/// string FriendlyName)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HaEntity {
    pub entity_id: String,
    pub domain: String,
    pub state: String,
    pub friendly_name: String,
}

impl HaEntity {
    /// Constructs an entity.
    pub fn new(
        entity_id: impl Into<String>,
        domain: impl Into<String>,
        state: impl Into<String>,
        friendly_name: impl Into<String>,
    ) -> Self {
        Self {
            entity_id: entity_id.into(),
            domain: domain.into(),
            state: state.into(),
            friendly_name: friendly_name.into(),
        }
    }
}

/// (Integration) A home-automation connector.
///
/// Mirrors `interface IHomeAutomationConnector`.
pub trait IHomeAutomationConnector {
    fn provider_id(&self) -> String;
    fn is_configured(&self) -> bool;
    /// All entities, ordered by entity id.
    fn list_entities(&self) -> Vec<HaEntity>;
    /// Calls a service against every entity in `domain` (case-insensitive):
    /// `turn_on`→`on`, `turn_off`→`off`, `toggle` flips on/off, anything else is a
    /// no-op.
    fn call_service(&self, domain: &str, service: &str);
}

/// (Integration) In-memory [`IHomeAutomationConnector`]: seeded entities;
/// turn_on/turn_off/toggle deterministically mutate matching-domain entity state.
///
/// Mirrors `sealed class InMemoryHomeAutomationConnector`.
pub struct InMemoryHomeAutomationConnector {
    entities: Mutex<HashMap<String, HaEntity>>,
}

impl InMemoryHomeAutomationConnector {
    /// Creates an empty connector.
    pub fn new() -> Self {
        Self {
            entities: Mutex::new(HashMap::new()),
        }
    }

    /// Creates a connector seeded with `seed`.
    pub fn seeded(seed: impl IntoIterator<Item = HaEntity>) -> Self {
        let mut map = HashMap::new();
        for e in seed {
            map.insert(e.entity_id.clone(), e);
        }
        Self {
            entities: Mutex::new(map),
        }
    }
}

impl Default for InMemoryHomeAutomationConnector {
    fn default() -> Self {
        Self::new()
    }
}

impl IHomeAutomationConnector for InMemoryHomeAutomationConnector {
    fn provider_id(&self) -> String {
        "in-memory".to_string()
    }

    fn is_configured(&self) -> bool {
        true
    }

    fn list_entities(&self) -> Vec<HaEntity> {
        let mut hits: Vec<HaEntity> = self.entities.lock().unwrap().values().cloned().collect();
        hits.sort_by(|a, b| a.entity_id.cmp(&b.entity_id));
        hits
    }

    fn call_service(&self, domain: &str, service: &str) {
        let mut entities = self.entities.lock().unwrap();
        for e in entities.values_mut() {
            if !e.domain.eq_ignore_ascii_case(domain) {
                continue;
            }
            let new_state = match service {
                "turn_on" => "on".to_string(),
                "turn_off" => "off".to_string(),
                "toggle" => {
                    if e.state == "on" {
                        "off".to_string()
                    } else {
                        "on".to_string()
                    }
                }
                _ => e.state.clone(),
            };
            e.state = new_state;
        }
    }
}
