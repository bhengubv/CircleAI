// personal_health_board.go
//
// Ports the CircleAI.Personal.Health primitive vertical
// (PersonalHealthPrimitives.cs):
//   VitalKind (enum)  -> VitalKind (int consts, stable ordinals)
//   VitalReading / Allergy / Medication (records) -> value structs
//   IPersonalHealthBoard        -> PersonalHealthBoard interface
//   InMemoryPersonalHealthBoard -> InMemoryPersonalHealthBoard
//
// The PersonalHealthDomainContext (static prompt strings) and
// PersonalHealthCompanionAdapter (LLM-prompt wrapper) are out of scope for the
// deterministic in-memory board.
//
// DETERMINISM: readings order by AtUtc (asc for ReadSince, desc for Latest);
// Allergies and ActiveMedications order deterministically (AllergyId asc / Name
// asc with an id tiebreak) where C# leaves ConcurrentDictionary order undefined.

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// VitalKind classifies a vital reading. Ordinals match the C# enum declaration
// order (BloodPressureSystolic=0 ... StepsCount=7). Ports VitalKind.
type VitalKind int

const (
	// VitalBloodPressureSystolic is systolic blood pressure.
	VitalBloodPressureSystolic VitalKind = iota
	// VitalBloodPressureDiastolic is diastolic blood pressure.
	VitalBloodPressureDiastolic
	// VitalGlucoseMgDl is blood glucose in mg/dL.
	VitalGlucoseMgDl
	// VitalWeightKg is body weight in kilograms.
	VitalWeightKg
	// VitalHeartRateBpm is heart rate in beats per minute.
	VitalHeartRateBpm
	// VitalTemperatureC is body temperature in Celsius.
	VitalTemperatureC
	// VitalOxygenPct is blood-oxygen saturation percentage.
	VitalOxygenPct
	// VitalStepsCount is a step count.
	VitalStepsCount
)

// String renders the C# enum member name for a VitalKind.
func (k VitalKind) String() string {
	switch k {
	case VitalBloodPressureSystolic:
		return "BloodPressureSystolic"
	case VitalBloodPressureDiastolic:
		return "BloodPressureDiastolic"
	case VitalGlucoseMgDl:
		return "GlucoseMgDl"
	case VitalWeightKg:
		return "WeightKg"
	case VitalHeartRateBpm:
		return "HeartRateBpm"
	case VitalTemperatureC:
		return "TemperatureC"
	case VitalOxygenPct:
		return "OxygenPct"
	case VitalStepsCount:
		return "StepsCount"
	default:
		return "Unknown"
	}
}

// VitalReading is a single vital measurement. Ports the VitalReading record.
// Note is a pointer to mirror the nullable C# string?.
type VitalReading struct {
	Kind  VitalKind
	Value float64
	AtUtc time.Time
	Note  *string
}

// Allergy is a recorded allergy. Ports the Allergy record.
type Allergy struct {
	AllergyId string
	Substance string
	Severity  string
}

// Medication is a medication course. Ports the Medication record. EndedAtUtc is a
// pointer to mirror the nullable C# DateTimeOffset? (nil == still active).
type Medication struct {
	MedId        string
	Name         string
	Dose         string
	Frequency    string
	StartedAtUtc time.Time
	EndedAtUtc   *time.Time
}

// PersonalHealthBoard is the personal vitals/allergies/medications board. Ports
// IPersonalHealthBoard. Allergies is exposed as a method.
type PersonalHealthBoard interface {
	Record(v VitalReading)
	// ReadSince lists readings of a kind at or after since, earliest first.
	ReadSince(kind VitalKind, since time.Time) []VitalReading
	// Latest returns the most recent reading of a kind, or (zero, false) if none.
	Latest(kind VitalKind) (VitalReading, bool)
	AddAllergy(a Allergy)
	// Allergies lists all recorded allergies.
	Allergies() []Allergy
	AddMedication(m Medication)
	// EndMedication sets a medication's end time; errors if the id is unknown.
	EndMedication(medId string, endedAtUtc time.Time) error
	// ActiveMedications lists medications with no end time, by Name ascending.
	ActiveMedications() []Medication
}

// InMemoryPersonalHealthBoard is a concurrency-safe in-memory
// PersonalHealthBoard. Ports InMemoryPersonalHealthBoard (vitals in an ordered
// list guarded by a mutex; allergies/medications in maps).
type InMemoryPersonalHealthBoard struct {
	mu        sync.RWMutex
	vitals    []VitalReading
	allergies map[string]Allergy
	meds      map[string]Medication
}

