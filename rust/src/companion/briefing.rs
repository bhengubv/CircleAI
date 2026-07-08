//! briefing.rs
//!
//! `ProactiveBriefingService` + `IBriefingNotifier` — the scheduled "what's
//! happening" briefing. Ported from `ProactiveBriefingService.cs`: assemble a
//! briefing from registered calendar / email / news / weather connectors, run it
//! through an LLM summariser, and push the result through every registered
//! notifier.
//!
//! The C# service is an `IHostedService` with a background delay loop; the two
//! load-bearing algorithms are ported exactly — [`time_until_next_fire`] (the
//! cron of fire-times-of-day) and [`ProactiveBriefingService::fire_once`] (the
//! assemble → summarise → deliver pass). The background loop itself is a host
//! concern in this sync port: a host ticks by computing the sleep from
//! [`ProactiveBriefingService::time_until_next_fire`] and calling `fire_once`.
//!
//! Integration connectors (`ICalendarConnector`, `IEmailConnector`,
//! `INewsSource`, `IWeatherProvider`) and the AI service live outside this crate,
//! so the small trait seams the service consumes are modelled here and injected;
//! nothing is stubbed.

use std::sync::Arc;

use chrono::{DateTime, Datelike, Duration, TimeZone, Utc};

// ─────────────────────────────────────────────────────────────────────────────
// Connector seams (modelled subset the briefing consumes).
// ─────────────────────────────────────────────────────────────────────────────

/// A calendar event in the briefing window.
#[derive(Debug, Clone, PartialEq)]
pub struct CalendarEvent {
    pub start_utc: DateTime<Utc>,
    pub title: String,
    pub location: Option<String>,
}

/// A calendar connector — lists events in a UTC window.
pub trait ICalendarConnector: Send + Sync {
    /// A stable provider identifier.
    fn provider_id(&self) -> String;
    /// Whether the connector is configured and should be consulted.
    fn is_configured(&self) -> bool;
    /// Events between `from_utc` and `to_utc`.
    fn list_events(&self, from_utc: DateTime<Utc>, to_utc: DateTime<Utc>) -> Vec<CalendarEvent>;
}

/// An unread email header.
#[derive(Debug, Clone, PartialEq)]
pub struct EmailHeader {
    pub from: String,
    pub subject: String,
}

/// An email connector — lists unread mail.
pub trait IEmailConnector: Send + Sync {
    fn provider_id(&self) -> String;
    fn is_configured(&self) -> bool;
    fn list_unread(&self, take: usize) -> Vec<EmailHeader>;
}

/// A news item.
#[derive(Debug, Clone, PartialEq)]
pub struct NewsItem {
    pub title: String,
}

/// A news source — fetches the latest items.
pub trait INewsSource: Send + Sync {
    fn source_id(&self) -> String;
    fn is_configured(&self) -> bool;
    fn fetch_latest(&self, take: usize) -> Vec<NewsItem>;
}

/// A current-weather reading.
#[derive(Debug, Clone, PartialEq)]
pub struct WeatherNow {
    pub temp_c: f64,
    pub condition: String,
    pub feels_like_c: f64,
    pub wind_kph: f64,
}

/// A weather provider — current conditions for a lat/lon.
pub trait IWeatherProvider: Send + Sync {
    fn provider_id(&self) -> String;
    fn current(&self, lat: f64, lon: f64) -> WeatherNow;
}

/// Summarises assembled briefing context via an LLM (injected). Returns `None`
/// to fall back to the raw context (mirrors the C# catch-and-send-raw path).
pub type BriefingSummariserFn = Arc<dyn Fn(&str) -> Option<String> + Send + Sync>;

/// Pluggable notifier — a host wires WhatsApp, Telegram, SMS, push, etc.
pub trait IBriefingNotifier: Send + Sync {
    /// Delivers `body` under `headline` to `address`.
    fn deliver(&self, headline: &str, body: &str, address: Option<&str>);
}

// ─────────────────────────────────────────────────────────────────────────────
// Options.
// ─────────────────────────────────────────────────────────────────────────────

/// Configuration knobs for [`ProactiveBriefingService`].
#[derive(Debug, Clone)]
pub struct ProactiveBriefingOptions {
    /// UTC times-of-day at which to fire, as offsets from midnight.
    pub fire_times_utc: Vec<Duration>,
    /// Latitude for weather lookup. `None` = skip weather.
    pub latitude: Option<f64>,
    /// Longitude for weather lookup. `None` = skip weather.
    pub longitude: Option<f64>,
    /// Headline used by the notifier.
    pub headline: String,
    /// Where to deliver — E.164 for SMS/WhatsApp, channel id for Telegram, etc.
    pub delivery_address: Option<String>,
}

