// ambient_board.go
//
// Ports the CircleAI.Ambient primitive vertical (AmbientPrimitives.cs):
//   AmbientReading / AmbientPreference (records) -> value structs
//   IAmbientBoard            -> AmbientBoard interface (I-prefix dropped)
//   InMemoryAmbientBoard     -> InMemoryAmbientBoard
//
// The AmbientCompanionMonitor (a background poll loop over ICompanionSession /
// IProactiveReasoningService — LLM/host glue) is out of scope, matching the
// treatment of the other Companion adapters.
//
// DETERMINISM: Latest returns the newest reading for a device; History orders by
// AtUtc descending then caps at limit. IsComfortable applies the C# thresholds:
// |temp - target| <= 2, |humidity - target| <= 10, noise <= max.

package circleai

import (
	"sort"
	"sync"
	"time"
)

// AmbientReading is an environmental sensor reading. Ports the AmbientReading
// record.
type AmbientReading struct {
	DeviceId     string
	TemperatureC float64
	Humidity     float64
	LuxLight     float64
	DbNoise      float64
	AtUtc        time.Time
}

// AmbientPreference is a location's comfort preference. Ports the
// AmbientPreference record.
type AmbientPreference struct {
	Location       string
	TargetTempC    float64
	TargetHumidity float64
	MaxNoiseDb     float64
}

// AmbientBoard is the readings/preferences board. Ports IAmbientBoard.
type AmbientBoard interface {
	Record(r AmbientReading)
	// Latest returns the newest reading for a device, or (zero,false).
	Latest(deviceId string) (AmbientReading, bool)
	// History lists a device's readings newest-first, capped at limit.
	History(deviceId string, limit int) []AmbientReading
	SetPreference(p AmbientPreference)
	GetPreference(location string) (AmbientPreference, bool)
	// IsComfortable reports whether a device's latest reading meets the location's
	// preference; false if either is missing.
	IsComfortable(deviceId, location string) bool
}

// InMemoryAmbientBoard is a concurrency-safe in-memory AmbientBoard. Ports
// InMemoryAmbientBoard.
type InMemoryAmbientBoard struct {
	mu       sync.Mutex
	readings []AmbientReading
	prefs    map[string]AmbientPreference
}

// NewInMemoryAmbientBoard constructs an empty board.
func NewInMemoryAmbientBoard() *InMemoryAmbientBoard {
	return &InMemoryAmbientBoard{prefs: make(map[string]AmbientPreference)}
}

// Record appends a reading. Ports Record.
func (b *InMemoryAmbientBoard) Record(r AmbientReading) {
	b.mu.Lock()
	b.readings = append(b.readings, r)
	b.mu.Unlock()
}

// Latest returns the newest reading for a device. Ports Latest (null ->
// (zero,false)).
func (b *InMemoryAmbientBoard) Latest(deviceId string) (AmbientReading, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.latestLocked(deviceId)
}

// latestLocked returns the newest reading for a device. The caller must hold
// b.mu.
func (b *InMemoryAmbientBoard) latestLocked(deviceId string) (AmbientReading, bool) {
	var newest AmbientReading
	found := false
	for _, r := range b.readings {
		if r.DeviceId == deviceId {
			if !found || r.AtUtc.After(newest.AtUtc) {
				newest = r
				found = true
			}
		}
	}
	return newest, found
}

// History lists a device's readings newest-first, capped at limit. Ports
// History.
func (b *InMemoryAmbientBoard) History(deviceId string, limit int) []AmbientReading {
	b.mu.Lock()
	out := make([]AmbientReading, 0)
	for _, r := range b.readings {
		if r.DeviceId == deviceId {
			out = append(out, r)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.After(out[j].AtUtc) })
	if limit >= 0 && len(out) > limit {
		out = out[:limit]
	}
	return out
}

// SetPreference stores (or replaces by Location) a preference. Ports
// SetPreference.
func (b *InMemoryAmbientBoard) SetPreference(p AmbientPreference) {
	b.mu.Lock()
	b.prefs[p.Location] = p
	b.mu.Unlock()
}

// GetPreference returns a location's preference, or (zero,false). Ports
// GetPreference.
func (b *InMemoryAmbientBoard) GetPreference(location string) (AmbientPreference, bool) {
	b.mu.Lock()
	p, ok := b.prefs[location]
	b.mu.Unlock()
	return p, ok
}

// IsComfortable reports whether a device's latest reading meets a location's
// preference. Ports IsComfortable.
func (b *InMemoryAmbientBoard) IsComfortable(deviceId, location string) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	pref, ok := b.prefs[location]
	if !ok {
		return false
	}
	last, ok := b.latestLocked(deviceId)
	if !ok {
		return false
	}
	return absFloat(last.TemperatureC-pref.TargetTempC) <= 2 &&
		absFloat(last.Humidity-pref.TargetHumidity) <= 10 &&
		last.DbNoise <= pref.MaxNoiseDb
}

// absFloat returns the absolute value of f.
func absFloat(f float64) float64 {
	if f < 0 {
		return -f
	}
	return f
}

// Interface guard.
var _ AmbientBoard = (*InMemoryAmbientBoard)(nil)
