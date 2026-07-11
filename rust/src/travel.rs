//! travel — CircleAI travel-board primitives.
//!
//! Full Rust port of `src/CircleAI.Travel/TravelPrimitives.cs`:
//!
//! - Records [`Flight`] / [`HotelStay`] / [`TravelTrip`], the [`ITravelBoard`]
//!   contract, and the deterministic in-memory [`InMemoryTravelBoard`] (flights +
//!   stays + trips + trip-cost aggregation + upcoming trips).
//!
//! Sync-only; `decimal Price/NightlyRate` → `f64`; `DateTimeOffset`/`DateTime` →
//! [`chrono::DateTime<Utc>`]. The two C# `Add` overloads become
//! [`ITravelBoard::add_flight`] and [`ITravelBoard::add_stay`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Travel) A flight leg.
///
/// Mirrors `sealed record Flight(string FlightId, string From, string To,
/// DateTimeOffset DepartUtc, DateTimeOffset ArriveUtc, string Carrier,
/// string Cabin, decimal Price, string Currency)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Flight {
    pub flight_id: String,
    pub from: String,
    pub to: String,
    pub depart_utc: DateTime<Utc>,
    pub arrive_utc: DateTime<Utc>,
    pub carrier: String,
    pub cabin: String,
    pub price: f64,
    pub currency: String,
}

impl Flight {
    /// Constructs a flight, mirroring the positional C# record constructor.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        flight_id: impl Into<String>,
        from: impl Into<String>,
        to: impl Into<String>,
        depart_utc: DateTime<Utc>,
        arrive_utc: DateTime<Utc>,
        carrier: impl Into<String>,
        cabin: impl Into<String>,
        price: f64,
        currency: impl Into<String>,
    ) -> Self {
        Self {
            flight_id: flight_id.into(),
            from: from.into(),
            to: to.into(),
            depart_utc,
            arrive_utc,
            carrier: carrier.into(),
            cabin: cabin.into(),
            price,
            currency: currency.into(),
        }
    }
}

/// (Travel) A hotel stay.
///
/// Mirrors `sealed record HotelStay(string StayId, string Hotel, string City,
/// DateTime CheckIn, DateTime CheckOut, decimal NightlyRate, string Currency)`.
#[derive(Debug, Clone, PartialEq)]
pub struct HotelStay {
    pub stay_id: String,
    pub hotel: String,
    pub city: String,
    pub check_in: DateTime<Utc>,
    pub check_out: DateTime<Utc>,
    pub nightly_rate: f64,
    pub currency: String,
}

impl HotelStay {
    /// Constructs a stay, mirroring the positional C# record constructor.
    pub fn new(
        stay_id: impl Into<String>,
        hotel: impl Into<String>,
        city: impl Into<String>,
        check_in: DateTime<Utc>,
        check_out: DateTime<Utc>,
        nightly_rate: f64,
        currency: impl Into<String>,
    ) -> Self {
        Self {
            stay_id: stay_id.into(),
            hotel: hotel.into(),
            city: city.into(),
            check_in,
            check_out,
            nightly_rate,
            currency: currency.into(),
        }
    }
}

/// (Travel) A planned trip.
///
/// Mirrors `sealed record TravelTrip(string TripId, string Name,
/// DateTime StartDate, DateTime EndDate, IReadOnlyList<string> FlightIds,
/// IReadOnlyList<string> StayIds)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TravelTrip {
    pub trip_id: String,
    pub name: String,
    pub start_date: DateTime<Utc>,
    pub end_date: DateTime<Utc>,
    pub flight_ids: Vec<String>,
    pub stay_ids: Vec<String>,
}

impl TravelTrip {
    /// Constructs a trip, mirroring the positional C# record constructor.
    pub fn new(
        trip_id: impl Into<String>,
        name: impl Into<String>,
        start_date: DateTime<Utc>,
        end_date: DateTime<Utc>,
        flight_ids: Vec<String>,
        stay_ids: Vec<String>,
    ) -> Self {
        Self {
            trip_id: trip_id.into(),
            name: name.into(),
            start_date,
            end_date,
            flight_ids,
            stay_ids,
        }
    }
}

