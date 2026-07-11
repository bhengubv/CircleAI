// family_board.go
//
// Ports the CircleAI.Family primitive vertical (FamilyPrimitives.cs):
//   FamilyMember / FamilyEvent / SharedExpense (records) -> value structs
//   IFamilyBoard        -> FamilyBoard interface (I-prefix dropped)
//   InMemoryFamilyBoard -> InMemoryFamilyBoard
//
// The FamilyDomainContext (static prompt strings) and FamilyCompanionAdapter
// (LLM-prompt wrapper) are out of scope for the deterministic in-memory board.
//
// MONEY: SharedExpense.Amount and the two spend totals use the shared exact
// Decimal (C# decimal). DETERMINISM: Members orders by Name (culture-sensitive
// default comparer -> cultureLess). EventsForMember returns events that include
// memberId, ordered by AtUtc ascending (ties by EventId). TotalPaidBy /
// SpendByCategory sum expenses at or after `since` (SpendByCategory matches
// Category case-insensitively). MemberIds is copied defensively on Schedule.

package circleai

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// FamilyMember is a family member. Ports the FamilyMember record.
type FamilyMember struct {
	MemberId    string
	Name        string
	Role        string
	DateOfBirth time.Time
}

// FamilyEvent is a scheduled family event. Ports the FamilyEvent record.
// MemberIds mirrors the C# IReadOnlyList<string> of attendee member ids.
type FamilyEvent struct {
	EventId   string
	Title     string
	AtUtc     time.Time
	MemberIds []string
}

// SharedExpense is a shared expense. Ports the SharedExpense record. Amount uses
// exact Decimal.
type SharedExpense struct {
	ExpenseId string
	PaidById  string
	Amount    Decimal
	Currency  string
	Category  string
	AtUtc     time.Time
}

// FamilyBoard is the members/events/expenses board. Ports IFamilyBoard. Members
// is exposed as a method.
type FamilyBoard interface {
	Add(m FamilyMember)
	GetMember(id string) (FamilyMember, bool)
	// Members lists all members ordered by Name ascending.
	Members() []FamilyMember
	Schedule(e FamilyEvent)
	// EventsForMember lists events that include memberId, earliest first.
	EventsForMember(memberId string) []FamilyEvent
	Record(e SharedExpense)
	// TotalPaidBy sums expenses paid by memberId at or after since.
	TotalPaidBy(memberId string, since time.Time) Decimal
	// SpendByCategory sums expenses in category (case-insensitive) at or after since.
	SpendByCategory(category string, since time.Time) Decimal
}

// InMemoryFamilyBoard is a concurrency-safe in-memory FamilyBoard. Ports
// InMemoryFamilyBoard (members + events in maps; expenses in an ordered list
// guarded by the mutex).
type InMemoryFamilyBoard struct {
	mu       sync.RWMutex
	members  map[string]FamilyMember
	events   map[string]FamilyEvent
	expenses []SharedExpense
}

// NewInMemoryFamilyBoard constructs an empty board.
func NewInMemoryFamilyBoard() *InMemoryFamilyBoard {
	return &InMemoryFamilyBoard{
		members:  make(map[string]FamilyMember),
		events:   make(map[string]FamilyEvent),
		expenses: make([]SharedExpense, 0),
	}
}

// Add stores (or replaces by MemberId) a member. Ports Add.
func (b *InMemoryFamilyBoard) Add(m FamilyMember) {
	b.mu.Lock()
	b.members[m.MemberId] = m
	b.mu.Unlock()
}

// GetMember returns the member for id and true, or (zero, false) if absent. Ports
// GetMember.
func (b *InMemoryFamilyBoard) GetMember(id string) (FamilyMember, bool) {
	b.mu.RLock()
	m, ok := b.members[id]
	b.mu.RUnlock()
	return m, ok
}

// Members lists all members ordered by Name ascending. Ports the Members property
// (OrderBy(Name)).
func (b *InMemoryFamilyBoard) Members() []FamilyMember {
	b.mu.RLock()
	out := make([]FamilyMember, 0, len(b.members))
	for _, m := range b.members {
		out = append(out, m)
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return cultureLess(out[i].Name, out[j].Name) })
	return out
}

// Schedule stores (or replaces by EventId) an event, copying MemberIds
// defensively. Ports Schedule.
func (b *InMemoryFamilyBoard) Schedule(e FamilyEvent) {
	e.MemberIds = append([]string(nil), e.MemberIds...)
	b.mu.Lock()
	b.events[e.EventId] = e
	b.mu.Unlock()
}

// EventsForMember lists events whose MemberIds include memberId, ordered by AtUtc
// ascending. Ports EventsForMember (Where(MemberIds.Contains).OrderBy(AtUtc)).
// Equal timestamps break by EventId for determinism.
func (b *InMemoryFamilyBoard) EventsForMember(memberId string) []FamilyEvent {
	b.mu.RLock()
	out := make([]FamilyEvent, 0)
	for _, e := range b.events {
		for _, id := range e.MemberIds {
			if id == memberId {
				out = append(out, e)
				break
			}
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].AtUtc.Equal(out[j].AtUtc) {
			return out[i].AtUtc.Before(out[j].AtUtc)
		}
		return out[i].EventId < out[j].EventId
	})
	return out
}

// Record appends a shared expense. Ports Record.
func (b *InMemoryFamilyBoard) Record(e SharedExpense) {
	b.mu.Lock()
	b.expenses = append(b.expenses, e)
	b.mu.Unlock()
}

// TotalPaidBy sums the Amount of expenses paid by memberId at or after since.
// Ports TotalPaidBy (Where(PaidById == memberId && AtUtc >= since).Sum(Amount)).
func (b *InMemoryFamilyBoard) TotalPaidBy(memberId string, since time.Time) Decimal {
	b.mu.RLock()
	defer b.mu.RUnlock()
	var total Decimal
	for _, e := range b.expenses {
		if e.PaidById == memberId && !e.AtUtc.Before(since) {
			total = total.Add(e.Amount)
		}
	}
	return total
}

// SpendByCategory sums the Amount of expenses in category (case-insensitive) at
// or after since. Ports SpendByCategory
// (Where(Category ~= category && AtUtc >= since).Sum(Amount)).
func (b *InMemoryFamilyBoard) SpendByCategory(category string, since time.Time) Decimal {
	b.mu.RLock()
	defer b.mu.RUnlock()
	var total Decimal
	for _, e := range b.expenses {
		if strings.EqualFold(e.Category, category) && !e.AtUtc.Before(since) {
			total = total.Add(e.Amount)
		}
	}
	return total
}

// Interface guard.
var _ FamilyBoard = (*InMemoryFamilyBoard)(nil)
