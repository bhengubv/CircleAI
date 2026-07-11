//! home — CircleAI home-board primitives.
//!
//! Full Rust port of `src/CircleAI.Home/HomePrimitives.cs`:
//!
//! - Records ([`Room`], [`HomeDevice`], [`MaintenanceTask`]) + [`IHomeBoard`]
//!   with the deterministic in-memory [`InMemoryHomeBoard`] (rooms, devices +
//!   toggle, per-room + active device queries, maintenance tasks +
//!   completion + upcoming).
//!
//! The C# `ConcurrentDictionary` collapses to `Mutex`-guarded `HashMap`s. The
//! `DateTime DueOn` (offset-less in the C#) maps to [`DateTime<Utc>`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Home) A room.
///
/// Mirrors `sealed record Room(string RoomId, string Name, double AreaM2)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Room {
    pub room_id: String,
    pub name: String,
    pub area_m2: f64,
}

impl Room {
    /// Constructs a room, mirroring the positional C# record constructor.
    pub fn new(room_id: impl Into<String>, name: impl Into<String>, area_m2: f64) -> Self {
        Self {
            room_id: room_id.into(),
            name: name.into(),
            area_m2,
        }
    }
}

/// (Home) A home device.
///
/// Mirrors `sealed record HomeDevice(string DeviceId, string Name, string Kind,
/// string? RoomId, bool IsOn)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HomeDevice {
    pub device_id: String,
    pub name: String,
    pub kind: String,
    pub room_id: Option<String>,
    pub is_on: bool,
}

impl HomeDevice {
    /// Constructs a device, mirroring the positional C# record constructor.
    pub fn new(
        device_id: impl Into<String>,
        name: impl Into<String>,
        kind: impl Into<String>,
        room_id: Option<String>,
        is_on: bool,
    ) -> Self {
        Self {
            device_id: device_id.into(),
            name: name.into(),
            kind: kind.into(),
            room_id,
            is_on,
        }
    }
}

/// (Home) A maintenance task.
///
/// Mirrors `sealed record MaintenanceTask(string TaskId, string Description,
/// DateTime DueOn, bool Completed)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MaintenanceTask {
    pub task_id: String,
    pub description: String,
    pub due_on: DateTime<Utc>,
    pub completed: bool,
}

impl MaintenanceTask {
    /// Constructs a task, mirroring the positional C# record constructor.
    pub fn new(
        task_id: impl Into<String>,
        description: impl Into<String>,
        due_on: DateTime<Utc>,
        completed: bool,
    ) -> Self {
        Self {
            task_id: task_id.into(),
            description: description.into(),
            due_on,
            completed,
        }
    }
}

/// (Home) The home board contract.
///
/// Mirrors `interface IHomeBoard`.
pub trait IHomeBoard {
    /// Adds (or overwrites) a room.
    fn add_room(&self, r: Room);
    /// Looks up a room by id.
    fn get_room(&self, id: &str) -> Option<Room>;
    /// All rooms, ordered by name ascending.
    fn rooms(&self) -> Vec<Room>;
    /// Adds (or overwrites) a device.
    fn add_device(&self, d: HomeDevice);
    /// Sets a device's on/off state. Panics on an unknown device id (mirrors the
    /// C# `InvalidOperationException`).
    fn toggle(&self, device_id: &str, on: bool);
    /// Devices assigned to `room_id`.
    fn devices_in(&self, room_id: &str) -> Vec<HomeDevice>;
    /// All devices currently on.
    fn active_devices(&self) -> Vec<HomeDevice>;
    /// Schedules (or overwrites) a maintenance task.
    fn schedule_task(&self, t: MaintenanceTask);
    /// Marks a task complete. Panics on an unknown task id.
    fn complete_task(&self, task_id: &str);
    /// Incomplete tasks due at/before `by`, ordered by due date ascending.
    fn upcoming_tasks(&self, by: DateTime<Utc>) -> Vec<MaintenanceTask>;
}

/// (Home) In-memory [`IHomeBoard`].
///
/// Mirrors `sealed class InMemoryHomeBoard`.
pub struct InMemoryHomeBoard {
    rooms: Mutex<HashMap<String, Room>>,
    devices: Mutex<HashMap<String, HomeDevice>>,
    tasks: Mutex<HashMap<String, MaintenanceTask>>,
}

impl InMemoryHomeBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            rooms: Mutex::new(HashMap::new()),
            devices: Mutex::new(HashMap::new()),
            tasks: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryHomeBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IHomeBoard for InMemoryHomeBoard {
    fn add_room(&self, r: Room) {
        self.rooms.lock().unwrap().insert(r.room_id.clone(), r);
    }

    fn get_room(&self, id: &str) -> Option<Room> {
        self.rooms.lock().unwrap().get(id).cloned()
    }

    fn rooms(&self) -> Vec<Room> {
        let mut out: Vec<Room> = self.rooms.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }

    fn add_device(&self, d: HomeDevice) {
        self.devices.lock().unwrap().insert(d.device_id.clone(), d);
    }

    fn toggle(&self, device_id: &str, on: bool) {
        let mut devices = self.devices.lock().unwrap();
        match devices.get(device_id) {
            Some(d) => {
                let updated = HomeDevice {
                    is_on: on,
                    ..d.clone()
                };
                devices.insert(device_id.to_string(), updated);
            }
            None => panic!("Unknown device {device_id}"),
        }
    }

    fn devices_in(&self, room_id: &str) -> Vec<HomeDevice> {
        self.devices
            .lock()
            .unwrap()
            .values()
            .filter(|d| d.room_id.as_deref() == Some(room_id))
            .cloned()
            .collect()
    }

    fn active_devices(&self) -> Vec<HomeDevice> {
        self.devices
            .lock()
            .unwrap()
            .values()
            .filter(|d| d.is_on)
            .cloned()
            .collect()
    }

    fn schedule_task(&self, t: MaintenanceTask) {
        self.tasks.lock().unwrap().insert(t.task_id.clone(), t);
    }

    fn complete_task(&self, task_id: &str) {
        let mut tasks = self.tasks.lock().unwrap();
        match tasks.get(task_id) {
            Some(t) => {
                let updated = MaintenanceTask {
                    completed: true,
                    ..t.clone()
                };
                tasks.insert(task_id.to_string(), updated);
            }
            None => panic!("Unknown task {task_id}"),
        }
    }

    fn upcoming_tasks(&self, by: DateTime<Utc>) -> Vec<MaintenanceTask> {
        let mut out: Vec<MaintenanceTask> = self
            .tasks
            .lock()
            .unwrap()
            .values()
            .filter(|t| !t.completed && t.due_on <= by)
            .cloned()
            .collect();
        out.sort_by(|a, b| a.due_on.cmp(&b.due_on));
        out
    }
}