/// (Travel) The travel-board contract.
///
/// Mirrors `interface ITravelBoard`.
pub trait ITravelBoard {
    /// Adds (or overwrites) a flight (C# `Add(Flight)`).
    fn add_flight(&self, f: Flight);
    /// Adds (or overwrites) a stay (C# `Add(HotelStay)`).
    fn add_stay(&self, s: HotelStay);
    /// Plans (or overwrites) a trip.
    fn plan(&self, t: TravelTrip);
    /// A trip by id, if any.
    fn get_trip(&self, id: &str) -> Option<TravelTrip>;
    /// A flight by id, if any.
    fn get_flight(&self, id: &str) -> Option<Flight>;
    /// A stay by id, if any.
    fn get_stay(&self, id: &str) -> Option<HotelStay>;
    /// Total cost of a trip: each known flight's price plus each known stay's
    /// `nightly_rate * max(1, nights)`. Panics on an unknown trip id (mirrors the
    /// C# `InvalidOperationException`).
    fn trip_cost(&self, trip_id: &str) -> f64;
    /// Trips starting at/after `now`, earliest first.
    fn upcoming_trips(&self, now: DateTime<Utc>) -> Vec<TravelTrip>;
}

/// (Travel) In-memory [`ITravelBoard`].
///
/// Mirrors `sealed class InMemoryTravelBoard`.
pub struct InMemoryTravelBoard {
    flights: Mutex<HashMap<String, Flight>>,
    stays: Mutex<HashMap<String, HotelStay>>,
    trips: Mutex<HashMap<String, TravelTrip>>,
}

impl InMemoryTravelBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            flights: Mutex::new(HashMap::new()),
            stays: Mutex::new(HashMap::new()),
            trips: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryTravelBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl ITravelBoard for InMemoryTravelBoard {
    fn add_flight(&self, f: Flight) {
        self.flights.lock().unwrap().insert(f.flight_id.clone(), f);
    }

    fn add_stay(&self, s: HotelStay) {
        self.stays.lock().unwrap().insert(s.stay_id.clone(), s);
    }

    fn plan(&self, t: TravelTrip) {
        self.trips.lock().unwrap().insert(t.trip_id.clone(), t);
    }

    fn get_trip(&self, id: &str) -> Option<TravelTrip> {
        self.trips.lock().unwrap().get(id).cloned()
    }

    fn get_flight(&self, id: &str) -> Option<Flight> {
        self.flights.lock().unwrap().get(id).cloned()
    }

    fn get_stay(&self, id: &str) -> Option<HotelStay> {
        self.stays.lock().unwrap().get(id).cloned()
    }

    fn trip_cost(&self, trip_id: &str) -> f64 {
        let trips = self.trips.lock().unwrap();
        let t = match trips.get(trip_id) {
            Some(t) => t.clone(),
            None => panic!("Unknown trip {trip_id}"),
        };
        drop(trips);
        let mut total = 0.0;
        let flights = self.flights.lock().unwrap();
        for fid in &t.flight_ids {
            if let Some(f) = flights.get(fid) {
                total += f.price;
            }
        }
        drop(flights);
        let stays = self.stays.lock().unwrap();
        for sid in &t.stay_ids {
            if let Some(s) = stays.get(sid) {
                // (CheckOut - CheckIn).Days — whole days, floored at 1.
                let nights = (s.check_out - s.check_in).num_days().max(1);
                total += s.nightly_rate * nights as f64;
            }
        }
        total
    }

    fn upcoming_trips(&self, now: DateTime<Utc>) -> Vec<TravelTrip> {
        let mut hits: Vec<TravelTrip> = self
            .trips
            .lock()
            .unwrap()
            .values()
            .filter(|t| t.start_date >= now)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.start_date.cmp(&b.start_date));
        hits
    }
}
