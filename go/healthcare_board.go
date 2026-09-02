// healthcare_board.go
//
// Ports the CircleAI.Healthcare primitive vertical (HealthcarePrimitives.cs):
//   Patient / HealthAppointment / Prescription (records) -> value structs
//   IHealthcareBoard      -> HealthcareBoard interface (I-prefix dropped)
//   InMemoryHealthcareBoard -> InMemoryHealthcareBoard
//
// The HealthcareDomainContext (static system-prompt / compliance-flag strings)
// and HealthcareCompanionAdapter (an ICompanionSession LLM-prompt wrapper) are
// out of scope for the deterministic in-memory board and are intentionally not
// ported here.
//
// DETERMINISM: the C# orders appointments by AtUtc ascending and prescriptions
// by PrescribedUtc descending over a ConcurrentDictionary (unspecified
// enumeration order), so equal timestamps tie non-deterministically there. This
// port keeps the same primary ordering and adds a stable id tiebreak so equal
// timestamps are ordered deterministically.

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// Patient is a healthcare patient. Ports the Patient record.
type Patient struct {
	PatientId   string
	Name        string
	DateOfBirth time.Time
}

// HealthAppointment is a scheduled appointment. Ports the HealthAppointment record.
type HealthAppointment struct {
	ApptId    string
	PatientId string
	Provider  string
	AtUtc     time.Time
	Status    string
}

// Prescription is a prescribed medication. Ports the Prescription record.
type Prescription struct {
	RxId           string
	PatientId      string
	MedicationName string
	Dose           string
	Frequency      string
	PrescribedUtc  time.Time
}

// HealthcareBoard is the healthcare operations board. Ports IHealthcareBoard.
type HealthcareBoard interface {
	Register(p Patient)
	GetPatient(id string) (Patient, bool)
	Schedule(a HealthAppointment)
	// UpdateStatus sets an appointment's status; errors if the id is unknown.
	UpdateStatus(apptId, status string) error
	// AppointmentsFor lists a patient's appointments, earliest first.
	AppointmentsFor(patientId string) []HealthAppointment
	Prescribe(r Prescription)
	// PrescriptionsFor lists a patient's prescriptions, most recent first.
	PrescriptionsFor(patientId string) []Prescription
}

// InMemoryHealthcareBoard is a concurrency-safe in-memory HealthcareBoard. Ports
// InMemoryHealthcareBoard (ConcurrentDictionary keyed on the respective id with
// ordinal comparison).
type InMemoryHealthcareBoard struct {
	mu       sync.RWMutex
	patients map[string]Patient
	appts    map[string]HealthAppointment
	rx       map[string]Prescription
}

// NewInMemoryHealthcareBoard constructs an empty board.
func NewInMemoryHealthcareBoard() *InMemoryHealthcareBoard {
	return &InMemoryHealthcareBoard{
		patients: make(map[string]Patient),
		appts:    make(map[string]HealthAppointment),
		rx:       make(map[string]Prescription),
	}
}

// Register stores (or replaces by PatientId) a patient. Ports Register.
func (b *InMemoryHealthcareBoard) Register(p Patient) {
	b.mu.Lock()
	b.patients[p.PatientId] = p
	b.mu.Unlock()
}

// GetPatient returns the patient for id and true, or (zero, false) if absent.
func (b *InMemoryHealthcareBoard) GetPatient(id string) (Patient, bool) {
	b.mu.RLock()
	p, ok := b.patients[id]
	b.mu.RUnlock()
	return p, ok
}

// Schedule stores (or replaces by ApptId) an appointment. Ports Schedule.
func (b *InMemoryHealthcareBoard) Schedule(a HealthAppointment) {
	b.mu.Lock()
	b.appts[a.ApptId] = a
	b.mu.Unlock()
}

// UpdateStatus mutates the appointment's status. Ports UpdateStatus (throws
// InvalidOperationException on unknown id -> returns an error here).
func (b *InMemoryHealthcareBoard) UpdateStatus(apptId, status string) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	a, ok := b.appts[apptId]
	if !ok {
		return errors.New("Unknown appointment " + apptId)
	}
	a.Status = status
	b.appts[apptId] = a
	return nil
}

// AppointmentsFor returns a patient's appointments ordered by AtUtc ascending.
// Ports AppointmentsFor (Where.OrderBy(AtUtc)).
func (b *InMemoryHealthcareBoard) AppointmentsFor(patientId string) []HealthAppointment {
	b.mu.RLock()
	out := make([]HealthAppointment, 0)
	for _, a := range b.appts {
		if a.PatientId == patientId {
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

// Prescribe stores (or replaces by RxId) a prescription. Ports Prescribe.
func (b *InMemoryHealthcareBoard) Prescribe(r Prescription) {
	b.mu.Lock()
	b.rx[r.RxId] = r
	b.mu.Unlock()
}

// PrescriptionsFor returns a patient's prescriptions ordered by PrescribedUtc
// descending (newest first). Ports PrescriptionsFor.
func (b *InMemoryHealthcareBoard) PrescriptionsFor(patientId string) []Prescription {
	b.mu.RLock()
	out := make([]Prescription, 0)
	for _, r := range b.rx {
		if r.PatientId == patientId {
			out = append(out, r)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].PrescribedUtc.Equal(out[j].PrescribedUtc) {
			return out[i].PrescribedUtc.After(out[j].PrescribedUtc)
		}
		return out[i].RxId < out[j].RxId
	})
	return out
}

// Interface guard.
var _ HealthcareBoard = (*InMemoryHealthcareBoard)(nil)
