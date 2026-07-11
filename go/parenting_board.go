// parenting_board.go
//
// Ports the CircleAI.Parenting primitive vertical (ParentingPrimitives.cs):
//   Child / Milestone / RoutineEntry / Routine (records) -> value structs
//   DayOfWeek                                             -> time.Weekday
//   IParentingBoard        -> ParentingBoard interface (I-prefix dropped)
//   InMemoryParentingBoard -> InMemoryParentingBoard
//
// The ParentingDomainContext (static prompt strings) and ParentingCompanionAdapter
// (LLM-prompt wrapper) are out of scope for the deterministic in-memory board.
//
// DETERMINISM: Children orders by Name (culture-sensitive default comparer ->
// cultureLess). MilestonesFor orders by AchievedAtUtc descending (ties by a stable
// descending sort, source order preserved). Routine keys use the C#
// "{childId}/{DayOfWeek}" format; time.Weekday.String() yields the same day names
// ("Sunday".."Saturday") as the C# DayOfWeek enum. AgeAsOf returns at-DateOfBirth
// as a time.Duration (the C# TimeSpan). Milestone lists are per-child, guarded by
// the mutex; Routine entries are copied defensively on SetRoutine.

package circleai

import (
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// Child is a child record. Ports the Child record. Gender is a pointer to mirror
// the nullable C# string? (nil == unspecified).
type Child struct {
	ChildId     string
	Name        string
	DateOfBirth time.Time
	Gender      *string
}

// Milestone is a developmental milestone. Ports the Milestone record.
type Milestone struct {
	MilestoneId   string
	ChildId       string
	Category      string
	Description   string
	AchievedAtUtc time.Time
}

// RoutineEntry is one time-slotted activity in a routine. Ports the RoutineEntry
// record.
type RoutineEntry struct {
	Time     string
	Activity string
}

// Routine is a child's routine for a given weekday. Ports the Routine record.
// Entries mirrors the C# IReadOnlyList<RoutineEntry>.
type Routine struct {
	ChildId   string
	DayOfWeek time.Weekday
	Entries   []RoutineEntry
}

// ParentingBoard is the children/milestones/routines board. Ports
// IParentingBoard. Children is exposed as a method.
type ParentingBoard interface {
	AddChild(c Child)
	GetChild(id string) (Child, bool)
	// Children lists all children ordered by Name ascending.
	Children() []Child
	// RecordMilestone appends a milestone; errors on a blank ChildId.
	RecordMilestone(m Milestone) error
	// MilestonesFor lists a child's milestones, most recent first.
	MilestonesFor(childId string) []Milestone
	SetRoutine(r Routine)
	// GetRoutine returns the routine for (childId, dow) and true, or (zero, false).
	GetRoutine(childId string, dow time.Weekday) (Routine, bool)
	// AgeAsOf returns at minus the child's DateOfBirth; errors on an unknown child.
	AgeAsOf(childId string, at time.Time) (time.Duration, error)
}

// InMemoryParentingBoard is a concurrency-safe in-memory ParentingBoard. Ports
// InMemoryParentingBoard (children + routines in maps; per-child milestone lists
// guarded by the mutex).
type InMemoryParentingBoard struct {
	mu         sync.RWMutex
	children   map[string]Child
	milestones map[string][]Milestone
	routines   map[string]Routine
}

// NewInMemoryParentingBoard constructs an empty board.
func NewInMemoryParentingBoard() *InMemoryParentingBoard {
	return &InMemoryParentingBoard{
		children:   make(map[string]Child),
		milestones: make(map[string][]Milestone),
		routines:   make(map[string]Routine),
	}
}

// AddChild stores (or replaces by ChildId) a child. Ports AddChild.
func (b *InMemoryParentingBoard) AddChild(c Child) {
	b.mu.Lock()
	b.children[c.ChildId] = c
	b.mu.Unlock()
}

// GetChild returns the child for id and true, or (zero, false) if absent. Ports
// GetChild.
func (b *InMemoryParentingBoard) GetChild(id string) (Child, bool) {
	b.mu.RLock()
	c, ok := b.children[id]
	b.mu.RUnlock()
	return c, ok
}

// Children lists all children ordered by Name ascending. Ports the Children
// property (OrderBy(Name)).
func (b *InMemoryParentingBoard) Children() []Child {
	b.mu.RLock()
	out := make([]Child, 0, len(b.children))
	for _, c := range b.children {
		out = append(out, c)
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return cultureLess(out[i].Name, out[j].Name) })
	return out
}

// RecordMilestone appends a milestone under its ChildId. Ports RecordMilestone
// (ArgumentException on blank ChildId -> error).
func (b *InMemoryParentingBoard) RecordMilestone(m Milestone) error {
	if strings.TrimSpace(m.ChildId) == "" {
		return errors.New("ChildId required")
	}
	b.mu.Lock()
	b.milestones[m.ChildId] = append(b.milestones[m.ChildId], m)
	b.mu.Unlock()
	return nil
}

// MilestonesFor lists a child's milestones ordered by AchievedAtUtc descending
// (empty for an unknown child). Ports MilestonesFor. Equal timestamps break by a
// stable descending sort (source order preserved).
func (b *InMemoryParentingBoard) MilestonesFor(childId string) []Milestone {
	b.mu.RLock()
	list, ok := b.milestones[childId]
	cp := make([]Milestone, len(list))
	copy(cp, list)
	b.mu.RUnlock()
	if !ok {
		return []Milestone{}
	}
	sort.SliceStable(cp, func(i, j int) bool { return cp[i].AchievedAtUtc.After(cp[j].AchievedAtUtc) })
	return cp
}

// SetRoutine stores (or replaces by child+weekday) a routine, copying Entries
// defensively. Ports SetRoutine.
func (b *InMemoryParentingBoard) SetRoutine(r Routine) {
	r.Entries = append([]RoutineEntry(nil), r.Entries...)
	key := routineKey(r.ChildId, r.DayOfWeek)
	b.mu.Lock()
	b.routines[key] = r
	b.mu.Unlock()
}

// GetRoutine returns the routine for (childId, dow) and true, or (zero, false) if
// absent. Ports GetRoutine.
func (b *InMemoryParentingBoard) GetRoutine(childId string, dow time.Weekday) (Routine, bool) {
	key := routineKey(childId, dow)
	b.mu.RLock()
	r, ok := b.routines[key]
	b.mu.RUnlock()
	return r, ok
}

// AgeAsOf returns at minus the child's DateOfBirth. Ports AgeAsOf (throws on an
// unknown child -> error).
func (b *InMemoryParentingBoard) AgeAsOf(childId string, at time.Time) (time.Duration, error) {
	b.mu.RLock()
	c, ok := b.children[childId]
	b.mu.RUnlock()
	if !ok {
		return 0, errors.New("Unknown child " + childId)
	}
	return at.Sub(c.DateOfBirth), nil
}

// routineKey builds the C# "{childId}/{DayOfWeek}" composite key.
func routineKey(childId string, dow time.Weekday) string {
	return childId + "/" + dow.String()
}

// Interface guard.
var _ ParentingBoard = (*InMemoryParentingBoard)(nil)
