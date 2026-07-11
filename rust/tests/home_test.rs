//! home_test.rs
//!
//! Ports the behaviour of `CircleAI.Home`: rooms (name-ordered), devices +
//! toggle, per-room + active device queries, maintenance tasks + completion +
//! upcoming (due-ordered).

use chrono::{Duration, Utc};
use circle_ai::home::{
    HomeDevice, IHomeBoard, InMemoryHomeBoard, MaintenanceTask, Room,
};

#[test]
fn rooms_added_and_name_ordered() {
    let board = InMemoryHomeBoard::new();
    assert!(board.get_room("r1").is_none());
    board.add_room(Room::new("r2", "Kitchen", 12.0));
    board.add_room(Room::new("r1", "Bedroom", 15.0));
    let rooms = board.rooms();
    let names: Vec<&str> = rooms.iter().map(|r| r.name.as_str()).collect();
    assert_eq!(names, vec!["Bedroom", "Kitchen"]);
}

#[test]
fn devices_toggle_and_room_and_active_queries() {
    let board = InMemoryHomeBoard::new();
    board.add_device(HomeDevice::new("d1", "Lamp", "light", Some("r1".into()), false));
    board.add_device(HomeDevice::new("d2", "Fan", "fan", Some("r1".into()), true));
    board.add_device(HomeDevice::new("d3", "TV", "media", None, false));

    let in_r1 = board.devices_in("r1");
    assert_eq!(in_r1.len(), 2);
    assert_eq!(board.active_devices().len(), 1);

    board.toggle("d1", true);
    assert_eq!(board.active_devices().len(), 2);
}

#[test]
#[should_panic(expected = "Unknown device")]
fn toggle_unknown_panics() {
    InMemoryHomeBoard::new().toggle("nope", true);
}

#[test]
fn tasks_complete_and_upcoming_due_ordered() {
    let board = InMemoryHomeBoard::new();
    let now = Utc::now();
    board.schedule_task(MaintenanceTask::new("t1", "Filter", now + Duration::days(3), false));
    board.schedule_task(MaintenanceTask::new("t2", "Gutter", now + Duration::days(1), false));
    board.schedule_task(MaintenanceTask::new("t3", "Far", now + Duration::days(30), false));
    board.schedule_task(MaintenanceTask::new("t4", "Done", now + Duration::days(2), true));

    let by = now + Duration::days(5);
    let upcoming = board.upcoming_tasks(by);
    let ids: Vec<&str> = upcoming.iter().map(|t| t.task_id.as_str()).collect();
    assert_eq!(ids, vec!["t2", "t1"]); // due-ordered, far + completed excluded

    board.complete_task("t2");
    let after = board.upcoming_tasks(by);
    let ids: Vec<&str> = after.iter().map(|t| t.task_id.as_str()).collect();
    assert_eq!(ids, vec!["t1"]);
}

#[test]
#[should_panic(expected = "Unknown task")]
fn complete_unknown_task_panics() {
    InMemoryHomeBoard::new().complete_task("nope");
}
