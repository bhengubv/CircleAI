//! hr_test.rs
//!
//! Ports the behaviour of `CircleAI.HR`: hire / employee registry (name-ordered),
//! leave requests + decisions + pending filter, performance reviews + average
//! rating (0 when none).

use chrono::Utc;
use circle_ai::hr::{
    Employee, IHRBoard, InMemoryHRBoard, LeaveRequest, PerformanceReview,
};

#[test]
fn hire_get_and_employees_name_ordered() {
    let board = InMemoryHRBoard::new();
    assert!(board.get_employee("e1").is_none());
    board.hire(Employee::new("e2", "Zoe", "Eng", Utc::now(), 100.0, "USD"));
    board.hire(Employee::new("e1", "Ada", "Eng", Utc::now(), 120.0, "USD"));
    let employees = board.employees();
    let names: Vec<&str> = employees.iter().map(|e| e.name.as_str()).collect();
    assert_eq!(names, vec!["Ada", "Zoe"]);
    assert_eq!(board.get_employee("e1").unwrap().salary, 120.0);
}

#[test]
fn leave_request_decide_and_pending_filter() {
    let board = InMemoryHRBoard::new();
    board.request(LeaveRequest::new("r1", "e1", "Annual", Utc::now(), Utc::now(), "Pending"));
    board.request(LeaveRequest::new("r2", "e1", "Sick", Utc::now(), Utc::now(), "pending"));
    assert_eq!(board.pending_leaves().len(), 2);

    board.decide_leave("r1", "Approved");
    let pending = board.pending_leaves();
    let ids: Vec<&str> = pending.iter().map(|r| r.request_id.as_str()).collect();
    assert_eq!(ids, vec!["r2"]);
}

#[test]
#[should_panic(expected = "Unknown leave request")]
fn decide_unknown_leave_panics() {
    InMemoryHRBoard::new().decide_leave("nope", "Approved");
}

#[test]
fn avg_rating_averages_or_zero() {
    let board = InMemoryHRBoard::new();
    assert_eq!(board.avg_rating_for("e1"), 0.0);
    board.review(PerformanceReview::new("v1", "e1", Utc::now(), 4, "good"));
    board.review(PerformanceReview::new("v2", "e1", Utc::now(), 2, "ok"));
    board.review(PerformanceReview::new("v3", "e2", Utc::now(), 5, "great"));
    assert_eq!(board.avg_rating_for("e1"), 3.0);
    assert_eq!(board.avg_rating_for("e2"), 5.0);
}
