//! business_test.rs
//!
//! Ports the behaviour of `CircleAI.Business`: unit tree, KPI samples + latest
//! lookup (NaN when none), quarter targets + achievement ratio (NaN when no
//! target).

use chrono::{Duration, Utc};
use circle_ai::business::{
    BusinessUnit, IBusinessBoard, InMemoryBusinessBoard, KpiSample, QuarterTarget,
};

#[test]
fn units_add_get_children() {
    let board = InMemoryBusinessBoard::new();
    board.add(BusinessUnit::new("root", "Group", "", vec!["rev".into()]));
    board.add(BusinessUnit::new("a", "Alpha", "root", vec![]));
    board.add(BusinessUnit::new("b", "Beta", "root", vec![]));
    board.add(BusinessUnit::new("c", "Gamma", "a", vec![]));

    assert_eq!(board.get_unit("a").unwrap().name, "Alpha");
    let kids = board.children_of("root");
    let mut ids: Vec<&str> = kids.iter().map(|u| u.unit_id.as_str()).collect();
    ids.sort_unstable();
    assert_eq!(ids, vec!["a", "b"]);
}

#[test]
fn latest_kpi_returns_most_recent_or_nan() {
    let board = InMemoryBusinessBoard::new();
    assert!(board.latest_kpi("a", "rev").is_nan());
    board.record(KpiSample::new("a", "rev", 10.0, Utc::now() - Duration::hours(2)));
    board.record(KpiSample::new("a", "rev", 30.0, Utc::now()));
    board.record(KpiSample::new("a", "rev", 20.0, Utc::now() - Duration::hours(1)));
    board.record(KpiSample::new("a", "cost", 5.0, Utc::now()));
    assert_eq!(board.latest_kpi("a", "rev"), 30.0);
    assert_eq!(board.latest_kpi("a", "cost"), 5.0);
}

#[test]
fn target_achievement_ratio_or_nan() {
    let board = InMemoryBusinessBoard::new();
    // no target → NaN.
    assert!(board.target_achievement("a", "rev", 2026, 2).is_nan());

    board.record(KpiSample::new("a", "rev", 80.0, Utc::now()));
    board.set_target(QuarterTarget::new("a", "rev", 2026, 2, 100.0));
    assert_eq!(board.target_achievement("a", "rev", 2026, 2), 0.8);

    // zero target → NaN.
    board.set_target(QuarterTarget::new("a", "rev", 2026, 3, 0.0));
    assert!(board.target_achievement("a", "rev", 2026, 3).is_nan());
}
