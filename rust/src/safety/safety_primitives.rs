//! safety_primitives.rs
//!
//! (3.3.0) Real domain types + in-memory store for the Safety vertical —
//! Rust port of `src/CircleAI.Safety/SafetyPrimitives.cs`.
//!
//! Incidents, hazards, emergency contacts, and severity-routing. The C#
//! `List<T>` + `object _lock` and the `ConcurrentDictionary<string, Hazard>`
//! collapse to `Mutex`-guarded collections here; ordering queries reproduce the
//! .NET `OrderByDescending` (a **stable** sort — ties keep insertion order).

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (3.3.0) Severity of a logged incident. Discriminants match the C# enum order
/// (`Info = 0 … Emergency = 3`) so `AtOrAboveSeverity` compares correctly.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum IncidentSeverity {
    Info = 0,
    Warning = 1,
    Critical = 2,
    Emergency = 3,
}

/// (3.3.0) A logged safety incident.
///
/// Mirrors `sealed record Incident(string IncidentId, IncidentSeverity Severity,
/// string Description, double? Latitude, double? Longitude, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Incident {
    pub incident_id: String,
    pub severity: IncidentSeverity,
    pub description: String,
    pub latitude: Option<f64>,
    pub longitude: Option<f64>,
    pub at_utc: DateTime<Utc>,
}

impl Incident {
    /// Constructs an incident, mirroring the positional C# record constructor.
    pub fn new(
        incident_id: impl Into<String>,
        severity: IncidentSeverity,
        description: impl Into<String>,
        latitude: Option<f64>,
        longitude: Option<f64>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            incident_id: incident_id.into(),
            severity,
            description: description.into(),
            latitude,
            longitude,
            at_utc,
        }
    }
}

/// (3.3.0) A noted hazard.
///
/// Mirrors `sealed record Hazard(string HazardId, string Description,
/// string Category, DateTimeOffset NotedUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Hazard {
    pub hazard_id: String,
    pub description: String,
    pub category: String,
    pub noted_utc: DateTime<Utc>,
}

impl Hazard {
    /// Constructs a hazard, mirroring the positional C# record constructor.
    pub fn new(
        hazard_id: impl Into<String>,
        description: impl Into<String>,
        category: impl Into<String>,
        noted_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            hazard_id: hazard_id.into(),
            description: description.into(),
            category: category.into(),
            noted_utc,
        }
    }
}

/// (3.3.0) An emergency contact.
///
/// Mirrors `sealed record EmergencyContact(string ContactId, string Name,
/// string Phone, string Relationship)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EmergencyContact {
    pub contact_id: String,
    pub name: String,
    pub phone: String,
    pub relationship: String,
}

impl EmergencyContact {
    /// Constructs a contact, mirroring the positional C# record constructor.
    pub fn new(
        contact_id: impl Into<String>,
        name: impl Into<String>,
        phone: impl Into<String>,
        relationship: impl Into<String>,
    ) -> Self {
        Self {
            contact_id: contact_id.into(),
            name: name.into(),
            phone: phone.into(),
            relationship: relationship.into(),
        }
    }
}

/// (3.3.0) The Safety board contract.
///
/// Mirrors `interface ISafetyBoard`. Getters become `*()` methods.
pub trait ISafetyBoard {
    /// Logs an incident.
    fn log(&self, i: Incident);
    /// All incidents, newest-first (by `at_utc`).
    fn active(&self) -> Vec<Incident>;
    /// Incidents at or above `minimum` severity, newest-first.
    fn at_or_above_severity(&self, minimum: IncidentSeverity) -> Vec<Incident>;
    /// Notes a hazard (keyed by `hazard_id`; a repeat id overwrites).
    fn note_hazard(&self, h: Hazard);
    /// All hazards, newest-first (by `noted_utc`).
    fn hazards(&self) -> Vec<Hazard>;
    /// Adds an emergency contact.
    fn add_contact(&self, c: EmergencyContact);
    /// The first-added contact, if any.
    fn first_contact(&self) -> Option<EmergencyContact>;
    /// All contacts, in insertion order.
    fn contacts(&self) -> Vec<EmergencyContact>;
}

/// (3.3.0) In-memory [`ISafetyBoard`].
pub struct InMemorySafetyBoard {
    incidents: Mutex<Vec<Incident>>,
    hazards: Mutex<HashMap<String, Hazard>>,
    contacts: Mutex<Vec<EmergencyContact>>,
}

impl InMemorySafetyBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            incidents: Mutex::new(Vec::new()),
            hazards: Mutex::new(HashMap::new()),
            contacts: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemorySafetyBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl ISafetyBoard for InMemorySafetyBoard {
    fn log(&self, i: Incident) {
        self.incidents.lock().unwrap().push(i);
    }

    fn active(&self) -> Vec<Incident> {
        let mut out: Vec<Incident> = self.incidents.lock().unwrap().clone();
        // Stable descending by at_utc (matches .NET OrderByDescending stability).
        out.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        out
    }

    fn at_or_above_severity(&self, minimum: IncidentSeverity) -> Vec<Incident> {
        let mut out: Vec<Incident> = self
            .incidents
            .lock()
            .unwrap()
            .iter()
            .filter(|i| i.severity >= minimum)
            .cloned()
            .collect();
        out.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        out
    }

    fn note_hazard(&self, h: Hazard) {
        self.hazards.lock().unwrap().insert(h.hazard_id.clone(), h);
    }

    fn hazards(&self) -> Vec<Hazard> {
        let mut out: Vec<Hazard> = self.hazards.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| b.noted_utc.cmp(&a.noted_utc));
        out
    }

    fn add_contact(&self, c: EmergencyContact) {
        self.contacts.lock().unwrap().push(c);
    }

    fn first_contact(&self) -> Option<EmergencyContact> {
        self.contacts.lock().unwrap().first().cloned()
    }

    fn contacts(&self) -> Vec<EmergencyContact> {
        self.contacts.lock().unwrap().clone()
    }
}
