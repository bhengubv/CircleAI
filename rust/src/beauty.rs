//! beauty — CircleAI beauty-board primitives.
//!
//! Full Rust port of `src/CircleAI.Beauty/BeautyPrimitives.cs`:
//!
//! - Records [`Treatment`] / [`Appointment`] / [`SkinProfile`], the
//!   [`IBeautyBoard`] contract, and the deterministic in-memory
//!   [`InMemoryBeautyBoard`] (treatment catalogue + bookings + skin profiles +
//!   concern-based recommendations).
//!
//! Sync-only; `decimal Price` → `f64`; `DateTimeOffset` →
//! [`chrono::DateTime<Utc>`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Beauty) A bookable treatment.
///
/// Mirrors `sealed record Treatment(string TreatmentId, string Name,
/// int DurationMinutes, decimal Price, string Currency)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Treatment {
    pub treatment_id: String,
    pub name: String,
    pub duration_minutes: i32,
    pub price: f64,
    pub currency: String,
}

impl Treatment {
    /// Constructs a treatment, mirroring the positional C# record constructor.
    pub fn new(
        treatment_id: impl Into<String>,
        name: impl Into<String>,
        duration_minutes: i32,
        price: f64,
        currency: impl Into<String>,
    ) -> Self {
        Self {
            treatment_id: treatment_id.into(),
            name: name.into(),
            duration_minutes,
            price,
            currency: currency.into(),
        }
    }
}

/// (Beauty) A client appointment.
///
/// Mirrors `sealed record Appointment(string ApptId, string ClientName,
/// string TreatmentId, DateTimeOffset AtUtc, string? Notes)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Appointment {
    pub appt_id: String,
    pub client_name: String,
    pub treatment_id: String,
    pub at_utc: DateTime<Utc>,
    pub notes: Option<String>,
}

impl Appointment {
    /// Constructs an appointment, mirroring the positional C# record constructor.
    pub fn new(
        appt_id: impl Into<String>,
        client_name: impl Into<String>,
        treatment_id: impl Into<String>,
        at_utc: DateTime<Utc>,
        notes: Option<String>,
    ) -> Self {
        Self {
            appt_id: appt_id.into(),
            client_name: client_name.into(),
            treatment_id: treatment_id.into(),
            at_utc,
            notes,
        }
    }
}

/// (Beauty) A client skin profile.
///
/// Mirrors `sealed record SkinProfile(string ClientName, string SkinType,
/// IReadOnlyList<string> Concerns)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SkinProfile {
    pub client_name: String,
    pub skin_type: String,
    pub concerns: Vec<String>,
}

impl SkinProfile {
    /// Constructs a skin profile, mirroring the positional C# record constructor.
    pub fn new(
        client_name: impl Into<String>,
        skin_type: impl Into<String>,
        concerns: Vec<String>,
    ) -> Self {
        Self {
            client_name: client_name.into(),
            skin_type: skin_type.into(),
            concerns,
        }
    }
}

/// (Beauty) The beauty-board contract.
///
/// Mirrors `interface IBeautyBoard`.
pub trait IBeautyBoard {
    /// Adds (or overwrites) a treatment.
    fn add_treatment(&self, t: Treatment);
    /// A treatment by id, if any.
    fn get_treatment(&self, id: &str) -> Option<Treatment>;
    /// Books an appointment.
    fn book(&self, a: Appointment);
    /// Appointments in `[start, end]`, earliest first.
    fn appointments_between(&self, start: DateTime<Utc>, end: DateTime<Utc>) -> Vec<Appointment>;
    /// Saves (or overwrites) a skin profile.
    fn save_profile(&self, p: SkinProfile);
    /// A client's skin profile, if any.
    fn get_profile(&self, client_name: &str) -> Option<SkinProfile>;
    /// Treatments whose name contains one of the client's concerns
    /// (case-insensitive). Empty when the client has no profile.
    fn recommend_for(&self, client_name: &str) -> Vec<Treatment>;
}

/// (Beauty) In-memory [`IBeautyBoard`].
///
/// Mirrors `sealed class InMemoryBeautyBoard`.
pub struct InMemoryBeautyBoard {
    treatments: Mutex<HashMap<String, Treatment>>,
    appts: Mutex<Vec<Appointment>>,
    profiles: Mutex<HashMap<String, SkinProfile>>,
}

