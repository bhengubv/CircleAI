//! real_estate — CircleAI real-estate-board primitives.
//!
//! Full Rust port of `src/CircleAI.RealEstate/RealEstatePrimitives.cs`:
//!
//! - Enum [`PropertyKind`] + records ([`Property`], [`Listing`], [`Valuation`],
//!   [`Viewing`]) + [`IRealEstateBoard`] with the deterministic in-memory
//!   [`InMemoryRealEstateBoard`] (property registry, listings + close,
//!   valuations, viewings, active-in-suburb + suburb-average comparable).
//!
//! `decimal` money maps to [`f64`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (RealEstate) A property kind.
///
/// Mirrors `enum PropertyKind { Apartment, House, Townhouse, Commercial, Land }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum PropertyKind {
    Apartment,
    House,
    Townhouse,
    Commercial,
    Land,
}

/// (RealEstate) A property.
///
/// Mirrors `sealed record Property(string PropertyId, string Suburb,
/// PropertyKind Kind, int Beds, int Baths, double FloorAreaM2)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Property {
    pub property_id: String,
    pub suburb: String,
    pub kind: PropertyKind,
    pub beds: i32,
    pub baths: i32,
    pub floor_area_m2: f64,
}

impl Property {
    /// Constructs a property, mirroring the positional C# record constructor.
    pub fn new(
        property_id: impl Into<String>,
        suburb: impl Into<String>,
        kind: PropertyKind,
        beds: i32,
        baths: i32,
        floor_area_m2: f64,
    ) -> Self {
        Self {
            property_id: property_id.into(),
            suburb: suburb.into(),
            kind,
            beds,
            baths,
            floor_area_m2,
        }
    }
}

/// (RealEstate) A listing.
///
/// Mirrors `sealed record Listing(string ListingId, string PropertyId,
/// decimal AskingPrice, string Currency, DateTimeOffset ListedUtc,
/// bool IsActive)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Listing {
    pub listing_id: String,
    pub property_id: String,
    pub asking_price: f64,
    pub currency: String,
    pub listed_utc: DateTime<Utc>,
    pub is_active: bool,
}

impl Listing {
    /// Constructs a listing, mirroring the positional C# record constructor.
    pub fn new(
        listing_id: impl Into<String>,
        property_id: impl Into<String>,
        asking_price: f64,
        currency: impl Into<String>,
        listed_utc: DateTime<Utc>,
        is_active: bool,
    ) -> Self {
        Self {
            listing_id: listing_id.into(),
            property_id: property_id.into(),
            asking_price,
            currency: currency.into(),
            listed_utc,
            is_active,
        }
    }
}

/// (RealEstate) A valuation.
///
/// Mirrors `sealed record Valuation(string PropertyId, decimal EstimatedValue,
/// string Source, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Valuation {
    pub property_id: String,
    pub estimated_value: f64,
    pub source: String,
    pub at_utc: DateTime<Utc>,
}

impl Valuation {
    /// Constructs a valuation, mirroring the positional C# record constructor.
    pub fn new(
        property_id: impl Into<String>,
        estimated_value: f64,
        source: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            property_id: property_id.into(),
            estimated_value,
            source: source.into(),
            at_utc,
        }
    }
}

/// (RealEstate) A scheduled viewing.
///
/// Mirrors `sealed record Viewing(string ViewingId, string ListingId,
/// string AttendeeName, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Viewing {
    pub viewing_id: String,
    pub listing_id: String,
    pub attendee_name: String,
    pub at_utc: DateTime<Utc>,
}

impl Viewing {
    /// Constructs a viewing, mirroring the positional C# record constructor.
    pub fn new(
        viewing_id: impl Into<String>,
        listing_id: impl Into<String>,
        attendee_name: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            viewing_id: viewing_id.into(),
            listing_id: listing_id.into(),
            attendee_name: attendee_name.into(),
            at_utc,
        }
    }
}

/// (RealEstate) The real-estate board contract.
///
/// Mirrors `interface IRealEstateBoard`.
pub trait IRealEstateBoard {
    /// Registers (or overwrites) a property.
    fn register_property(&self, p: Property);
    /// Publishes (or overwrites) a listing.
    fn list(&self, l: Listing);
    /// Marks a listing inactive. Panics on an unknown listing id (mirrors the
    /// C# `InvalidOperationException`).
    fn close(&self, listing_id: &str);
    /// Records a valuation.
    fn value(&self, v: Valuation);
    /// Schedules a viewing.
    fn schedule_viewing(&self, v: Viewing);
    /// Active listings whose property is in `suburb` (case-insensitive), ordered
    /// by `listed_utc` descending.
    fn active_in_suburb(&self, suburb: &str) -> Vec<Listing>;
    /// The mean asking price of active listings in `suburb`, or `None` when there
    /// are none.
    fn suburb_average(&self, suburb: &str) -> Option<f64>;
}

/// (RealEstate) In-memory [`IRealEstateBoard`].
///
/// Mirrors `sealed class InMemoryRealEstateBoard`.
pub struct InMemoryRealEstateBoard {
    props: Mutex<HashMap<String, Property>>,
    listings: Mutex<HashMap<String, Listing>>,
    vals: Mutex<Vec<Valuation>>,
    viewings: Mutex<Vec<Viewing>>,
}

impl InMemoryRealEstateBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            props: Mutex::new(HashMap::new()),
            listings: Mutex::new(HashMap::new()),
            vals: Mutex::new(Vec::new()),
            viewings: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryRealEstateBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IRealEstateBoard for InMemoryRealEstateBoard {
    fn register_property(&self, p: Property) {
        self.props.lock().unwrap().insert(p.property_id.clone(), p);
    }

    fn list(&self, l: Listing) {
        self.listings.lock().unwrap().insert(l.listing_id.clone(), l);
    }

    fn close(&self, listing_id: &str) {
        let mut listings = self.listings.lock().unwrap();
        match listings.get(listing_id) {
            Some(l) => {
                let updated = Listing {
                    is_active: false,
                    ..l.clone()
                };
                listings.insert(listing_id.to_string(), updated);
            }
            None => panic!("Unknown listing {listing_id}"),
        }
    }

    fn value(&self, v: Valuation) {
        self.vals.lock().unwrap().push(v);
    }

    fn schedule_viewing(&self, v: Viewing) {
        self.viewings.lock().unwrap().push(v);
    }

    fn active_in_suburb(&self, suburb: &str) -> Vec<Listing> {
        if suburb.trim().is_empty() {
            panic!("suburb required");
        }
        let props = self.props.lock().unwrap();
        let listings = self.listings.lock().unwrap();
        let mut out: Vec<Listing> = listings
            .values()
            .filter(|l| {
                l.is_active
                    && props
                        .get(&l.property_id)
                        .is_some_and(|p| p.suburb.eq_ignore_ascii_case(suburb))
            })
            .cloned()
            .collect();
        // OrderByDescending(ListedUtc).
        out.sort_by(|a, b| b.listed_utc.cmp(&a.listed_utc));
        out
    }

    fn suburb_average(&self, suburb: &str) -> Option<f64> {
        let rows = self.active_in_suburb(suburb);
        if rows.is_empty() {
            return None;
        }
        let sum: f64 = rows.iter().map(|l| l.asking_price).sum();
        Some(sum / rows.len() as f64)
    }
}
