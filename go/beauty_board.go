// beauty_board.go
//
// Ports the CircleAI.Beauty primitive vertical (BeautyPrimitives.cs):
//   Treatment / Appointment / SkinProfile (records) -> value structs
//   IBeautyBoard             -> BeautyBoard interface (I-prefix dropped)
//   InMemoryBeautyBoard      -> InMemoryBeautyBoard
//
// The BeautyDomainContext / BeautyCompanionAdapter (LLM glue) are out of scope.
//
// DETERMINISM: AppointmentsBetween orders by AtUtc ascending. RecommendFor
// mirrors a ConcurrentDictionary in C# (no defined order); this port sorts by
// TreatmentId for stable output. Treatment.Price uses the shared exact Decimal.

package circleai

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// Treatment is a bookable treatment. Ports the Treatment record. Price uses the
// shared exact Decimal (C# decimal).
type Treatment struct {
	TreatmentId     string
	Name            string
	DurationMinutes int
	Price           Decimal
	Currency        string
}

// Appointment is a booked appointment. Ports the Appointment record. Notes is a
// *string to model the C# nullable string.
type Appointment struct {
	ApptId      string
	ClientName  string
	TreatmentId string
	AtUtc       time.Time
	Notes       *string
}

// SkinProfile is a client's skin profile. Ports the SkinProfile record.
type SkinProfile struct {
	ClientName string
	SkinType   string
	Concerns   []string
}

// BeautyBoard is the treatments/appointments/profiles board. Ports IBeautyBoard.
type BeautyBoard interface {
	AddTreatment(t Treatment)
	GetTreatment(id string) (Treatment, bool)
	Book(a Appointment)
	// AppointmentsBetween lists appointments in [start,end], oldest-first.
	AppointmentsBetween(start, end time.Time) []Appointment
	SaveProfile(p SkinProfile)
	GetProfile(clientName string) (SkinProfile, bool)
	// RecommendFor returns treatments whose name contains any of the client's
	// concerns (case-insensitive); empty if the client has no profile.
	RecommendFor(clientName string) []Treatment
}

// InMemoryBeautyBoard is a concurrency-safe in-memory BeautyBoard. Ports
// InMemoryBeautyBoard.
type InMemoryBeautyBoard struct {
	mu         sync.Mutex
	treatments map[string]Treatment
	appts      []Appointment
	profiles   map[string]SkinProfile
}

// NewInMemoryBeautyBoard constructs an empty board.
func NewInMemoryBeautyBoard() *InMemoryBeautyBoard {
	return &InMemoryBeautyBoard{
		treatments: make(map[string]Treatment),
		profiles:   make(map[string]SkinProfile),
	}
}

// AddTreatment stores (or replaces by TreatmentId) a treatment. Ports
// AddTreatment.
func (b *InMemoryBeautyBoard) AddTreatment(t Treatment) {
	b.mu.Lock()
	b.treatments[t.TreatmentId] = t
	b.mu.Unlock()
}

// GetTreatment returns the treatment for id, or (zero,false). Ports
// GetTreatment.
func (b *InMemoryBeautyBoard) GetTreatment(id string) (Treatment, bool) {
	b.mu.Lock()
	t, ok := b.treatments[id]
	b.mu.Unlock()
	return t, ok
}

// Book appends an appointment. Ports Book.
func (b *InMemoryBeautyBoard) Book(a Appointment) {
	b.mu.Lock()
	b.appts = append(b.appts, a)
	b.mu.Unlock()
}

// AppointmentsBetween lists appointments in [start,end], oldest-first. Ports
// AppointmentsBetween.
func (b *InMemoryBeautyBoard) AppointmentsBetween(start, end time.Time) []Appointment {
	b.mu.Lock()
	out := make([]Appointment, 0)
	for _, a := range b.appts {
		if !a.AtUtc.Before(start) && !a.AtUtc.After(end) {
			out = append(out, a)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.Before(out[j].AtUtc) })
	return out
}

// SaveProfile stores (or replaces by ClientName) a skin profile. Ports
// SaveProfile.
func (b *InMemoryBeautyBoard) SaveProfile(p SkinProfile) {
	b.mu.Lock()
	b.profiles[p.ClientName] = p
	b.mu.Unlock()
}

// GetProfile returns the client's profile, or (zero,false). Ports GetProfile.
func (b *InMemoryBeautyBoard) GetProfile(clientName string) (SkinProfile, bool) {
	b.mu.Lock()
	p, ok := b.profiles[clientName]
	b.mu.Unlock()
	return p, ok
}

// RecommendFor returns treatments matching the client's concerns. Ports
// RecommendFor.
func (b *InMemoryBeautyBoard) RecommendFor(clientName string) []Treatment {
	b.mu.Lock()
	defer b.mu.Unlock()
	p, ok := b.profiles[clientName]
	if !ok {
		return []Treatment{}
	}
	out := make([]Treatment, 0)
	for _, t := range b.treatments {
		lname := strings.ToLower(t.Name)
		for _, c := range p.Concerns {
			if strings.Contains(lname, strings.ToLower(c)) {
				out = append(out, t)
				break
			}
		}
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].TreatmentId < out[j].TreatmentId })
	return out
}

// Interface guard.
var _ BeautyBoard = (*InMemoryBeautyBoard)(nil)
