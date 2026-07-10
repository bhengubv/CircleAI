//! child_safety_primitives.rs
//!
//! (3.3.0) Real domain types + in-memory store for the Child Safety vertical —
//! Rust port of `src/CircleAI.Safety.Child/ChildSafetyPrimitives.cs`.
//!
//! Trusted-adult ring, geofences, check-in events. The C#
//! `ConcurrentDictionary<..>` maps and the `List<CheckIn>` + `object _lock`
//! collapse to `Mutex`-guarded collections; ordering reproduces the .NET
//! `OrderBy` / `OrderByDescending` (stable — ties keep insertion order). The
//! geofence test uses the same Haversine metres as the reference.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (3.3.0) A trusted adult in the child's safeguarding ring.
///
/// Mirrors `sealed record TrustedAdult(string AdultId, string Name, string Phone,
/// string Relationship, int RingPriority)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TrustedAdult {
    pub adult_id: String,
    pub name: String,
    pub phone: String,
    pub relationship: String,
    pub ring_priority: i32,
}

impl TrustedAdult {
    /// Constructs a trusted adult, mirroring the positional C# record constructor.
    pub fn new(
        adult_id: impl Into<String>,
        name: impl Into<String>,
        phone: impl Into<String>,
        relationship: impl Into<String>,
        ring_priority: i32,
    ) -> Self {
        Self {
            adult_id: adult_id.into(),
            name: name.into(),
            phone: phone.into(),
            relationship: relationship.into(),
            ring_priority,
        }
    }
}

/// (3.3.0) A circular geofence.
///
/// Mirrors `sealed record Geofence(string FenceId, string Name, double CentreLat,
/// double CentreLon, double RadiusMeters)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Geofence {
    pub fence_id: String,
    pub name: String,
    pub centre_lat: f64,
    pub centre_lon: f64,
    pub radius_meters: f64,
}

impl Geofence {
    /// Constructs a geofence, mirroring the positional C# record constructor.
    pub fn new(
        fence_id: impl Into<String>,
        name: impl Into<String>,
        centre_lat: f64,
        centre_lon: f64,
        radius_meters: f64,
    ) -> Self {
        Self {
            fence_id: fence_id.into(),
            name: name.into(),
            centre_lat,
            centre_lon,
            radius_meters,
        }
    }
}

/// (3.3.0) A child check-in event.
///
/// Mirrors `sealed record CheckIn(string ChildId, string Status, double? Lat,
/// double? Lon, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct CheckIn {
    pub child_id: String,
    pub status: String,
    pub lat: Option<f64>,
    pub lon: Option<f64>,
    pub at_utc: DateTime<Utc>,
}

impl CheckIn {
    /// Constructs a check-in, mirroring the positional C# record constructor.
    pub fn new(
        child_id: impl Into<String>,
        status: impl Into<String>,
        lat: Option<f64>,
        lon: Option<f64>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            child_id: child_id.into(),
            status: status.into(),
            lat,
            lon,
            at_utc,
        }
    }
}

/// (3.3.0) The Child Safety board contract.
///
/// Mirrors `interface IChildSafetyBoard`. The C# default `int limit = 20` is an
/// explicit argument here; callers pass `20` for the default. Passing `limit <= 0`
/// panics, matching the C# `ArgumentOutOfRangeException`.
pub trait IChildSafetyBoard {
    /// Adds (or replaces, by `adult_id`) a trusted adult.
    fn add_adult(&self, a: TrustedAdult);
    /// The trusted-adult ring ordered by ascending `ring_priority`.
    fn ring_ordered(&self) -> Vec<TrustedAdult>;
    /// Defines (or replaces, by `fence_id`) a geofence.
    fn define_geofence(&self, g: Geofence);
    /// Looks up a geofence by id.
    fn get_geofence(&self, id: &str) -> Option<Geofence>;
    /// True if `(lat, lon)` is within any defined fence's radius.
    fn is_inside_any_fence(&self, lat: f64, lon: f64) -> bool;
    /// Records a check-in.
    fn record_check_in(&self, c: CheckIn);
    /// The most-recent `limit` check-ins for `child_id`, newest-first.
    fn recent_check_ins(&self, child_id: &str, limit: usize) -> Vec<CheckIn>;
}

/// (3.3.0) In-memory [`IChildSafetyBoard`].
pub struct InMemoryChildSafetyBoard {
    adults: Mutex<HashMap<String, TrustedAdult>>,
    fences: Mutex<HashMap<String, Geofence>>,
    check_ins: Mutex<Vec<CheckIn>>,
}

impl InMemoryChildSafetyBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            adults: Mutex::new(HashMap::new()),
            fences: Mutex::new(HashMap::new()),
            check_ins: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryChildSafetyBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IChildSafetyBoard for InMemoryChildSafetyBoard {
    fn add_adult(&self, a: TrustedAdult) {
        self.adults.lock().unwrap().insert(a.adult_id.clone(), a);
    }

    fn ring_ordered(&self) -> Vec<TrustedAdult> {
        let mut out: Vec<TrustedAdult> = self.adults.lock().unwrap().values().cloned().collect();
        // Stable ascending by ring_priority (matches .NET OrderBy stability).
        out.sort_by(|a, b| a.ring_priority.cmp(&b.ring_priority));
        out
    }

    fn define_geofence(&self, g: Geofence) {
        self.fences.lock().unwrap().insert(g.fence_id.clone(), g);
    }

    fn get_geofence(&self, id: &str) -> Option<Geofence> {
        self.fences.lock().unwrap().get(id).cloned()
    }

    fn is_inside_any_fence(&self, lat: f64, lon: f64) -> bool {
        let fences = self.fences.lock().unwrap();
        for g in fences.values() {
            if haversine_meters(g.centre_lat, g.centre_lon, lat, lon) <= g.radius_meters {
                return true;
            }
        }
        false
    }

    fn record_check_in(&self, c: CheckIn) {
        self.check_ins.lock().unwrap().push(c);
    }

    fn recent_check_ins(&self, child_id: &str, limit: usize) -> Vec<CheckIn> {
        // The C# signature is `int limit`; `limit <= 0` throws. `usize` cannot be
        // negative, but `0` must still panic to match the reference.
        if limit == 0 {
            panic!("limit must be greater than zero");
        }
        let mut out: Vec<CheckIn> = self
            .check_ins
            .lock()
            .unwrap()
            .iter()
            .filter(|c| c.child_id == child_id)
            .cloned()
            .collect();
        out.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        out.truncate(limit);
        out
    }
}

/// Great-circle distance in metres between two `(lat, lon)` points — a direct
/// port of the reference `HaversineMeters` (`R = 6_371_000`, degrees→radians,
/// `2·atan2(√a, √(1−a))`).
pub fn haversine_meters(a_lat: f64, a_lon: f64, b_lat: f64, b_lon: f64) -> f64 {
    const R: f64 = 6_371_000.0;
    let deg_to_rad = |d: f64| d * std::f64::consts::PI / 180.0;
    let d_lat = deg_to_rad(b_lat - a_lat);
    let d_lon = deg_to_rad(b_lon - a_lon);
    let s1 = (d_lat / 2.0).sin();
    let s2 = (d_lon / 2.0).sin();
    let a = s1 * s1 + deg_to_rad(a_lat).cos() * deg_to_rad(b_lat).cos() * s2 * s2;
    let c = 2.0 * a.sqrt().atan2((1.0 - a).sqrt());
    R * c
}