// NewInMemoryPersonalHealthBoard constructs an empty board.
func NewInMemoryPersonalHealthBoard() *InMemoryPersonalHealthBoard {
	return &InMemoryPersonalHealthBoard{
		vitals:    make([]VitalReading, 0),
		allergies: make(map[string]Allergy),
		meds:      make(map[string]Medication),
	}
}

// Record appends a vital reading. Ports Record.
func (b *InMemoryPersonalHealthBoard) Record(v VitalReading) {
	b.mu.Lock()
	b.vitals = append(b.vitals, v)
	b.mu.Unlock()
}

// ReadSince lists readings of kind at or after since, ordered by AtUtc ascending.
// Ports ReadSince. Equal timestamps keep insertion order (stable sort).
func (b *InMemoryPersonalHealthBoard) ReadSince(kind VitalKind, since time.Time) []VitalReading {
	b.mu.RLock()
	out := make([]VitalReading, 0)
	for _, v := range b.vitals {
		if v.Kind == kind && !v.AtUtc.Before(since) {
			out = append(out, v)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.Before(out[j].AtUtc) })
	return out
}

// Latest returns the most recent reading of kind. Ports Latest (OrderByDescending
// .FirstOrDefault -> (zero, false) when none). C# OrderByDescending is a STABLE
// sort and First() takes the head, so among equal-max timestamps the
// FIRST-INSERTED reading wins; this scan replaces best only on a strictly newer
// timestamp to reproduce that (verified against the .NET runtime).
func (b *InMemoryPersonalHealthBoard) Latest(kind VitalKind) (VitalReading, bool) {
	b.mu.RLock()
	defer b.mu.RUnlock()
	var best VitalReading
	found := false
	for _, v := range b.vitals {
		if v.Kind != kind {
			continue
		}
		if !found || v.AtUtc.After(best.AtUtc) {
			best = v
			found = true
		}
	}
	return best, found
}

// AddAllergy stores (or replaces by AllergyId) an allergy. Ports AddAllergy.
func (b *InMemoryPersonalHealthBoard) AddAllergy(a Allergy) {
	b.mu.Lock()
	b.allergies[a.AllergyId] = a
	b.mu.Unlock()
}

// Allergies lists all allergies (sorted by AllergyId for determinism). Ports the
// Allergies property.
func (b *InMemoryPersonalHealthBoard) Allergies() []Allergy {
	b.mu.RLock()
	out := make([]Allergy, 0, len(b.allergies))
	for _, a := range b.allergies {
		out = append(out, a)
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AllergyId < out[j].AllergyId })
	return out
}

// AddMedication stores (or replaces by MedId) a medication. Ports AddMedication.
func (b *InMemoryPersonalHealthBoard) AddMedication(m Medication) {
	b.mu.Lock()
	b.meds[m.MedId] = m
	b.mu.Unlock()
}

// EndMedication sets a medication's EndedAtUtc. Ports EndMedication (throws on
// unknown id -> error).
func (b *InMemoryPersonalHealthBoard) EndMedication(medId string, endedAtUtc time.Time) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	m, ok := b.meds[medId]
	if !ok {
		return errors.New("Unknown medication " + medId)
	}
	end := endedAtUtc
	m.EndedAtUtc = &end
	b.meds[medId] = m
	return nil
}

// ActiveMedications lists medications with no end time, ordered by Name ascending.
// Ports ActiveMedications (Where(EndedAtUtc is null).OrderBy(Name)). C#
// OrderBy(string) is culture-sensitive; cultureLess reproduces that for ASCII
// medication names (see domain_sort.go). Identical names break by MedId so the
// result stays deterministic (C# leaves same-name order to the stable OrderBy over
// undefined ConcurrentDictionary enumeration).
func (b *InMemoryPersonalHealthBoard) ActiveMedications() []Medication {
	b.mu.RLock()
	out := make([]Medication, 0)
	for _, m := range b.meds {
		if m.EndedAtUtc == nil {
			out = append(out, m)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].Name != out[j].Name {
			return cultureLess(out[i].Name, out[j].Name)
		}
		return out[i].MedId < out[j].MedId
	})
	return out
}

// Interface guard.
var _ PersonalHealthBoard = (*InMemoryPersonalHealthBoard)(nil)
