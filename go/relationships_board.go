// relationships_board.go
//
// Ports the CircleAI.Relationships primitive vertical
// (RelationshipsPrimitives.cs):
//   PersonContact / ImportantDate / ContactEvent (records) -> value structs
//   IRelationshipsBoard      -> RelationshipsBoard interface (I-prefix dropped)
//   InMemoryRelationshipsBoard -> InMemoryRelationshipsBoard
//
// The RelationshipsDomainContext / RelationshipsCompanionAdapter (LLM glue) are
// out of scope.
//
// DETERMINISM: Contacts orders by Name ascending. UpcomingThisMonth filters
// dates in the current month and orders by day-of-month. LastContact returns the
// newest ContactEvent's AtUtc (as (time,bool) for the C# nullable). NotContacted
// Since mirrors a ConcurrentDictionary in C# (no defined order); this port sorts
// by ContactId for stable output.

package circleai

import (
	"sort"
	"sync"
	"time"
)

// PersonContact is a personal contact. Ports the PersonContact record. Notes is
// a *string to model the C# nullable string.
type PersonContact struct {
	ContactId    string
	Name         string
	Relationship string
	Notes        *string
}

// ImportantDate is a dated event tied to a contact. Ports the ImportantDate
// record. Date mirrors the C# DateTime.
type ImportantDate struct {
	DateId    string
	ContactId string
	Kind      string
	Date      time.Time
}

// ContactEvent is a touchpoint with a contact. Ports the ContactEvent record.
// Note is a *string to model the C# nullable string.
type ContactEvent struct {
	ContactId string
	Kind      string
	AtUtc     time.Time
	Note      *string
}

// RelationshipsBoard is the contacts/dates/touchpoints board. Ports
// IRelationshipsBoard.
type RelationshipsBoard interface {
	AddContact(c PersonContact)
	GetContact(id string) (PersonContact, bool)
	// Contacts lists all contacts ordered by Name.
	Contacts() []PersonContact
	AddImportantDate(d ImportantDate)
	// UpcomingThisMonth lists important dates in the current month by day.
	UpcomingThisMonth() []ImportantDate
	RecordTouchpoint(e ContactEvent)
	// LastContact returns the most recent touchpoint time for a contact, or
	// (zero,false) if there is none.
	LastContact(contactId string) (time.Time, bool)
	// NotContactedSince lists contacts last touched before cutoff (or never),
	// sorted by ContactId.
	NotContactedSince(cutoff time.Time) []PersonContact
}

// InMemoryRelationshipsBoard is a concurrency-safe in-memory RelationshipsBoard.
// Ports InMemoryRelationshipsBoard.
type InMemoryRelationshipsBoard struct {
	mu       sync.Mutex
	contacts map[string]PersonContact
	dates    map[string]ImportantDate
	events   []ContactEvent
}

// NewInMemoryRelationshipsBoard constructs an empty board.
func NewInMemoryRelationshipsBoard() *InMemoryRelationshipsBoard {
	return &InMemoryRelationshipsBoard{
		contacts: make(map[string]PersonContact),
		dates:    make(map[string]ImportantDate),
	}
}

// AddContact stores (or replaces by ContactId) a contact. Ports AddContact.
func (b *InMemoryRelationshipsBoard) AddContact(c PersonContact) {
	b.mu.Lock()
	b.contacts[c.ContactId] = c
	b.mu.Unlock()
}

// GetContact returns the contact for id, or (zero,false). Ports GetContact.
func (b *InMemoryRelationshipsBoard) GetContact(id string) (PersonContact, bool) {
	b.mu.Lock()
	c, ok := b.contacts[id]
	b.mu.Unlock()
	return c, ok
}

// Contacts lists all contacts ordered by Name. Ports the Contacts property.
func (b *InMemoryRelationshipsBoard) Contacts() []PersonContact {
	b.mu.Lock()
	out := make([]PersonContact, 0, len(b.contacts))
	for _, c := range b.contacts {
		out = append(out, c)
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out
}

// AddImportantDate stores (or replaces by DateId) an important date. Ports
// AddImportantDate.
func (b *InMemoryRelationshipsBoard) AddImportantDate(d ImportantDate) {
	b.mu.Lock()
	b.dates[d.DateId] = d
	b.mu.Unlock()
}

// UpcomingThisMonth lists important dates in the current month by day. Ports
// UpcomingThisMonth (current month = DateTime.UtcNow.Month).
func (b *InMemoryRelationshipsBoard) UpcomingThisMonth() []ImportantDate {
	nowMonth := time.Now().UTC().Month()
	b.mu.Lock()
	out := make([]ImportantDate, 0)
	for _, d := range b.dates {
		if d.Date.Month() == nowMonth {
			out = append(out, d)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].Date.Day() < out[j].Date.Day() })
	return out
}

// RecordTouchpoint appends a touchpoint. Ports RecordTouchpoint.
func (b *InMemoryRelationshipsBoard) RecordTouchpoint(e ContactEvent) {
	b.mu.Lock()
	b.events = append(b.events, e)
	b.mu.Unlock()
}

// LastContact returns the most recent touchpoint time for a contact. Ports
// LastContact (null -> (zero,false)).
func (b *InMemoryRelationshipsBoard) LastContact(contactId string) (time.Time, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.lastContactLocked(contactId)
}

// lastContactLocked returns the newest touchpoint time for a contact. The caller
// must hold b.mu.
func (b *InMemoryRelationshipsBoard) lastContactLocked(contactId string) (time.Time, bool) {
	var newest time.Time
	found := false
	for _, e := range b.events {
		if e.ContactId == contactId {
			if !found || e.AtUtc.After(newest) {
				newest = e.AtUtc
				found = true
			}
		}
	}
	return newest, found
}

// NotContactedSince lists contacts last touched before cutoff (or never). Ports
// NotContactedSince.
func (b *InMemoryRelationshipsBoard) NotContactedSince(cutoff time.Time) []PersonContact {
	b.mu.Lock()
	out := make([]PersonContact, 0)
	for _, c := range b.contacts {
		last, ok := b.lastContactLocked(c.ContactId)
		if !ok || last.Before(cutoff) {
			out = append(out, c)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].ContactId < out[j].ContactId })
	return out
}

// Interface guard.
var _ RelationshipsBoard = (*InMemoryRelationshipsBoard)(nil)
