// pets_board.go
//
// Ports the CircleAI.Pets primitive vertical (PetsPrimitives.cs):
//   Pet / Vaccination / WeightSample / VetAppointment (records) -> value structs
//   IPetsBoard        -> PetsBoard interface (I-prefix dropped)
//   InMemoryPetsBoard -> InMemoryPetsBoard
//
// The PetsDomainContext (static prompt strings) and PetsCompanionAdapter
// (LLM-prompt wrapper) are out of scope for the deterministic in-memory board.
//
// DETERMINISM: Pets orders by Name (culture-sensitive default comparer ->
// cultureLess). VaccinationsFor orders by AdministeredUtc descending; WeightHistory
// orders by AtUtc ascending (both via a stable sort so equal timestamps keep
// source order). UpcomingAppointments returns appointments at or after the current
// wall clock (the C# DateTimeOffset.UtcNow), ordered by AtUtc ascending. Vaccination
// and weight samples live in ordered lists guarded by the mutex.

package circleai

import (
	"sort"
	"sync"
	"time"
)

// Pet is a pet record. Ports the Pet record. Breed is a pointer to mirror the
// nullable C# string? (nil == unspecified).
type Pet struct {
	PetId       string
	Name        string
	Species     string
	Breed       *string
	DateOfBirth time.Time
}

// Vaccination is a vaccination record. Ports the Vaccination record.
// BoosterDueUtc is a pointer to mirror the nullable C# DateTimeOffset?.
type Vaccination struct {
	PetId           string
	Vaccine         string
	AdministeredUtc time.Time
	BoosterDueUtc   *time.Time
}

// WeightSample is a timestamped weight reading. Ports the WeightSample record.
type WeightSample struct {
	PetId    string
	WeightKg float64
	AtUtc    time.Time
}

// VetAppointment is a scheduled vet appointment. Ports the VetAppointment record.
type VetAppointment struct {
	ApptId string
	PetId  string
	Reason string
	AtUtc  time.Time
	Vet    string
}

// PetsBoard is the pets/vaccinations/weights/appointments board. Ports
// IPetsBoard. Pets is exposed as a method.
type PetsBoard interface {
	Add(p Pet)
	GetPet(id string) (Pet, bool)
	// Pets lists all pets ordered by Name ascending.
	Pets() []Pet
	RecordVaccination(v Vaccination)
	// VaccinationsFor lists a pet's vaccinations, most recent first.
	VaccinationsFor(petId string) []Vaccination
	RecordWeight(s WeightSample)
	// WeightHistory lists a pet's weight samples, earliest first.
	WeightHistory(petId string) []WeightSample
	Schedule(a VetAppointment)
	// UpcomingAppointments lists appointments at or after now, soonest first.
	UpcomingAppointments() []VetAppointment
}

// InMemoryPetsBoard is a concurrency-safe in-memory PetsBoard. Ports
// InMemoryPetsBoard (pets + appointments in maps; vaccinations + weights in
// ordered lists guarded by the mutex).
type InMemoryPetsBoard struct {
	mu      sync.RWMutex
	pets    map[string]Pet
	vax     []Vaccination
	weights []WeightSample
	appts   map[string]VetAppointment
}

// NewInMemoryPetsBoard constructs an empty board.
func NewInMemoryPetsBoard() *InMemoryPetsBoard {
	return &InMemoryPetsBoard{
		pets:    make(map[string]Pet),
		vax:     make([]Vaccination, 0),
		weights: make([]WeightSample, 0),
		appts:   make(map[string]VetAppointment),
	}
}

// Add stores (or replaces by PetId) a pet. Ports Add.
func (b *InMemoryPetsBoard) Add(p Pet) {
	b.mu.Lock()
	b.pets[p.PetId] = p
	b.mu.Unlock()
}

// GetPet returns the pet for id and true, or (zero, false) if absent. Ports
// GetPet.
func (b *InMemoryPetsBoard) GetPet(id string) (Pet, bool) {
	b.mu.RLock()
	p, ok := b.pets[id]
	b.mu.RUnlock()
	return p, ok
}

// Pets lists all pets ordered by Name ascending. Ports the Pets property
// (OrderBy(Name)).
func (b *InMemoryPetsBoard) Pets() []Pet {
	b.mu.RLock()
	out := make([]Pet, 0, len(b.pets))
	for _, p := range b.pets {
		out = append(out, p)
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return cultureLess(out[i].Name, out[j].Name) })
	return out
}

// RecordVaccination appends a vaccination. Ports RecordVaccination.
func (b *InMemoryPetsBoard) RecordVaccination(v Vaccination) {
	b.mu.Lock()
	b.vax = append(b.vax, v)
	b.mu.Unlock()
}

// VaccinationsFor lists a pet's vaccinations ordered by AdministeredUtc
// descending. Ports VaccinationsFor. Equal timestamps keep source order (stable).
func (b *InMemoryPetsBoard) VaccinationsFor(petId string) []Vaccination {
	b.mu.RLock()
	out := make([]Vaccination, 0)
	for _, v := range b.vax {
		if v.PetId == petId {
			out = append(out, v)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AdministeredUtc.After(out[j].AdministeredUtc) })
	return out
}

// RecordWeight appends a weight sample. Ports RecordWeight.
func (b *InMemoryPetsBoard) RecordWeight(s WeightSample) {
	b.mu.Lock()
	b.weights = append(b.weights, s)
	b.mu.Unlock()
}

// WeightHistory lists a pet's weight samples ordered by AtUtc ascending. Ports
// WeightHistory. Equal timestamps keep source order (stable).
func (b *InMemoryPetsBoard) WeightHistory(petId string) []WeightSample {
	b.mu.RLock()
	out := make([]WeightSample, 0)
	for _, w := range b.weights {
		if w.PetId == petId {
			out = append(out, w)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.Before(out[j].AtUtc) })
	return out
}

// Schedule stores (or replaces by ApptId) an appointment. Ports Schedule.
func (b *InMemoryPetsBoard) Schedule(a VetAppointment) {
	b.mu.Lock()
	b.appts[a.ApptId] = a
	b.mu.Unlock()
}

// UpcomingAppointments lists appointments at or after the current wall clock,
// ordered by AtUtc ascending. Ports UpcomingAppointments
// (Where(AtUtc >= DateTimeOffset.UtcNow).OrderBy(AtUtc)). Equal timestamps break
// by ApptId for determinism.
func (b *InMemoryPetsBoard) UpcomingAppointments() []VetAppointment {
	now := time.Now().UTC()
	b.mu.RLock()
	out := make([]VetAppointment, 0)
	for _, a := range b.appts {
		if !a.AtUtc.Before(now) {
			out = append(out, a)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].AtUtc.Equal(out[j].AtUtc) {
			return out[i].AtUtc.Before(out[j].AtUtc)
		}
		return out[i].ApptId < out[j].ApptId
	})
	return out
}

// Interface guard.
var _ PetsBoard = (*InMemoryPetsBoard)(nil)
