// iot_board.go
//
// Ports the CircleAI.IoT primitive vertical (IoTPrimitives.cs):
//   IoTDevice / IoTTelemetry / IoTCommand (records) -> value structs
//   IIoTBoard        -> IoTBoard interface (I-prefix dropped)
//   InMemoryIoTBoard -> InMemoryIoTBoard
//
// The IoTCompanionPipeline (LLM-prompt wrapper) is out of scope for the
// deterministic in-memory board.
//
// DETERMINISM: Devices orders by Name (C# OrderBy(Name), culture-sensitive
// default comparer -> cultureLess). LatestValue returns double.NaN when there is
// no matching telemetry (exact AtUtc ties resolve to the first-inserted, matching
// the C# stable OrderByDescending.FirstOrDefault). History orders by AtUtc descending, capped at limit
// (ArgumentOutOfRange on limit <= 0). CommandsFor orders by SentUtc descending.
// Telemetry and commands live in ordered lists guarded by the mutex; equal
// timestamps break by a stable descending sort (source order preserved).

package circleai

import (
	"errors"
	"math"
	"sort"
	"sync"
	"time"
)

// DefaultIoTHistoryLimit is the C# default `limit = 100` for telemetry history.
const DefaultIoTHistoryLimit = 100

// IoTDevice is an IoT device. Ports the IoTDevice record.
type IoTDevice struct {
	DeviceId        string
	Name            string
	Kind            string
	FirmwareVersion string
	LastSeenUtc     time.Time
}

// IoTTelemetry is a telemetry reading. Ports the IoTTelemetry record.
type IoTTelemetry struct {
	DeviceId string
	Metric   string
	Value    float64
	AtUtc    time.Time
}

// IoTCommand is a command sent to a device. Ports the IoTCommand record.
type IoTCommand struct {
	CommandId     string
	DeviceId      string
	Action        string
	ArgumentsJson string
	SentUtc       time.Time
}

// IoTBoard is the devices/telemetry/commands board. Ports IIoTBoard. Devices is
// exposed as a method.
type IoTBoard interface {
	Register(d IoTDevice)
	GetDevice(id string) (IoTDevice, bool)
	// Devices lists all devices ordered by Name ascending.
	Devices() []IoTDevice
	RecordTelemetry(t IoTTelemetry)
	// LatestValue is the most recent Value for (deviceId, metric), or NaN if none.
	LatestValue(deviceId, metric string) float64
	// History lists telemetry for (deviceId, metric), newest first, capped at limit.
	History(deviceId, metric string, limit int) ([]IoTTelemetry, error)
	SendCommand(c IoTCommand)
	// CommandsFor lists commands sent to deviceId, newest first.
	CommandsFor(deviceId string) []IoTCommand
}

// InMemoryIoTBoard is a concurrency-safe in-memory IoTBoard. Ports
// InMemoryIoTBoard (devices in a map; telemetry + commands in ordered lists
// guarded by the mutex).
type InMemoryIoTBoard struct {
	mu        sync.RWMutex
	devices   map[string]IoTDevice
	telemetry []IoTTelemetry
	commands  []IoTCommand
}

// NewInMemoryIoTBoard constructs an empty board.
func NewInMemoryIoTBoard() *InMemoryIoTBoard {
	return &InMemoryIoTBoard{
		devices:   make(map[string]IoTDevice),
		telemetry: make([]IoTTelemetry, 0),
		commands:  make([]IoTCommand, 0),
	}
}

// Register stores (or replaces by DeviceId) a device. Ports Register.
func (b *InMemoryIoTBoard) Register(d IoTDevice) {
	b.mu.Lock()
	b.devices[d.DeviceId] = d
	b.mu.Unlock()
}

// GetDevice returns the device for id and true, or (zero, false) if absent. Ports
// GetDevice.
func (b *InMemoryIoTBoard) GetDevice(id string) (IoTDevice, bool) {
	b.mu.RLock()
	d, ok := b.devices[id]
	b.mu.RUnlock()
	return d, ok
}

// Devices lists all devices ordered by Name ascending. Ports the Devices property
// (OrderBy(Name)).
func (b *InMemoryIoTBoard) Devices() []IoTDevice {
	b.mu.RLock()
	out := make([]IoTDevice, 0, len(b.devices))
	for _, d := range b.devices {
		out = append(out, d)
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return cultureLess(out[i].Name, out[j].Name) })
	return out
}

// RecordTelemetry appends a telemetry reading. Ports RecordTelemetry.
func (b *InMemoryIoTBoard) RecordTelemetry(t IoTTelemetry) {
	b.mu.Lock()
	b.telemetry = append(b.telemetry, t)
	b.mu.Unlock()
}

// LatestValue returns the Value of the most recent telemetry for (deviceId,
// metric) by AtUtc, or NaN when there is none. Ports LatestValue
// (OrderByDescending(AtUtc).FirstOrDefault()?.Value ?? double.NaN). Ties on AtUtc
// resolve to the last-inserted matching reading (stable descending order).
func (b *InMemoryIoTBoard) LatestValue(deviceId, metric string) float64 {
	b.mu.RLock()
	defer b.mu.RUnlock()
	found := false
	var bestAt time.Time
	var bestVal float64
	for _, t := range b.telemetry {
		if t.DeviceId != deviceId || t.Metric != metric {
			continue
		}
		// Keep the latest by AtUtc; on an exact tie keep the FIRST-inserted to match
		// C# stable OrderByDescending(AtUtc).FirstOrDefault() (strict After, not >=).
		if !found || t.AtUtc.After(bestAt) {
			found = true
			bestAt = t.AtUtc
			bestVal = t.Value
		}
	}
	if !found {
		return math.NaN()
	}
	return bestVal
}

// History lists telemetry for (deviceId, metric) ordered by AtUtc descending,
// capped at limit. Ports History (ArgumentOutOfRange on limit <= 0 -> error).
// Equal timestamps break by a stable descending sort (source order preserved).
func (b *InMemoryIoTBoard) History(deviceId, metric string, limit int) ([]IoTTelemetry, error) {
	if limit <= 0 {
		return nil, errors.New("limit out of range")
	}
	b.mu.RLock()
	out := make([]IoTTelemetry, 0)
	for _, t := range b.telemetry {
		if t.DeviceId == deviceId && t.Metric == metric {
			out = append(out, t)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.After(out[j].AtUtc) })
	if len(out) > limit {
		out = out[:limit]
	}
	return out, nil
}

// SendCommand appends a command. Ports SendCommand.
func (b *InMemoryIoTBoard) SendCommand(c IoTCommand) {
	b.mu.Lock()
	b.commands = append(b.commands, c)
	b.mu.Unlock()
}

// CommandsFor lists commands sent to deviceId ordered by SentUtc descending.
// Ports CommandsFor (OrderByDescending(SentUtc)). Equal timestamps break by a
// stable descending sort (source order preserved).
func (b *InMemoryIoTBoard) CommandsFor(deviceId string) []IoTCommand {
	b.mu.RLock()
	out := make([]IoTCommand, 0)
	for _, c := range b.commands {
		if c.DeviceId == deviceId {
			out = append(out, c)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].SentUtc.After(out[j].SentUtc) })
	return out
}

// Interface guard.
var _ IoTBoard = (*InMemoryIoTBoard)(nil)
