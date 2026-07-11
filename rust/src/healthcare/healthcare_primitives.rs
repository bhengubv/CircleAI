//! healthcare_primitives.rs
//!
//! (3.3.0) Real domain types + in-memory store for the Healthcare vertical —
//! Rust port of `src/CircleAI.Healthcare/HealthcarePrimitives.cs`.
//!
//! Patients, appointments, prescriptions. The C#
//! `ConcurrentDictionary<string, T>` collapses to `Mutex`-guarded `HashMap`s
//! here; the `AppointmentsFor` / `PrescriptionsFor` ordering queries reproduce
//! the .NET `OrderBy` / `OrderByDescending` (stable — ties keep insertion
//! order).

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, NaiveDate, Utc};

/// (3.3.0) A registered patient.
///
/// Mirrors `sealed record Patient(string PatientId, string Name,
/// DateTime DateOfBirth)`. `DateOfBirth` is a date-only value in C#; a
/// [`NaiveDate`] is the faithful Rust analogue.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Patient {
    pub patient_id: String,
    pub name: String,
    pub date_of_birth: NaiveDate,
}

impl Patient {
    /// Constructs a patient, mirroring the positional C# record constructor.
    pub fn new(
        patient_id: impl Into<String>,
        name: impl Into<String>,
        date_of_birth: NaiveDate,
    ) -> Self {
        Self {
            patient_id: patient_id.into(),
            name: name.into(),
            date_of_birth,
        }
    }
}

/// (3.3.0) A scheduled healthcare appointment.
///
/// Mirrors `sealed record HealthAppointment(string ApptId, string PatientId,
/// string Provider, DateTimeOffset AtUtc, string Status)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HealthAppointment {
    pub appt_id: String,
    pub patient_id: String,
    pub provider: String,
    pub at_utc: DateTime<Utc>,
    pub status: String,
}

impl HealthAppointment {
    /// Constructs an appointment, mirroring the positional C# record constructor.
    pub fn new(
        appt_id: impl Into<String>,
        patient_id: impl Into<String>,
        provider: impl Into<String>,
        at_utc: DateTime<Utc>,
        status: impl Into<String>,
    ) -> Self {
        Self {
            appt_id: appt_id.into(),
            patient_id: patient_id.into(),
            provider: provider.into(),
            at_utc,
            status: status.into(),
        }
    }
}

/// (3.3.0) A prescription.
///
/// Mirrors `sealed record Prescription(string RxId, string PatientId,
/// string MedicationName, string Dose, string Frequency,
/// DateTimeOffset PrescribedUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Prescription {
    pub rx_id: String,
    pub patient_id: String,
    pub medication_name: String,
    pub dose: String,
    pub frequency: String,
    pub prescribed_utc: DateTime<Utc>,
}

impl Prescription {
    /// Constructs a prescription, mirroring the positional C# record constructor.
    pub fn new(
        rx_id: impl Into<String>,
        patient_id: impl Into<String>,
        medication_name: impl Into<String>,
        dose: impl Into<String>,
        frequency: impl Into<String>,
        prescribed_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            rx_id: rx_id.into(),
            patient_id: patient_id.into(),
            medication_name: medication_name.into(),
            dose: dose.into(),
            frequency: frequency.into(),
            prescribed_utc,
        }
    }
}

/// (3.3.0) The Healthcare board contract.
///
/// Mirrors `interface IHealthcareBoard`.
pub trait IHealthcareBoard {
    /// Registers (or overwrites) a patient.
    fn register(&self, p: Patient);
    /// Looks up a patient by id.
    fn get_patient(&self, id: &str) -> Option<Patient>;
    /// Schedules (or overwrites) an appointment.
    fn schedule(&self, a: HealthAppointment);
    /// Updates an appointment's status. Panics on an unknown appointment id
    /// (mirrors the C# `InvalidOperationException`).
    fn update_status(&self, appt_id: &str, status: &str);
    /// Appointments for a patient, ordered by `at_utc` ascending.
    fn appointments_for(&self, patient_id: &str) -> Vec<HealthAppointment>;
    /// Records (or overwrites) a prescription.
    fn prescribe(&self, r: Prescription);
    /// Prescriptions for a patient, ordered by `prescribed_utc` descending.
    fn prescriptions_for(&self, patient_id: &str) -> Vec<Prescription>;
}

/// (3.3.0) In-memory [`IHealthcareBoard`].
pub struct InMemoryHealthcareBoard {
    patients: Mutex<HashMap<String, Patient>>,
    appts: Mutex<HashMap<String, HealthAppointment>>,
    rx: Mutex<HashMap<String, Prescription>>,
}

impl InMemoryHealthcareBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            patients: Mutex::new(HashMap::new()),
            appts: Mutex::new(HashMap::new()),
            rx: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryHealthcareBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IHealthcareBoard for InMemoryHealthcareBoard {
    fn register(&self, p: Patient) {
        self.patients.lock().unwrap().insert(p.patient_id.clone(), p);
    }

    fn get_patient(&self, id: &str) -> Option<Patient> {
        self.patients.lock().unwrap().get(id).cloned()
    }

    fn schedule(&self, a: HealthAppointment) {
        self.appts.lock().unwrap().insert(a.appt_id.clone(), a);
    }

    fn update_status(&self, appt_id: &str, status: &str) {
        let mut appts = self.appts.lock().unwrap();
        match appts.get(appt_id) {
            Some(a) => {
                let updated = HealthAppointment {
                    status: status.to_string(),
                    ..a.clone()
                };
                appts.insert(appt_id.to_string(), updated);
            }
            None => panic!("Unknown appointment {appt_id}"),
        }
    }

    fn appointments_for(&self, patient_id: &str) -> Vec<HealthAppointment> {
        let mut out: Vec<HealthAppointment> = self
            .appts
            .lock()
            .unwrap()
            .values()
            .filter(|a| a.patient_id == patient_id)
            .cloned()
            .collect();
        out.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        out
    }

    fn prescribe(&self, r: Prescription) {
        self.rx.lock().unwrap().insert(r.rx_id.clone(), r);
    }

    fn prescriptions_for(&self, patient_id: &str) -> Vec<Prescription> {
        let mut out: Vec<Prescription> = self
            .rx
            .lock()
            .unwrap()
            .values()
            .filter(|p| p.patient_id == patient_id)
            .cloned()
            .collect();
        out.sort_by(|a, b| b.prescribed_utc.cmp(&a.prescribed_utc));
        out
    }
}