impl Default for ProactiveBriefingOptions {
    fn default() -> Self {
        Self {
            // 06:30 and 18:00 UTC.
            fire_times_utc: vec![
                Duration::hours(6) + Duration::minutes(30),
                Duration::hours(18),
            ],
            latitude: None,
            longitude: None,
            headline: "Your briefing".to_string(),
            delivery_address: None,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Service.
// ─────────────────────────────────────────────────────────────────────────────

/// The proactive briefing service. Holds registered connectors + notifiers and
/// an optional summariser; [`fire_once`](Self::fire_once) assembles and delivers
/// one briefing.
#[derive(Default)]
pub struct ProactiveBriefingService {
    calendars: Vec<Arc<dyn ICalendarConnector>>,
    emails: Vec<Arc<dyn IEmailConnector>>,
    news: Vec<Arc<dyn INewsSource>>,
    weather: Option<Arc<dyn IWeatherProvider>>,
    notifiers: Vec<Arc<dyn IBriefingNotifier>>,
    summariser: Option<BriefingSummariserFn>,
    opts: ProactiveBriefingOptions,
}

impl ProactiveBriefingService {
    /// Creates a service with the given options and no connectors yet.
    pub fn new(opts: ProactiveBriefingOptions) -> Self {
        Self {
            calendars: Vec::new(),
            emails: Vec::new(),
            news: Vec::new(),
            weather: None,
            notifiers: Vec::new(),
            summariser: None,
            opts,
        }
    }

    /// Registers a calendar connector (builder-style).
    pub fn with_calendar(mut self, c: Arc<dyn ICalendarConnector>) -> Self {
        self.calendars.push(c);
        self
    }

    /// Registers an email connector.
    pub fn with_email(mut self, c: Arc<dyn IEmailConnector>) -> Self {
        self.emails.push(c);
        self
    }

    /// Registers a news source.
    pub fn with_news(mut self, c: Arc<dyn INewsSource>) -> Self {
        self.news.push(c);
        self
    }

    /// Sets the weather provider.
    pub fn with_weather(mut self, w: Arc<dyn IWeatherProvider>) -> Self {
        self.weather = Some(w);
        self
    }

    /// Registers a notifier.
    pub fn with_notifier(mut self, n: Arc<dyn IBriefingNotifier>) -> Self {
        self.notifiers.push(n);
        self
    }

    /// Sets the LLM summariser.
    pub fn with_summariser(mut self, s: BriefingSummariserFn) -> Self {
        self.summariser = Some(s);
        self
    }

    /// Time until the next configured fire moment relative to `now`. Always
    /// pushes a candidate that is within 30 s of `now` to the next day, so a tick
    /// never double-fires. 1:1 with the C# `TimeUntilNextFire`.
    pub fn time_until_next_fire(&self, now: DateTime<Utc>) -> Duration {
        if self.opts.fire_times_utc.is_empty() {
            return Duration::hours(1);
        }
        let today_base = Utc
            .with_ymd_and_hms(now.year_ce().1 as i32, now.month(), now.day(), 0, 0, 0)
            .single()
            .unwrap_or(now);
        let mut best: Option<Duration> = None;
        for tod in &self.opts.fire_times_utc {
            let mut candidate = today_base + *tod;
            if candidate <= now + Duration::seconds(30) {
                candidate += Duration::days(1);
            }
            let gap = candidate - now;
            if best.is_none() || gap < best.unwrap() {
                best = Some(gap);
            }
        }
        best.unwrap_or_else(|| Duration::hours(1))
    }

    /// Assembles the briefing context, summarises it, and delivers it through
    /// every notifier. No-op when no signals are available. 1:1 with the C#
    /// `FireOnceAsync`.
    pub fn fire_once(&self) {
        let now = Utc::now();
        let mut ctx_parts: Vec<String> = Vec::new();

        // Calendar — next 24 hours.
        for cal in self.calendars.iter().filter(|c| c.is_configured()) {
            let mut events = cal.list_events(now, now + Duration::hours(24));
            if !events.is_empty() {
                ctx_parts.push(format!("### Calendar ({})", cal.provider_id()));
                events.sort_by(|a, b| a.start_utc.cmp(&b.start_utc));
                for e in events.into_iter().take(8) {
                    let loc = match &e.location {
                        Some(l) if !l.is_empty() => format!(" @ {l}"),
                        _ => String::new(),
                    };
                    ctx_parts.push(format!("- {} {}{}", e.start_utc.format("%H:%M"), e.title, loc));
                }
            }
        }

        // Email — unread.
        for em in self.emails.iter().filter(|c| c.is_configured()) {
            let unread = em.list_unread(5);
            if !unread.is_empty() {
                ctx_parts.push(format!("### Unread email ({})", em.provider_id()));
                for m in unread {
                    ctx_parts.push(format!("- {}: {}", m.from, m.subject));
                }
            }
        }

        // News — latest from each source.
        for src in self.news.iter().filter(|s| s.is_configured()) {
            let items = src.fetch_latest(5);
            if !items.is_empty() {
                ctx_parts.push(format!("### News ({})", src.source_id()));
                for i in items {
                    ctx_parts.push(format!("- {}", i.title));
                }
            }
        }

        // Weather — if location configured.
        if let (Some(weather), Some(lat), Some(lon)) =
            (&self.weather, self.opts.latitude, self.opts.longitude)
        {
            let w = weather.current(lat, lon);
            ctx_parts.push(format!("### Weather ({})", weather.provider_id()));
            ctx_parts.push(format!(
                "- {:.0}\u{00B0}C {}, feels {:.0}\u{00B0}C, wind {:.0} km/h",
                w.temp_c, w.condition, w.feels_like_c, w.wind_kph
            ));
        }

        if ctx_parts.is_empty() {
            return;
        }

        let context = ctx_parts.join("\n");
        let prompt = format!(
            "Summarise the user's morning briefing in 80 words or less. Warm but factual. \
             End with the one thing they should do first today.\n\n{context}"
        );

        let summary = match &self.summariser {
            Some(s) => s(&prompt).unwrap_or_else(|| context.clone()),
            None => context.clone(),
        };

        for notifier in &self.notifiers {
            notifier.deliver(
                &self.opts.headline,
                &summary,
                self.opts.delivery_address.as_deref(),
            );
        }
    }
}
