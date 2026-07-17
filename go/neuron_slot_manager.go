// neuron_slot_manager.go
//
// Two-slot residency — port of CircleAI.Hosting.Neuron.ResidentSlotManager.
// Owns the Neuron's one evictable specialist slot beside the always-warm
// generalist floor (held by AIService). RAM headroom is checked before a
// specialist is built; the incumbent is evicted first on a swap.

package circleai

import (
	"fmt"
	"strings"
	"sync"
)

// SlotOutcome is the result of a specialist-slot admission attempt.
type SlotOutcome int

const (
	// SlotAdmitted — the specialist was built and is now resident.
	SlotAdmitted SlotOutcome = iota
	// SlotAlreadyResident — the requested specialist model was already resident.
	SlotAlreadyResident
	// SlotInsufficientRAM — the RAM gate denied the load; caller uses the generalist.
	SlotInsufficientRAM
	// SlotBuildFailed — the factory failed; caller uses the generalist.
	SlotBuildFailed
)

// SlotAdmission is the result of EnsureSpecialist.
type SlotAdmission struct {
	Outcome   SlotOutcome
	Generator IChatGenerator // non-nil when admitted / already-resident
	Message   string
}

// ResidentSlotManager manages one evictable specialist slot. The generalist floor
// is never held here — only its reserved footprint counts against the RAM gate.
type ResidentSlotManager struct {
	generalistReservedBytes int64
	ramAvailable            func() int64

	mu                sync.Mutex
	specialist        IChatGenerator
	specialistModelID string
}

// NewResidentSlotManager builds a manager. ramAvailable may be nil (reports 0).
func NewResidentSlotManager(generalistReservedBytes int64, ramAvailable func() int64) *ResidentSlotManager {
	if generalistReservedBytes < 0 {
		generalistReservedBytes = 0
	}
	if ramAvailable == nil {
		ramAvailable = func() int64 { return 0 }
	}
	return &ResidentSlotManager{
		generalistReservedBytes: generalistReservedBytes,
		ramAvailable:            ramAvailable,
	}
}

// ResidentSpecialistModelID returns the resident specialist's model id, or "".
func (m *ResidentSlotManager) ResidentSpecialistModelID() string {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.specialistModelID
}

// ResidentSpecialist returns the resident specialist, or nil.
func (m *ResidentSlotManager) ResidentSpecialist() IChatGenerator {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.specialist
}

// EnsureSpecialist ensures a specialist for selection is resident, building it via
// build when needed. Admission gate: the generalist floor plus the specialist
// footprint must fit under the device RAM ceiling. On denial / build failure the
// slot is left empty and the caller answers from the generalist.
func (m *ResidentSlotManager) EnsureSpecialist(selection ModelSelection, build func(modelID string) (IChatGenerator, error)) SlotAdmission {
	m.mu.Lock()
	defer m.mu.Unlock()

	if m.specialist != nil && strings.EqualFold(m.specialistModelID, selection.ModelID) {
		return SlotAdmission{
			Outcome:   SlotAlreadyResident,
			Generator: m.specialist,
			Message:   fmt.Sprintf("Specialist %q already resident.", selection.ModelID),
		}
	}

	ceiling := m.ramAvailable()
	if ceiling < 0 {
		ceiling = 0
	}
	needed := m.generalistReservedBytes
	if selection.EstimatedBytes > 0 {
		needed += selection.EstimatedBytes
	}
	if ceiling > 0 && needed > ceiling {
		return SlotAdmission{
			Outcome: SlotInsufficientRAM,
			Message: fmt.Sprintf("Specialist %q needs %d MiB; device ceiling %d MiB.", selection.ModelID, needed>>20, ceiling>>20),
		}
	}

	// Only one specialist slot — evict the incumbent before building.
	m.disposeSpecialistLocked()

	built, err := build(selection.ModelID)
	if err != nil || built == nil {
		msg := "returned nil"
		if err != nil {
			msg = err.Error()
		}
		return SlotAdmission{
			Outcome: SlotBuildFailed,
			Message: fmt.Sprintf("Specialist %q build failed: %s", selection.ModelID, msg),
		}
	}

	m.specialist = built
	m.specialistModelID = selection.ModelID
	return SlotAdmission{
		Outcome:   SlotAdmitted,
		Generator: built,
		Message:   fmt.Sprintf("Specialist %q resident.", selection.ModelID),
	}
}

// EvictSpecialist evicts the specialist (the generalist floor is never touched).
func (m *ResidentSlotManager) EvictSpecialist() {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.disposeSpecialistLocked()
}

func (m *ResidentSlotManager) disposeSpecialistLocked() {
	gen := m.specialist
	m.specialist = nil
	m.specialistModelID = ""
	if gen != nil {
		_ = gen.Close()
	}
}
