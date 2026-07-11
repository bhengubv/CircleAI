//! iot_test.rs
//!
//! Ports the behaviour of `CircleAI.IoT`: device registry (name-ordered),
//! telemetry ingest + latest (NaN when none) + history (newest-first, limited),
//! command dispatch log (newest-first).

use chrono::{Duration, Utc};
use circle_ai::iot::{
    IIoTBoard, InMemoryIoTBoard, IoTCommand, IoTDevice, IoTTelemetry,
};

#[test]
fn devices_registered_and_name_ordered() {
    let board = InMemoryIoTBoard::new();
    assert!(board.get_device("d1").is_none());
    board.register(IoTDevice::new("d2", "Thermostat", "hvac", "1.0", Utc::now()));
    board.register(IoTDevice::new("d1", "Camera", "cam", "2.1", Utc::now()));
    let devices = board.devices();
    let names: Vec<&str> = devices.iter().map(|d| d.name.as_str()).collect();
    assert_eq!(names, vec!["Camera", "Thermostat"]);
}

#[test]
fn latest_value_and_history() {
    let board = InMemoryIoTBoard::new();
    assert!(board.latest_value("d1", "temp").is_nan());
    board.record_telemetry(IoTTelemetry::new("d1", "temp", 20.0, Utc::now() - Duration::hours(2)));
    board.record_telemetry(IoTTelemetry::new("d1", "temp", 22.0, Utc::now()));
    board.record_telemetry(IoTTelemetry::new("d1", "temp", 21.0, Utc::now() - Duration::hours(1)));
    board.record_telemetry(IoTTelemetry::new("d1", "humidity", 50.0, Utc::now()));

    assert_eq!(board.latest_value("d1", "temp"), 22.0);
    let hist = board.history("d1", "temp", 100);
    let vals: Vec<f64> = hist.iter().map(|t| t.value).collect();
    assert_eq!(vals, vec![22.0, 21.0, 20.0]); // newest-first
    assert_eq!(board.history("d1", "temp", 1).len(), 1);
}

#[test]
#[should_panic(expected = "limit")]
fn history_zero_limit_panics() {
    InMemoryIoTBoard::new().history("d1", "temp", 0);
}

#[test]
fn commands_logged_newest_first() {
    let board = InMemoryIoTBoard::new();
    board.send_command(IoTCommand::new("c1", "d1", "on", "{}", Utc::now() - Duration::hours(1)));
    board.send_command(IoTCommand::new("c2", "d1", "off", "{}", Utc::now()));
    board.send_command(IoTCommand::new("c3", "d2", "on", "{}", Utc::now()));

    let cmds = board.commands_for("d1");
    let ids: Vec<&str> = cmds.iter().map(|c| c.command_id.as_str()).collect();
    assert_eq!(ids, vec!["c2", "c1"]);
}
