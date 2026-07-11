//! construction_test.rs
//!
//! Ports the behaviour of `CircleAI.Construction`: projects + tasks (open,
//! ordered by due) + cost tracking + remaining budget.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::construction::{
    ConstructionTask, CostEntry, IConstructionBoard, InMemoryConstructionBoard, Project,
};

#[test]
fn open_tasks_ordered_by_due() {
    let board = InMemoryConstructionBoard::new();
    let start = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.create(Project::new("p1", "House", start, None, 1_000_000.0, "ZAR"));
    board.add(ConstructionTask::new("t2", "p1", "Roof", start + Duration::days(30), false));
    board.add(ConstructionTask::new("t1", "p1", "Foundation", start + Duration::days(10), false));
    board.add(ConstructionTask::new("t3", "p1", "Slab", start + Duration::days(20), false));

    board.complete("t3");
    let open = board.open_construction_tasks_for("p1");
    assert_eq!(open.len(), 2);
    assert_eq!(open[0].construction_task_id, "t1"); // earliest due first
    assert_eq!(open[1].construction_task_id, "t2");
}

#[test]
#[should_panic(expected = "Unknown task")]
fn complete_unknown_task_panics() {
    InMemoryConstructionBoard::new().complete("nope");
}

#[test]
fn spend_and_remaining_budget() {
    let board = InMemoryConstructionBoard::new();
    let start = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.create(Project::new("p1", "House", start, None, 100_000.0, "ZAR"));
    board.record_cost(CostEntry::new("c1", "p1", "Materials", 30_000.0, start));
    board.record_cost(CostEntry::new("c2", "p1", "Labour", 20_000.0, start));

    assert!((board.spend_for("p1") - 50_000.0).abs() < 1e-6);
    assert!((board.remaining_budget("p1") - 50_000.0).abs() < 1e-6);
}

#[test]
#[should_panic(expected = "Unknown project")]
fn remaining_budget_unknown_project_panics() {
    InMemoryConstructionBoard::new().remaining_budget("nope");
}
