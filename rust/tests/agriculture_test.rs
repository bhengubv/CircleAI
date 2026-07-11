//! agriculture_test.rs
//!
//! Ports the behaviour of `CircleAI.Agriculture`: fields + crops (ordered by
//! plant date) + yield log + case-insensitive average-yield-by-variety join.

use chrono::{TimeZone, Utc};
use circle_ai::agriculture::{Crop, Field, IFarmBoard, InMemoryFarmBoard, YieldRecord};

#[test]
fn fields_and_crops_ordered_by_plant_date() {
    let board = InMemoryFarmBoard::new();
    board.add_field(Field::new("f1", 12.5, "Loam", "Drip"));
    let d = |m| Utc.with_ymd_and_hms(2026, m, 1, 0, 0, 0).unwrap();
    board.plant(Crop::new("c2", "f1", "Maize", d(4), None));
    board.plant(Crop::new("c1", "f1", "Maize", d(2), None));
    board.plant(Crop::new("c3", "f2", "Wheat", d(3), None));

    assert_eq!(board.get_field("f1").unwrap().area_ha, 12.5);
    let crops = board.crops_for_field("f1");
    assert_eq!(crops.len(), 2);
    assert_eq!(crops[0].crop_id, "c1"); // earliest planted first
    assert_eq!(crops[1].crop_id, "c2");
}

#[test]
fn avg_yield_of_variety_case_insensitive() {
    let board = InMemoryFarmBoard::new();
    let d = |m| Utc.with_ymd_and_hms(2026, m, 1, 0, 0, 0).unwrap();
    board.plant(Crop::new("c1", "f1", "Maize", d(1), None));
    board.plant(Crop::new("c2", "f1", "maize", d(1), None));
    board.plant(Crop::new("c3", "f1", "Wheat", d(1), None));
    board.record_yield(YieldRecord::new("c1", 8.0, d(6)));
    board.record_yield(YieldRecord::new("c2", 10.0, d(6)));
    board.record_yield(YieldRecord::new("c3", 4.0, d(6)));

    // "MAIZE" matches c1 + c2 → (8 + 10) / 2 = 9.
    assert!((board.avg_yield_of_variety("MAIZE") - 9.0).abs() < 1e-9);
    // Unknown variety → 0.0.
    assert!((board.avg_yield_of_variety("Sorghum") - 0.0).abs() < 1e-9);
}
