//! pets_test.rs
//!
//! Ports the behaviour of `CircleAI.Pets`: pet registry (name-ordered),
//! vaccination log (newest-first), weight history (oldest-first), and upcoming
//! vet appointments (future-only, time-ordered).

use chrono::{Duration, Utc};
use circle_ai::pets::{
    IPetsBoard, InMemoryPetsBoard, Pet, Vaccination, VetAppointment, WeightSample,
};

#[test]
fn pets_added_and_name_ordered() {
    let board = InMemoryPetsBoard::new();
    assert!(board.get_pet("p1").is_none());
    board.add(Pet::new("p2", "Rex", "Dog", Some("Lab".into()), Utc::now()));
    board.add(Pet::new("p1", "Milo", "Cat", None, Utc::now()));
    let pets = board.pets();
    let names: Vec<&str> = pets.iter().map(|p| p.name.as_str()).collect();
    assert_eq!(names, vec!["Milo", "Rex"]);
}

#[test]
fn vaccinations_newest_first() {
    let board = InMemoryPetsBoard::new();
    board.record_vaccination(Vaccination::new("p1", "Rabies", Utc::now() - Duration::days(365), None));
    board.record_vaccination(Vaccination::new("p1", "Booster", Utc::now(), Some(Utc::now() + Duration::days(365))));
    board.record_vaccination(Vaccination::new("p2", "Rabies", Utc::now(), None));

    let vax = board.vaccinations_for("p1");
    let names: Vec<&str> = vax.iter().map(|v| v.vaccine.as_str()).collect();
    assert_eq!(names, vec!["Booster", "Rabies"]);
}

#[test]
fn weight_history_oldest_first() {
    let board = InMemoryPetsBoard::new();
    board.record_weight(WeightSample::new("p1", 5.0, Utc::now()));
    board.record_weight(WeightSample::new("p1", 4.0, Utc::now() - Duration::days(30)));
    board.record_weight(WeightSample::new("p2", 9.0, Utc::now()));

    let hist = board.weight_history("p1");
    let weights: Vec<f64> = hist.iter().map(|w| w.weight_kg).collect();
    assert_eq!(weights, vec![4.0, 5.0]); // oldest-first
}

#[test]
fn upcoming_appointments_future_only_time_ordered() {
    let board = InMemoryPetsBoard::new();
    let now = Utc::now();
    board.schedule(VetAppointment::new("a1", "p1", "Checkup", now + Duration::days(3), "Dr V"));
    board.schedule(VetAppointment::new("a2", "p1", "Shots", now + Duration::days(1), "Dr V"));
    board.schedule(VetAppointment::new("a3", "p2", "Past", now - Duration::days(1), "Dr V")); // past

    let up = board.upcoming_appointments();
    let ids: Vec<&str> = up.iter().map(|a| a.appt_id.as_str()).collect();
    assert_eq!(ids, vec!["a2", "a1"]);
}
