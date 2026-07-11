//! tourism — CircleAI tourism-board primitives.
//!
//! Full Rust port of `src/CircleAI.Tourism/TourismPrimitives.cs`:
//!
//! - Records [`Attraction`] / [`ItineraryItem`] / [`Itinerary`] /
//!   [`TourismBooking`], the [`ITourismBoard`] contract, and the deterministic
//!   in-memory [`InMemoryTourismBoard`] (attractions by city/tag + itineraries +
//!   bookings).
//!
//! Sync-only; `TimeSpan Start/EndLocal` → [`chrono::Duration`];
//! `decimal TotalPrice` → `f64`; `DateTime` → [`chrono::DateTime<Utc>`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Duration, Utc};

/// (Tourism) A visitable attraction.
///
/// Mirrors `sealed record Attraction(string AttractionId, string Name,
/// string City, string Country, double Lat, double Lon,
/// IReadOnlyList<string> Tags)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Attraction {
    pub attraction_id: String,
    pub name: String,
    pub city: String,
    pub country: String,
    pub lat: f64,
    pub lon: f64,
    pub tags: Vec<String>,
}

impl Attraction {
    /// Constructs an attraction, mirroring the positional C# record constructor.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        attraction_id: impl Into<String>,
        name: impl Into<String>,
        city: impl Into<String>,
        country: impl Into<String>,
        lat: f64,
        lon: f64,
        tags: Vec<String>,
    ) -> Self {
        Self {
            attraction_id: attraction_id.into(),
            name: name.into(),
            city: city.into(),
            country: country.into(),
            lat,
            lon,
            tags,
        }
    }
}

/// (Tourism) One scheduled item within an itinerary.
///
/// Mirrors `sealed record ItineraryItem(int DayIndex, TimeSpan StartLocal,
/// TimeSpan EndLocal, string AttractionId, string? Note)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ItineraryItem {
    pub day_index: i32,
    pub start_local: Duration,
    pub end_local: Duration,
    pub attraction_id: String,
    pub note: Option<String>,
}

impl ItineraryItem {
    /// Constructs an item, mirroring the positional C# record constructor.
    pub fn new(
        day_index: i32,
        start_local: Duration,
        end_local: Duration,
        attraction_id: impl Into<String>,
        note: Option<String>,
    ) -> Self {
        Self {
            day_index,
            start_local,
            end_local,
            attraction_id: attraction_id.into(),
            note,
        }
    }
}

/// (Tourism) A planned itinerary.
///
/// Mirrors `sealed record Itinerary(string ItineraryId, string Title,
/// IReadOnlyList<ItineraryItem> Items)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Itinerary {
    pub itinerary_id: String,
    pub title: String,
    pub items: Vec<ItineraryItem>,
}

impl Itinerary {
    /// Constructs an itinerary, mirroring the positional C# record constructor.
    pub fn new(itinerary_id: impl Into<String>, title: impl Into<String>, items: Vec<ItineraryItem>) -> Self {
        Self {
            itinerary_id: itinerary_id.into(),
            title: title.into(),
            items,
        }
    }
}

/// (Tourism) A confirmed booking of an itinerary.
///
/// Mirrors `sealed record TourismBooking(string BookingId, string ItineraryId,
/// DateTime StartDate, int Travelers, decimal TotalPrice, string Currency)`.
#[derive(Debug, Clone, PartialEq)]
pub struct TourismBooking {
    pub booking_id: String,
    pub itinerary_id: String,
    pub start_date: DateTime<Utc>,
    pub travelers: i32,
    pub total_price: f64,
    pub currency: String,
}

impl TourismBooking {
    /// Constructs a booking, mirroring the positional C# record constructor.
    pub fn new(
        booking_id: impl Into<String>,
        itinerary_id: impl Into<String>,
        start_date: DateTime<Utc>,
        travelers: i32,
        total_price: f64,
        currency: impl Into<String>,
    ) -> Self {
        Self {
            booking_id: booking_id.into(),
            itinerary_id: itinerary_id.into(),
            start_date,
            travelers,
            total_price,
            currency: currency.into(),
        }
    }
}

/// (Tourism) The tourism-board contract.
///
/// Mirrors `interface ITourismBoard`.
pub trait ITourismBoard {
    /// Adds (or overwrites) an attraction.
    fn add(&self, a: Attraction);
    /// Attractions in a city (case-insensitive), by name. Panics on blank city.
    fn attractions_in_city(&self, city: &str) -> Vec<Attraction>;
    /// Attractions carrying a tag (case-insensitive), by name. Panics on blank tag.
    fn by_tag(&self, tag: &str) -> Vec<Attraction>;
    /// Plans (or overwrites) an itinerary.
    fn plan(&self, i: Itinerary);
    /// An itinerary by id, if any.
    fn get_itinerary(&self, id: &str) -> Option<Itinerary>;
    /// Records a booking.
    fn book(&self, b: TourismBooking);
    /// All bookings (mirrors the C# `Bookings` property).
    fn bookings(&self) -> Vec<TourismBooking>;
}

/// (Tourism) In-memory [`ITourismBoard`].
///
/// Mirrors `sealed class InMemoryTourismBoard`.
pub struct InMemoryTourismBoard {
    attractions: Mutex<HashMap<String, Attraction>>,
    itineraries: Mutex<HashMap<String, Itinerary>>,
    bookings: Mutex<Vec<TourismBooking>>,
}

impl InMemoryTourismBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            attractions: Mutex::new(HashMap::new()),
            itineraries: Mutex::new(HashMap::new()),
            bookings: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryTourismBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl ITourismBoard for InMemoryTourismBoard {
    fn add(&self, a: Attraction) {
        self.attractions.lock().unwrap().insert(a.attraction_id.clone(), a);
    }

    fn attractions_in_city(&self, city: &str) -> Vec<Attraction> {
        if city.trim().is_empty() {
            panic!("city required");
        }
        let target = city.to_lowercase();
        let mut hits: Vec<Attraction> = self
            .attractions
            .lock()
            .unwrap()
            .values()
            .filter(|a| a.city.to_lowercase() == target)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.name.cmp(&b.name));
        hits
    }

    fn by_tag(&self, tag: &str) -> Vec<Attraction> {
        if tag.trim().is_empty() {
            panic!("tag required");
        }
        let target = tag.to_lowercase();
        let mut hits: Vec<Attraction> = self
            .attractions
            .lock()
            .unwrap()
            .values()
            .filter(|a| a.tags.iter().any(|t| t.to_lowercase() == target))
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.name.cmp(&b.name));
        hits
    }

    fn plan(&self, i: Itinerary) {
        self.itineraries.lock().unwrap().insert(i.itinerary_id.clone(), i);
    }

    fn get_itinerary(&self, id: &str) -> Option<Itinerary> {
        self.itineraries.lock().unwrap().get(id).cloned()
    }

    fn book(&self, b: TourismBooking) {
        self.bookings.lock().unwrap().push(b);
    }

    fn bookings(&self) -> Vec<TourismBooking> {
        self.bookings.lock().unwrap().clone()
    }
}
