// elderly_board.go
//
// Ports the CircleAI.Elderly primitive vertical (ElderlyPrimitives.cs):
//   CarePlan / MedReminder / CheckIn (records) -> value structs
//   IElderlyCareBoard        -> ElderlyCareBoard interface (I-prefix dropped)
//   InMemoryElderlyCareBoard -> InMemoryElderlyCareBoard
//
// The ElderlyDomainContext (static prompt strings) and ElderlyCompanionAdapter
// (LLM-prompt wrapper) are out of scope for the deterministic in-memory board.
//
// FLAT-PACKAGE DISAMBIGUATION: CircleAI.Elderly's `CheckIn` record shares a name
// with CircleAI.Safety.Child's `CheckIn` (safety_child.go); in the single Go
// package it is named ElderlyCheckIn.
//
// DETERMINISM: ActiveRemindersFor keeps no defined C# order (ConcurrentDictionary
// values); this port sorts by ReminderId for stable output. LatestCheckIn picks
// the most recent check-in by AtUtc (exact ties resolve to the first-inserted,
// matching the C# stable OrderByDescending.FirstOrDefault).
// MissedCheckIn is true when there is no check-in for the resident or the latest
// predates `since`. CarePlan's condition/allergy lists are copied defensively on
// SetPlan. Reminder resident matching is case-sensitive (C# Ordinal), matching the
// ResidentName-keyed store.

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// CarePlan is a resident's care plan. Ports the CarePlan record. MedicalConditions
// and Allergies mirror the C# IReadOnlyList<string>.
type CarePlan struct {
	PlanId            string
	ResidentName      string
	MedicalConditions []string
	Allergies         []string
	CarerNotes        string
}

// MedReminder is a daily medication reminder. Ports the MedReminder record.
// DailyAt mirrors the C# TimeSpan (time of day as a duration since midnight).
type MedReminder struct {
	ReminderId   string
	ResidentName string
	Medication   string
	DailyAt      time.Duration
	Active       bool
}

// ElderlyCheckIn is a resident well-being check-in. Ports the CircleAI.Elderly
// CheckIn record (renamed for the flat package — see the file header). Note is a
// pointer to mirror the nullable C# string?.
type ElderlyCheckIn struct {
	CheckInId    string
	ResidentName string
	AtUtc        time.Time
	Status       string
	Note         *string
}

// ElderlyCareBoard is the care-plans/reminders/check-ins board. Ports
// IElderlyCareBoard.
type ElderlyCareBoard interface {
	SetPlan(p CarePlan)
	GetPlan(resident string) (CarePlan, bool)
	AddReminder(r MedReminder)
	// DeactivateReminder marks a reminder inactive; errors if the id is unknown.
	DeactivateReminder(reminderId string) error
	// ActiveRemindersFor lists a resident's active reminders.
	ActiveRemindersFor(resident string) []MedReminder
	RecordCheckIn(c ElderlyCheckIn)
	// LatestCheckIn returns the resident's most recent check-in and true, or
	// (zero, false) when there is none.
	LatestCheckIn(resident string) (ElderlyCheckIn, bool)
	// MissedCheckIn is true when the resident has no check-in at or after since.
	MissedCheckIn(resident string, since time.Time) bool
}

// InMemoryElderlyCareBoard is a concurrency-safe in-memory ElderlyCareBoard. Ports
// InMemoryElderlyCareBoard (plans keyed by ResidentName + reminders in maps;
// check-ins in an ordered list guarded by the mutex).
type InMemoryElderlyCareBoard struct {
	mu        sync.RWMutex
	plans     map[string]CarePlan
	reminders map[string]MedReminder
	checkIns  []ElderlyCheckIn
}

// NewInMemoryElderlyCareBoard constructs an empty board.
func NewInMemoryElderlyCareBoard() *InMemoryElderlyCareBoard {
	return &InMemoryElderlyCareBoard{
		plans:     make(map[string]CarePlan),
		reminders: make(map[string]MedReminder),
		checkIns:  make([]ElderlyCheckIn, 0),
	}
}

