// home_board.go
//
// Ports the CircleAI.Home primitive vertical (HomePrimitives.cs):
//   Room / HomeDevice / MaintenanceTask (records) -> value structs
//   IHomeBoard        -> HomeBoard interface (I-prefix dropped)
//   InMemoryHomeBoard -> InMemoryHomeBoard
//
// The HomeDomainContext (static prompt strings) and HomeCompanionAdapter
// (LLM-prompt wrapper) are out of scope for the deterministic in-memory board.
//
// DETERMINISM: Rooms orders by Name (C# OrderBy(Name), culture-sensitive default
// comparer -> cultureLess). DevicesIn / ActiveDevices keep no defined C# order
// (ConcurrentDictionary values); this port sorts by DeviceId for stable output.
// UpcomingTasks orders by DueOn ascending (ties by TaskId). HomeDevice.RoomId is a
// pointer (nullable C# string?); DevicesIn matches devices whose RoomId equals the
// requested roomId (a device with no room never matches a concrete roomId).

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// Room is a room. Ports the Room record.
type Room struct {
	RoomId string
	Name   string
	AreaM2 float64
}

// HomeDevice is a smart-home device. Ports the HomeDevice record. RoomId is a
// pointer to mirror the nullable C# string? (nil == not assigned to a room).
type HomeDevice struct {
	DeviceId string
	Name     string
	Kind     string
	RoomId   *string
	IsOn     bool
}

// MaintenanceTask is a home maintenance task. Ports the MaintenanceTask record.
type MaintenanceTask struct {
	TaskId      string
	Description string
	DueOn       time.Time
	Completed   bool
}

// HomeBoard is the rooms/devices/maintenance board. Ports IHomeBoard. Rooms and
// ActiveDevices are exposed as methods.
type HomeBoard interface {
	AddRoom(r Room)
	GetRoom(id string) (Room, bool)
	// Rooms lists all rooms ordered by Name ascending.
	Rooms() []Room
	AddDevice(d HomeDevice)
	// Toggle sets a device on/off; errors if the id is unknown.
	Toggle(deviceId string, on bool) error
	// DevicesIn lists devices assigned to roomId.
	DevicesIn(roomId string) []HomeDevice
	// ActiveDevices lists devices that are currently on.
	ActiveDevices() []HomeDevice
	ScheduleTask(t MaintenanceTask)
	// CompleteTask marks a task done; errors if the id is unknown.
	CompleteTask(taskId string) error
	// UpcomingTasks lists incomplete tasks due at or before by, soonest first.
	UpcomingTasks(by time.Time) []MaintenanceTask
}

// InMemoryHomeBoard is a concurrency-safe in-memory HomeBoard. Ports
// InMemoryHomeBoard (rooms, devices, tasks each in a map).
type InMemoryHomeBoard struct {
	mu      sync.RWMutex
	rooms   map[string]Room
	devices map[string]HomeDevice
	tasks   map[string]MaintenanceTask
}

// NewInMemoryHomeBoard constructs an empty board.
func NewInMemoryHomeBoard() *InMemoryHomeBoard {
	return &InMemoryHomeBoard{
		rooms:   make(map[string]Room),
		devices: make(map[string]HomeDevice),
		tasks:   make(map[string]MaintenanceTask),
	}
}

// AddRoom stores (or replaces by RoomId) a room. Ports AddRoom.
func (b *InMemoryHomeBoard) AddRoom(r Room) {
	b.mu.Lock()
	b.rooms[r.RoomId] = r
	b.mu.Unlock()
}

// GetRoom returns the room for id and true, or (zero, false) if absent. Ports
// GetRoom.
func (b *InMemoryHomeBoard) GetRoom(id string) (Room, bool) {
	b.mu.RLock()
	r, ok := b.rooms[id]
	b.mu.RUnlock()
	return r, ok
}

// Rooms lists all rooms ordered by Name ascending. Ports the Rooms property
// (OrderBy(Name)).
func (b *InMemoryHomeBoard) Rooms() []Room {
	b.mu.RLock()
	out := make([]Room, 0, len(b.rooms))
	for _, r := range b.rooms {
		out = append(out, r)
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return cultureLess(out[i].Name, out[j].Name) })
	return out
}

// AddDevice stores (or replaces by DeviceId) a device. Ports AddDevice.
func (b *InMemoryHomeBoard) AddDevice(d HomeDevice) {
	b.mu.Lock()
	b.devices[d.DeviceId] = d
	b.mu.Unlock()
}

// Toggle sets a device's IsOn. Ports Toggle (throws on unknown id -> error).
func (b *InMemoryHomeBoard) Toggle(deviceId string, on bool) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	d, ok := b.devices[deviceId]
	if !ok {
		return errors.New("Unknown device " + deviceId)
	}
	d.IsOn = on
	b.devices[deviceId] = d
	return nil
}

// DevicesIn lists devices whose RoomId equals roomId, sorted by DeviceId for
// determinism. Ports DevicesIn (Where(d.RoomId == roomId)). A device with a nil
// RoomId never matches a concrete roomId.
func (b *InMemoryHomeBoard) DevicesIn(roomId string) []HomeDevice {
	b.mu.RLock()
	out := make([]HomeDevice, 0)
	for _, d := range b.devices {
		if d.RoomId != nil && *d.RoomId == roomId {
			out = append(out, d)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].DeviceId < out[j].DeviceId })
	return out
}

// ActiveDevices lists devices that are on, sorted by DeviceId for determinism.
// Ports the ActiveDevices property (Where(IsOn)).
func (b *InMemoryHomeBoard) ActiveDevices() []HomeDevice {
	b.mu.RLock()
	out := make([]HomeDevice, 0)
	for _, d := range b.devices {
		if d.IsOn {
			out = append(out, d)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].DeviceId < out[j].DeviceId })
	return out
}

// ScheduleTask stores (or replaces by TaskId) a maintenance task. Ports
// ScheduleTask.
func (b *InMemoryHomeBoard) ScheduleTask(t MaintenanceTask) {
	b.mu.Lock()
	b.tasks[t.TaskId] = t
	b.mu.Unlock()
}

// CompleteTask marks a task Completed. Ports CompleteTask (throws on unknown id
// -> error).
func (b *InMemoryHomeBoard) CompleteTask(taskId string) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	t, ok := b.tasks[taskId]
	if !ok {
		return errors.New("Unknown task " + taskId)
	}
	t.Completed = true
	b.tasks[taskId] = t
	return nil
}

// UpcomingTasks lists incomplete tasks due at or before by, ordered by DueOn
// ascending. Ports UpcomingTasks (Where(!Completed && DueOn <= by).OrderBy(DueOn)).
// Equal due dates break by TaskId for determinism.
func (b *InMemoryHomeBoard) UpcomingTasks(by time.Time) []MaintenanceTask {
	b.mu.RLock()
	out := make([]MaintenanceTask, 0)
	for _, t := range b.tasks {
		if !t.Completed && !t.DueOn.After(by) {
			out = append(out, t)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].DueOn.Equal(out[j].DueOn) {
			return out[i].DueOn.Before(out[j].DueOn)
		}
		return out[i].TaskId < out[j].TaskId
	})
	return out
}

// Interface guard.
var _ HomeBoard = (*InMemoryHomeBoard)(nil)
