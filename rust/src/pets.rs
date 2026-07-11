//! pets — CircleAI pets-board primitives.
//!
//! Full Rust port of `src/CircleAI.Pets/PetsPrimitives.cs`:
//!
//! - Records ([`Pet`], [`Vaccination`], [`WeightSample`], [`VetAppointment`]) +
//!   [`IPetsBoard`] with the deterministic in-memory [`InMemoryPetsBoard`] (pet
//!   registry, vaccination log, weight history, upcoming vet appointments).
//!
//! `DateTime DateOfBirth` (offset-less in the C#) maps to [`DateTime<Utc>`];
//! `DateTimeOffset?` maps to `Option<DateTime<Utc>>`. `UpcomingAppointments`
//! filters against [`Utc::now`], mirroring the C# `DateTimeOffset.UtcNow`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Pets) A pet.
///
/// Mirrors `sealed record Pet(string PetId, string Name, string Species,
/// string? Breed, DateTime DateOfBirth)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Pet {
    pub pet_id: String,
    pub name: String,
    pub species: String,
    pub breed: Option<String>,
    pub date_of_birth: DateTime<Utc>,
}

impl Pet {
    /// Constructs a pet, mirroring the positional C# record constructor.
    pub fn new(
        pet_id: impl Into<String>,
        name: impl Into<String>,
        species: impl Into<String>,
        breed: Option<String>,
        date_of_birth: DateTime<Utc>,
    ) -> Self {
        Self {
            pet_id: pet_id.into(),
            name: name.into(),
            species: species.into(),
            breed,
            date_of_birth,
        }
    }
}

/// (Pets) A vaccination record.
///
/// Mirrors `sealed record Vaccination(string PetId, string Vaccine,
/// DateTimeOffset AdministeredUtc, DateTimeOffset? BoosterDueUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Vaccination {
    pub pet_id: String,
    pub vaccine: String,
    pub administered_utc: DateTime<Utc>,
    pub booster_due_utc: Option<DateTime<Utc>>,
}

impl Vaccination {
    /// Constructs a vaccination, mirroring the positional C# record constructor.
    pub fn new(
        pet_id: impl Into<String>,
        vaccine: impl Into<String>,
        administered_utc: DateTime<Utc>,
        booster_due_utc: Option<DateTime<Utc>>,
    ) -> Self {
        Self {
            pet_id: pet_id.into(),
            vaccine: vaccine.into(),
            administered_utc,
            booster_due_utc,
        }
    }
}

/// (Pets) A weight sample.
///
/// Mirrors `sealed record WeightSample(string PetId, double WeightKg,
/// DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct WeightSample {
    pub pet_id: String,
    pub weight_kg: f64,
    pub at_utc: DateTime<Utc>,
}

impl WeightSample {
    /// Constructs a weight sample, mirroring the positional C# record constructor.
    pub fn new(pet_id: impl Into<String>, weight_kg: f64, at_utc: DateTime<Utc>) -> Self {
        Self {
            pet_id: pet_id.into(),
            weight_kg,
            at_utc,
        }
    }
}

/// (Pets) A vet appointment.
///
/// Mirrors `sealed record VetAppointment(string ApptId, string PetId,
/// string Reason, DateTimeOffset AtUtc, string Vet)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VetAppointment {
    pub appt_id: String,
    pub pet_id: String,
    pub reason: String,
    pub at_utc: DateTime<Utc>,
    pub vet: String,
}

impl VetAppointment {
    /// Constructs a vet appointment, mirroring the positional C# record constructor.
    pub fn new(
        appt_id: impl Into<String>,
        pet_id: impl Into<String>,
        reason: impl Into<String>,
        at_utc: DateTime<Utc>,
        vet: impl Into<String>,
    ) -> Self {
        Self {
            appt_id: appt_id.into(),
            pet_id: pet_id.into(),
            reason: reason.into(),
            at_utc,
            vet: vet.into(),
        }
    }
}

/// (Pets) The pets board contract.
///
/// Mirrors `interface IPetsBoard`.
pub trait IPetsBoard {
    /// Adds (or overwrites) a pet.
    fn add(&self, p: Pet);
    /// Looks up a pet by id.
    fn get_pet(&self, id: &str) -> Option<Pet>;
    /// All pets, ordered by name ascending.
    fn pets(&self) -> Vec<Pet>;
    /// Records a vaccination.
    fn record_vaccination(&self, v: Vaccination);
    /// Vaccinations for a pet, newest-first (by administered date).
    fn vaccinations_for(&self, pet_id: &str) -> Vec<Vaccination>;
    /// Records a weight sample.
    fn record_weight(&self, s: WeightSample);
    /// Weight history for a pet, oldest-first.
    fn weight_history(&self, pet_id: &str) -> Vec<WeightSample>;
    /// Schedules (or overwrites) a vet appointment.
    fn schedule(&self, a: VetAppointment);
    /// All appointments at/after now, ordered by time ascending.
    fn upcoming_appointments(&self) -> Vec<VetAppointment>;
}

/// (Pets) In-memory [`IPetsBoard`].
///
/// Mirrors `sealed class InMemoryPetsBoard`.
pub struct InMemoryPetsBoard {
    pets: Mutex<HashMap<String, Pet>>,
    vax: Mutex<Vec<Vaccination>>,
    weights: Mutex<Vec<WeightSample>>,
    appts: Mutex<HashMap<String, VetAppointment>>,
}

impl InMemoryPetsBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            pets: Mutex::new(HashMap::new()),
            vax: Mutex::new(Vec::new()),
            weights: Mutex::new(Vec::new()),
            appts: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryPetsBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IPetsBoard for InMemoryPetsBoard {
    fn add(&self, p: Pet) {
        self.pets.lock().unwrap().insert(p.pet_id.clone(), p);
    }

    fn get_pet(&self, id: &str) -> Option<Pet> {
        self.pets.lock().unwrap().get(id).cloned()
    }

    fn pets(&self) -> Vec<Pet> {
        let mut out: Vec<Pet> = self.pets.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }

    fn record_vaccination(&self, v: Vaccination) {
        self.vax.lock().unwrap().push(v);
    }

    fn vaccinations_for(&self, pet_id: &str) -> Vec<Vaccination> {
        let mut out: Vec<Vaccination> = self
            .vax
            .lock()
            .unwrap()
            .iter()
            .filter(|v| v.pet_id == pet_id)
            .cloned()
            .collect();
        out.sort_by(|a, b| b.administered_utc.cmp(&a.administered_utc));
        out
    }

    fn record_weight(&self, s: WeightSample) {
        self.weights.lock().unwrap().push(s);
    }

    fn weight_history(&self, pet_id: &str) -> Vec<WeightSample> {
        let mut out: Vec<WeightSample> = self
            .weights
            .lock()
            .unwrap()
            .iter()
            .filter(|w| w.pet_id == pet_id)
            .cloned()
            .collect();
        out.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        out
    }

    fn schedule(&self, a: VetAppointment) {
        self.appts.lock().unwrap().insert(a.appt_id.clone(), a);
    }

    fn upcoming_appointments(&self) -> Vec<VetAppointment> {
        let now = Utc::now();
        let mut out: Vec<VetAppointment> = self
            .appts
            .lock()
            .unwrap()
            .values()
            .filter(|a| a.at_utc >= now)
            .cloned()
            .collect();
        out.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        out
    }
}
