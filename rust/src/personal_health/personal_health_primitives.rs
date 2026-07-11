//! personal_health_primitives.rs
//!
//! (3.3.0) Real domain types + in-memory store for personal health — Rust port
//! of `src/CircleAI.Personal.Health/PersonalHealthPrimitives.cs`: vitals (BP,
//! glucose, weight …), allergies, medications, last-reading helpers.
//!
//! Privacy: instances are user-scoped and never written to a shared store.
//! Vitals live in a `Mutex<Vec>` (C# `List` + `object _lock`); allergies and
//! medications in `Mutex<HashMap>` (C# `ConcurrentDictionary`). `double Value`
//! → [`f64`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (3.3.0) The kind of a vital reading.
///
/// Mirrors `enum VitalKind { ... }`; discriminants match the C# declaration
/// order (`BloodPressureSystolic = 0 … StepsCount = 7`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum VitalKind {
    BloodPressureSystolic = 0,
    BloodPressureDiastolic = 1,
    GlucoseMgDl = 2,
    WeightKg = 3,
    HeartRateBpm = 4,
    TemperatureC = 5,
    OxygenPct = 6,
    StepsCount = 7,
}

/// (3.3.0) A single vital reading.
///
/// Mirrors `sealed record VitalReading(VitalKind Kind, double Value,
/// DateTimeOffset AtUtc, string? Note)`.
#[derive(Debug, Clone, PartialEq)]
pub struct VitalReading {
    pub kind: VitalKind,
    pub value: f64,
    pub at_utc: DateTime<Utc>,
    pub note: Option<String>,
}

impl VitalReading {
    /// Constructs a reading, mirroring the positional C# record constructor.
    pub fn new(kind: VitalKind, value: f64, at_utc: DateTime<Utc>, note: Option<String>) -> Self {
        Self {
            kind,
            value,
            at_utc,
            note,
        }
    }
}

/// (3.3.0) A recorded allergy.
///
/// Mirrors `sealed record Allergy(string AllergyId, string Substance,
/// string Severity)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Allergy {
    pub allergy_id: String,
    pub substance: String,
    pub severity: String,
}

impl Allergy {
    /// Constructs an allergy, mirroring the positional C# record constructor.
    pub fn new(
        allergy_id: impl Into<String>,
        substance: impl Into<String>,
        severity: impl Into<String>,
    ) -> Self {
        Self {
            allergy_id: allergy_id.into(),
            substance: substance.into(),
            severity: severity.into(),
        }
    }
}

/// (3.3.0) A medication.
///
/// Mirrors `sealed record Medication(string MedId, string Name, string Dose,
/// string Frequency, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Medication {
    pub med_id: String,
    pub name: String,
    pub dose: String,
    pub frequency: String,
    pub started_at_utc: DateTime<Utc>,
    pub ended_at_utc: Option<DateTime<Utc>>,
}

impl Medication {
    /// Constructs a medication, mirroring the positional C# record constructor.
    pub fn new(
        med_id: impl Into<String>,
        name: impl Into<String>,
        dose: impl Into<String>,
        frequency: impl Into<String>,
        started_at_utc: DateTime<Utc>,
        ended_at_utc: Option<DateTime<Utc>>,
    ) -> Self {
        Self {
            med_id: med_id.into(),
            name: name.into(),
            dose: dose.into(),
            frequency: frequency.into(),
            started_at_utc,
            ended_at_utc,
        }
    }
}

/// (3.3.0) The Personal Health board contract.
///
/// Mirrors `interface IPersonalHealthBoard`. The `Allergies` getter becomes
/// [`allergies`](IPersonalHealthBoard::allergies).
pub trait IPersonalHealthBoard {
    /// Records a vital reading.
    fn record(&self, v: VitalReading);
    /// Readings of a kind at or after `since`, oldest-first.
    fn read_since(&self, kind: VitalKind, since: DateTime<Utc>) -> Vec<VitalReading>;
    /// The newest reading of a kind, if any.
    fn latest(&self, kind: VitalKind) -> Option<VitalReading>;
    /// Adds (or overwrites) an allergy.
    fn add_allergy(&self, a: Allergy);
    /// All recorded allergies.
    fn allergies(&self) -> Vec<Allergy>;
    /// Adds (or overwrites) a medication.
    fn add_medication(&self, m: Medication);
    /// Ends a medication. Panics on an unknown id (C#
    /// `InvalidOperationException`).
    fn end_medication(&self, med_id: &str, ended_at_utc: DateTime<Utc>);
    /// Active (not-yet-ended) medications, ordered by name.
    fn active_medications(&self) -> Vec<Medication>;
}

/// (3.3.0) In-memory [`IPersonalHealthBoard`].
pub struct InMemoryPersonalHealthBoard {
    vitals: Mutex<Vec<VitalReading>>,
    allergies: Mutex<HashMap<String, Allergy>>,
    meds: Mutex<HashMap<String, Medication>>,
}

impl InMemoryPersonalHealthBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            vitals: Mutex::new(Vec::new()),
            allergies: Mutex::new(HashMap::new()),
            meds: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryPersonalHealthBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IPersonalHealthBoard for InMemoryPersonalHealthBoard {
    fn record(&self, v: VitalReading) {
        self.vitals.lock().unwrap().push(v);
    }

    fn read_since(&self, kind: VitalKind, since: DateTime<Utc>) -> Vec<VitalReading> {
        let mut out: Vec<VitalReading> = self
            .vitals
            .lock()
            .unwrap()
            .iter()
            .filter(|v| v.kind == kind && v.at_utc >= since)
            .cloned()
            .collect();
        out.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        out
    }

    fn latest(&self, kind: VitalKind) -> Option<VitalReading> {
        // C# `OrderByDescending(v => v.AtUtc).FirstOrDefault()` is a *stable*
        // descending sort then first — so among equal timestamps the
        // earliest-inserted wins. A strict `>` fold (keeping the first-seen on
        // ties) reproduces that; `Iterator::max_by` would keep the last, which
        // differs on ties.
        let vitals = self.vitals.lock().unwrap();
        let mut best: Option<&VitalReading> = None;
        for v in vitals.iter().filter(|v| v.kind == kind) {
            match best {
                Some(b) if b.at_utc >= v.at_utc => {}
                _ => best = Some(v),
            }
        }
        best.cloned()
    }

    fn add_allergy(&self, a: Allergy) {
        self.allergies
            .lock()
            .unwrap()
            .insert(a.allergy_id.clone(), a);
    }

    fn allergies(&self) -> Vec<Allergy> {
        self.allergies.lock().unwrap().values().cloned().collect()
    }

    fn add_medication(&self, m: Medication) {
        self.meds.lock().unwrap().insert(m.med_id.clone(), m);
    }

    fn end_medication(&self, med_id: &str, ended_at_utc: DateTime<Utc>) {
        let mut meds = self.meds.lock().unwrap();
        match meds.get(med_id) {
            Some(m) => {
                let updated = Medication {
                    ended_at_utc: Some(ended_at_utc),
                    ..m.clone()
                };
                meds.insert(med_id.to_string(), updated);
            }
            None => panic!("Unknown medication {med_id}"),
        }
    }

    fn active_medications(&self) -> Vec<Medication> {
        let mut out: Vec<Medication> = self
            .meds
            .lock()
            .unwrap()
            .values()
            .filter(|m| m.ended_at_utc.is_none())
            .cloned()
            .collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }
}
