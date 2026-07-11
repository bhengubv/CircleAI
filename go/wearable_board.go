// wearable_board.go
//
// Ports the CircleAI.Wearable primitive vertical (WearablePrimitives.cs,
// WearableContext.cs):
//   WearableKind / WearableTelemetryKind (enums) -> int consts, stable ordinals
//   WearableDevice / WearableSample (records)     -> value structs
//   WearableContext (record)                      -> value struct
//   IWearableBoard           -> WearableBoard interface (I-prefix dropped)
//   InMemoryWearableBoard    -> InMemoryWearableBoard
//
// The WearableCompanionAdapter (LLM glue) is out of scope.
//
// DETERMINISM: Devices orders by Vendor ascending. ReadSince orders by AtUtc
// ascending. LatestValue returns the newest matching sample's value as
// (value,bool) for the C# double?. AverageValue returns NaN when there are no
// samples, matching the C# double.NaN sentinel.

package circleai

import (
	"errors"
	"math"
	"sort"
	"sync"
	"time"
)

// WearableKind enumerates wearable form factors. Ports the WearableKind enum;
// ordinals are stable (Smartwatch=0..Headset=4).
type WearableKind int

const (
	WearableKindSmartwatch WearableKind = iota
	WearableKindFitnessBand
	WearableKindChestStrap
	WearableKindPatch
	WearableKindHeadset
)

// WearableTelemetryKind enumerates wearable telemetry channels. Ports the
// WearableTelemetryKind enum; ordinals are stable (HeartRate=0..OxygenPct=6).
type WearableTelemetryKind int

const (
	WearableTelemetryKindHeartRate WearableTelemetryKind = iota
	WearableTelemetryKindSteps
	WearableTelemetryKindCalories
	WearableTelemetryKindSleepStage
	WearableTelemetryKindSkinTempC
	WearableTelemetryKindStress
	WearableTelemetryKindOxygenPct
)

// WearableDevice is a paired wearable device. Ports the WearableDevice record.
type WearableDevice struct {
	DeviceId        string
	Kind            WearableKind
	Vendor          string
	FirmwareVersion string
	BatteryPct      float64
}

// WearableSample is a telemetry sample from a device. Ports the WearableSample
// record.
type WearableSample struct {
	DeviceId string
	Kind     WearableTelemetryKind
	Value    float64
	AtUtc    time.Time
}

// WearableContext is a biometric snapshot injected into the Companion context on
// wearable surfaces. Ports the WearableContext record. Optional readings use
// pointers to model the C# nullable fields.
type WearableContext struct {
	HeartRateBpm    *float64
	StepCountToday  *int
	SpO2Percent     *float64
	SkinTempCelsius *float64
	IsWorkoutActive bool
	CapturedAt      time.Time
}

// WearableBoard is the devices/telemetry board. Ports IWearableBoard.
type WearableBoard interface {
	Add(d WearableDevice)
	GetDevice(id string) (WearableDevice, bool)
	// Devices lists all devices ordered by Vendor.
	Devices() []WearableDevice
	// Record stores a sample; errors when the device is unknown.
	Record(s WearableSample) error
	// ReadSince lists a device's samples of a kind at/after since, oldest-first.
	ReadSince(deviceId string, kind WearableTelemetryKind, since time.Time) []WearableSample
	// LatestValue returns the newest sample value for a device+kind, or
	// (0,false) if none.
	LatestValue(deviceId string, kind WearableTelemetryKind) (float64, bool)
	// AverageValue is the mean sample value for a device+kind at/after since;
	// NaN when there are none.
	AverageValue(deviceId string, kind WearableTelemetryKind, since time.Time) float64
}

// InMemoryWearableBoard is a concurrency-safe in-memory WearableBoard. Ports
// InMemoryWearableBoard.
type InMemoryWearableBoard struct {
	mu      sync.Mutex
	devices map[string]WearableDevice
	samples []WearableSample
}

// NewInMemoryWearableBoard constructs an empty board.
func NewInMemoryWearableBoard() *InMemoryWearableBoard {
	return &InMemoryWearableBoard{devices: make(map[string]WearableDevice)}
}

// Add stores (or replaces by DeviceId) a device. Ports Add.
func (b *InMemoryWearableBoard) Add(d WearableDevice) {
	b.mu.Lock()
	b.devices[d.DeviceId] = d
	b.mu.Unlock()
}

// GetDevice returns the device for id, or (zero,false). Ports GetDevice.
func (b *InMemoryWearableBoard) GetDevice(id string) (WearableDevice, bool) {
	b.mu.Lock()
	d, ok := b.devices[id]
	b.mu.Unlock()
	return d, ok
}

// Devices lists all devices ordered by Vendor. Ports the Devices property.
func (b *InMemoryWearableBoard) Devices() []WearableDevice {
	b.mu.Lock()
	out := make([]WearableDevice, 0, len(b.devices))
	for _, d := range b.devices {
		out = append(out, d)
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].Vendor < out[j].Vendor })
	return out
}

// Record stores a sample. Ports Record (throws on unknown device -> error).
func (b *InMemoryWearableBoard) Record(s WearableSample) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if _, ok := b.devices[s.DeviceId]; !ok {
		return errors.New("Unknown device " + s.DeviceId)
	}
	b.samples = append(b.samples, s)
	return nil
}

// ReadSince lists a device's samples of a kind at/after since, oldest-first.
// Ports ReadSince.
func (b *InMemoryWearableBoard) ReadSince(deviceId string, kind WearableTelemetryKind, since time.Time) []WearableSample {
	b.mu.Lock()
	out := make([]WearableSample, 0)
	for _, s := range b.samples {
		if s.DeviceId == deviceId && s.Kind == kind && !s.AtUtc.Before(since) {
			out = append(out, s)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.Before(out[j].AtUtc) })
	return out
}

// LatestValue returns the newest sample value for a device+kind. Ports
// LatestValue (null -> (0,false)).
func (b *InMemoryWearableBoard) LatestValue(deviceId string, kind WearableTelemetryKind) (float64, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	var newest time.Time
	var value float64
	found := false
	for _, s := range b.samples {
		if s.DeviceId == deviceId && s.Kind == kind {
			if !found || s.AtUtc.After(newest) {
				newest = s.AtUtc
				value = s.Value
				found = true
			}
		}
	}
	if !found {
		return 0, false
	}
	return value, true
}

// AverageValue is the mean sample value for a device+kind at/after since. Ports
// AverageValue (empty -> NaN).
func (b *InMemoryWearableBoard) AverageValue(deviceId string, kind WearableTelemetryKind, since time.Time) float64 {
	items := b.ReadSince(deviceId, kind, since)
	if len(items) == 0 {
		return math.NaN()
	}
	var sum float64
	for _, s := range items {
		sum += s.Value
	}
	return sum / float64(len(items))
}

// Interface guard.
var _ WearableBoard = (*InMemoryWearableBoard)(nil)