// SetPlan stores (or replaces by ResidentName) a care plan, copying its
// condition/allergy lists defensively. Ports SetPlan.
func (b *InMemoryElderlyCareBoard) SetPlan(p CarePlan) {
	p.MedicalConditions = append([]string(nil), p.MedicalConditions...)
	p.Allergies = append([]string(nil), p.Allergies...)
	b.mu.Lock()
	b.plans[p.ResidentName] = p
	b.mu.Unlock()
}

// GetPlan returns the plan for resident and true, or (zero, false) if absent.
// Ports GetPlan.
func (b *InMemoryElderlyCareBoard) GetPlan(resident string) (CarePlan, bool) {
	b.mu.RLock()
	p, ok := b.plans[resident]
	b.mu.RUnlock()
	return p, ok
}

// AddReminder stores (or replaces by ReminderId) a reminder. Ports AddReminder.
func (b *InMemoryElderlyCareBoard) AddReminder(r MedReminder) {
	b.mu.Lock()
	b.reminders[r.ReminderId] = r
	b.mu.Unlock()
}

// DeactivateReminder sets a reminder's Active to false. Ports DeactivateReminder
// (throws on unknown id -> error).
func (b *InMemoryElderlyCareBoard) DeactivateReminder(reminderId string) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	r, ok := b.reminders[reminderId]
	if !ok {
		return errors.New("Unknown reminder " + reminderId)
	}
	r.Active = false
	b.reminders[reminderId] = r
	return nil
}

// ActiveRemindersFor lists a resident's active reminders, sorted by ReminderId for
// determinism. Ports ActiveRemindersFor (Where(ResidentName == resident && Active)).
// Resident matching is case-sensitive (C# Ordinal).
func (b *InMemoryElderlyCareBoard) ActiveRemindersFor(resident string) []MedReminder {
	b.mu.RLock()
	out := make([]MedReminder, 0)
	for _, r := range b.reminders {
		if r.ResidentName == resident && r.Active {
			out = append(out, r)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].ReminderId < out[j].ReminderId })
	return out
}

// RecordCheckIn appends a check-in. Ports RecordCheckIn.
func (b *InMemoryElderlyCareBoard) RecordCheckIn(c ElderlyCheckIn) {
	b.mu.Lock()
	b.checkIns = append(b.checkIns, c)
	b.mu.Unlock()
}

// LatestCheckIn returns the resident's most recent check-in by AtUtc and true, or
// (zero, false) when there is none. Ports LatestCheckIn
// (OrderByDescending(AtUtc).FirstOrDefault()). Ties on AtUtc resolve to the
// last-inserted matching check-in (stable descending scan).
func (b *InMemoryElderlyCareBoard) LatestCheckIn(resident string) (ElderlyCheckIn, bool) {
	b.mu.RLock()
	defer b.mu.RUnlock()
	found := false
	var best ElderlyCheckIn
	for _, c := range b.checkIns {
		if c.ResidentName != resident {
			continue
		}
		// Keep the latest by AtUtc; on an exact tie keep the FIRST-inserted to match
		// C# stable OrderByDescending(AtUtc).FirstOrDefault() (strict After, not >=).
		if !found || c.AtUtc.After(best.AtUtc) {
			found = true
			best = c
		}
	}
	return best, found
}

// MissedCheckIn is true when the resident has no check-in, or the latest one
// predates since. Ports MissedCheckIn (latest is null || latest.AtUtc < since).
func (b *InMemoryElderlyCareBoard) MissedCheckIn(resident string, since time.Time) bool {
	latest, ok := b.LatestCheckIn(resident)
	if !ok {
		return true
	}
	return latest.AtUtc.Before(since)
}

// Interface guard.
var _ ElderlyCareBoard = (*InMemoryElderlyCareBoard)(nil)
