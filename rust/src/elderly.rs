//! elderly — CircleAI elderly-care-board primitives.
//!
//! Full Rust port of `src/CircleAI.Elderly/ElderlyPrimitives.cs`:
//!
//! - Records ([`CarePlan`], [`MedReminder`], [`CheckIn`]) + [`IElderlyCareBoard`]
//!   with the deterministic in-memory [`InMemoryElderlyCareBoard`] (care plans
//!   keyed by resident, medication reminders + deactivation, check-in log +
//!   missed-check-in detection).
//!
//! The C# `TimeSpan DailyAt` (a time-of-day) maps to [`chrono::Duration`].
//! `CheckIn` is re-exported at the crate root as `ElderlyCheckIn` to avoid
//! clashing with `safety_child::CheckIn`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Duration, Utc};

/// (Elderly) A resident care plan.
///
/// Mirrors `sealed record CarePlan(string PlanId, string ResidentName,
/// IReadOnlyList<string> MedicalConditions, IReadOnlyList<string> Allergies,
/// string CarerNotes)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CarePlan {
    pub plan_id: String,
    pub resident_name: String,
    pub medical_conditions: Vec<String>,
    pub allergies: Vec<String>,
    pub carer_notes: String,
}

impl CarePlan {
    /// Constructs a care plan, mirroring the positional C# record constructor.
    pub fn new(
        plan_id: impl Into<String>,
        resident_name: impl Into<String>,
        medical_conditions: Vec<String>,
        allergies: Vec<String>,
        carer_notes: impl Into<String>,
    ) -> Self {
        Self {
            plan_id: plan_id.into(),
            resident_name: resident_name.into(),
            medical_conditions,
            allergies,
            carer_notes: carer_notes.into(),
        }
    }
}

/// (Elderly) A daily medication reminder.
///
/// Mirrors `sealed record MedReminder(string ReminderId, string ResidentName,
/// string Medication, TimeSpan DailyAt, bool Active)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MedReminder {
    pub reminder_id: String,
    pub resident_name: String,
    pub medication: String,
    pub daily_at: Duration,
    pub active: bool,
}

impl MedReminder {
    /// Constructs a reminder, mirroring the positional C# record constructor.
    pub fn new(
        reminder_id: impl Into<String>,
        resident_name: impl Into<String>,
        medication: impl Into<String>,
        daily_at: Duration,
        active: bool,
    ) -> Self {
        Self {
            reminder_id: reminder_id.into(),
            resident_name: resident_name.into(),
            medication: medication.into(),
            daily_at,
            active,
        }
    }
}

/// (Elderly) A resident check-in.
///
/// Mirrors `sealed record CheckIn(string CheckInId, string ResidentName,
/// DateTimeOffset AtUtc, string Status, string? Note)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckIn {
    pub check_in_id: String,
    pub resident_name: String,
    pub at_utc: DateTime<Utc>,
    pub status: String,
    pub note: Option<String>,
}

impl CheckIn {
    /// Constructs a check-in, mirroring the positional C# record constructor.
    pub fn new(
        check_in_id: impl Into<String>,
        resident_name: impl Into<String>,
        at_utc: DateTime<Utc>,
        status: impl Into<String>,
        note: Option<String>,
    ) -> Self {
        Self {
            check_in_id: check_in_id.into(),
            resident_name: resident_name.into(),
            at_utc,
            status: status.into(),
            note,
        }
    }
}

/// (Elderly) The elderly-care board contract.
///
/// Mirrors `interface IElderlyCareBoard`.
pub trait IElderlyCareBoard {
    /// Sets (or overwrites) a resident's care plan.
    fn set_plan(&self, p: CarePlan);
    /// The care plan for a resident, if any.
    fn get_plan(&self, resident: &str) -> Option<CarePlan>;
    /// Adds (or overwrites) a medication reminder.
    fn add_reminder(&self, r: MedReminder);
    /// Deactivates a reminder. Panics on an unknown reminder id (mirrors the C#
    /// `InvalidOperationException`).
    fn deactivate_reminder(&self, reminder_id: &str);
    /// Active reminders for a resident.
    fn active_reminders_for(&self, resident: &str) -> Vec<MedReminder>;
    /// Records a check-in.
    fn record_check_in(&self, c: CheckIn);
    /// The most-recent check-in for a resident, if any.
    fn latest_check_in(&self, resident: &str) -> Option<CheckIn>;
    /// `true` when the resident has no check-in at/after `since`.
    fn missed_check_in(&self, resident: &str, since: DateTime<Utc>) -> bool;
}

/// (Elderly) In-memory [`IElderlyCareBoard`].
///
/// Mirrors `sealed class InMemoryElderlyCareBoard`.
pub struct InMemoryElderlyCareBoard {
    plans: Mutex<HashMap<String, CarePlan>>,
    reminders: Mutex<HashMap<String, MedReminder>>,
    check_ins: Mutex<Vec<CheckIn>>,
}

impl InMemoryElderlyCareBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            plans: Mutex::new(HashMap::new()),
            reminders: Mutex::new(HashMap::new()),
            check_ins: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryElderlyCareBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IElderlyCareBoard for InMemoryElderlyCareBoard {
    fn set_plan(&self, p: CarePlan) {
        self.plans.lock().unwrap().insert(p.resident_name.clone(), p);
    }

    fn get_plan(&self, resident: &str) -> Option<CarePlan> {
        self.plans.lock().unwrap().get(resident).cloned()
    }

    fn add_reminder(&self, r: MedReminder) {
        self.reminders.lock().unwrap().insert(r.reminder_id.clone(), r);
    }

    fn deactivate_reminder(&self, reminder_id: &str) {
        let mut reminders = self.reminders.lock().unwrap();
        match reminders.get(reminder_id) {
            Some(r) => {
                let updated = MedReminder {
                    active: false,
                    ..r.clone()
                };
                reminders.insert(reminder_id.to_string(), updated);
            }
            None => panic!("Unknown reminder {reminder_id}"),
        }
    }

    fn active_reminders_for(&self, resident: &str) -> Vec<MedReminder> {
        self.reminders
            .lock()
            .unwrap()
            .values()
            .filter(|r| r.resident_name == resident && r.active)
            .cloned()
            .collect()
    }

    fn record_check_in(&self, c: CheckIn) {
        self.check_ins.lock().unwrap().push(c);
    }

    fn latest_check_in(&self, resident: &str) -> Option<CheckIn> {
        let check_ins = self.check_ins.lock().unwrap();
        // C# `OrderByDescending(AtUtc).FirstOrDefault()` — stable, so among equal
        // timestamps the earliest-inserted wins.
        let mut best: Option<&CheckIn> = None;
        for c in check_ins.iter().filter(|c| c.resident_name == resident) {
            match best {
                Some(b) if c.at_utc > b.at_utc => best = Some(c),
                None => best = Some(c),
                _ => {}
            }
        }
        best.cloned()
    }

    fn missed_check_in(&self, resident: &str, since: DateTime<Utc>) -> bool {
        match self.latest_check_in(resident) {
            None => true,
            Some(latest) => latest.at_utc < since,
        }
    }
}