impl InMemoryBeautyBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            treatments: Mutex::new(HashMap::new()),
            appts: Mutex::new(Vec::new()),
            profiles: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryBeautyBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IBeautyBoard for InMemoryBeautyBoard {
    fn add_treatment(&self, t: Treatment) {
        self.treatments.lock().unwrap().insert(t.treatment_id.clone(), t);
    }

    fn get_treatment(&self, id: &str) -> Option<Treatment> {
        self.treatments.lock().unwrap().get(id).cloned()
    }

    fn book(&self, a: Appointment) {
        self.appts.lock().unwrap().push(a);
    }

    fn appointments_between(&self, start: DateTime<Utc>, end: DateTime<Utc>) -> Vec<Appointment> {
        let mut hits: Vec<Appointment> = self
            .appts
            .lock()
            .unwrap()
            .iter()
            .filter(|a| a.at_utc >= start && a.at_utc <= end)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        hits
    }

    fn save_profile(&self, p: SkinProfile) {
        self.profiles.lock().unwrap().insert(p.client_name.clone(), p);
    }

    fn get_profile(&self, client_name: &str) -> Option<SkinProfile> {
        self.profiles.lock().unwrap().get(client_name).cloned()
    }

    fn recommend_for(&self, client_name: &str) -> Vec<Treatment> {
        let profiles = self.profiles.lock().unwrap();
        let p = match profiles.get(client_name) {
            Some(p) => p.clone(),
            None => return Vec::new(),
        };
        drop(profiles);
        let concerns: Vec<String> = p.concerns.iter().map(|c| c.to_lowercase()).collect();
        self.treatments
            .lock()
            .unwrap()
            .values()
            .filter(|t| {
                let name = t.name.to_lowercase();
                concerns.iter().any(|c| name.contains(c))
            })
            .cloned()
            .collect()
    }
}

/// StubGuard parity additions — concrete-only helpers on the in-memory board
/// (mirroring the C# members added to `InMemoryBeautyBoard`/`IBeautyBoard`).
impl InMemoryBeautyBoard {
    /// Number of catalogued treatments. Mirrors `TreatmentCount`.
    pub fn treatment_count(&self) -> usize {
        self.treatments.lock().unwrap().len()
    }

    /// Cancels every appointment with `appt_id` (ordinal match). Returns `true`
    /// if at least one was removed. Mirrors `CancelAppointment`.
    pub fn cancel_appointment(&self, appt_id: &str) -> bool {
        let mut appts = self.appts.lock().unwrap();
        let before = appts.len();
        appts.retain(|a| a.appt_id != appt_id);
        appts.len() != before
    }

    /// A client's appointments (case-insensitive name), earliest first. Mirrors
    /// `AppointmentsForClient`.
    pub fn appointments_for_client(&self, client_name: &str) -> Vec<Appointment> {
        let mut hits: Vec<Appointment> = self
            .appts
            .lock()
            .unwrap()
            .iter()
            .filter(|a| a.client_name.eq_ignore_ascii_case(client_name))
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        hits
    }

    /// Treatments priced at or below `max_price`, cheapest first. Mirrors
    /// `TreatmentsUnder`.
    pub fn treatments_under(&self, max_price: f64) -> Vec<Treatment> {
        let mut hits: Vec<Treatment> = self
            .treatments
            .lock()
            .unwrap()
            .values()
            .filter(|t| t.price <= max_price)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.price.partial_cmp(&b.price).unwrap_or(std::cmp::Ordering::Equal));
        hits
    }

    /// The client's next appointment at/after `now` (case-insensitive name),
    /// earliest first, if any. Mirrors `NextAppointmentFor`.
    pub fn next_appointment_for(
        &self,
        client_name: &str,
        now: DateTime<Utc>,
    ) -> Option<Appointment> {
        let mut hits: Vec<Appointment> = self
            .appts
            .lock()
            .unwrap()
            .iter()
            .filter(|a| a.client_name.eq_ignore_ascii_case(client_name) && a.at_utc >= now)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        hits.into_iter().next()
    }

    /// Total scheduled revenue for appointments in `[start, end]` whose treatment
    /// is catalogued (sum of treatment prices). Mirrors `ScheduledRevenueBetween`.
    pub fn scheduled_revenue_between(&self, start: DateTime<Utc>, end: DateTime<Utc>) -> f64 {
        let treatments = self.treatments.lock().unwrap();
        self.appts
            .lock()
            .unwrap()
            .iter()
            .filter(|a| a.at_utc >= start && a.at_utc <= end)
            .filter_map(|a| treatments.get(&a.treatment_id).map(|t| t.price))
            .sum()
    }
}
