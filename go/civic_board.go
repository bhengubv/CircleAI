// civic_board.go
//
// Ports the CircleAI.Civic primitive vertical (CivicPrimitives.cs):
//   CivicIssue / Representative / CivicEvent (records) -> value structs
//   ICivicBoard              -> CivicBoard interface (I-prefix dropped)
//   InMemoryCivicBoard       -> InMemoryCivicBoard
//
// The CivicDomainContext / CivicCompanionAdapter (LLM glue) are out of scope.
//
// DETERMINISM: the three stores mirror ConcurrentDictionaries in C# (no defined
// order); OpenIssues sorts by IssueId and RepsForDistrict by RepId for stable
// output. UpcomingEvents orders by AtUtc ascending. Representative.District is a
// *string to model the C# nullable string (a nil district never matches).

package circleai

import (
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// CivicIssue is a reported civic issue. Ports the CivicIssue record.
type CivicIssue struct {
	IssueId      string
	Category     string
	Description  string
	Lat          float64
	Lon          float64
	ReportedUtc  time.Time
	Status       string
}

// Representative is an elected/appointed representative. Ports the
// Representative record. District is a *string to model the C# nullable string.
type Representative struct {
	RepId        string
	Name         string
	Office       string
	ContactEmail string
	District     *string
}

// CivicEvent is a scheduled civic event. Ports the CivicEvent record.
type CivicEvent struct {
	EventId  string
	Title    string
	AtUtc    time.Time
	Location string
	Audience string
}

// CivicBoard is the issues/representatives/events board. Ports ICivicBoard.
type CivicBoard interface {
	Report(i CivicIssue)
	// Resolve sets an issue's status; errors on unknown id.
	Resolve(issueId, status string) error
	// OpenIssues lists issues whose status is not "Resolved" (case-insensitive),
	// sorted by IssueId.
	OpenIssues() []CivicIssue
	AddRep(r Representative)
	// RepsForDistrict lists reps in a district (case-insensitive), sorted by RepId.
	RepsForDistrict(district string) []Representative
	Schedule(e CivicEvent)
	// UpcomingEvents lists future events ordered by AtUtc.
	UpcomingEvents() []CivicEvent
}

// InMemoryCivicBoard is a concurrency-safe in-memory CivicBoard. Ports
// InMemoryCivicBoard.
type InMemoryCivicBoard struct {
	mu     sync.Mutex
	issues map[string]CivicIssue
	reps   map[string]Representative
	events map[string]CivicEvent
}

// NewInMemoryCivicBoard constructs an empty board.
func NewInMemoryCivicBoard() *InMemoryCivicBoard {
	return &InMemoryCivicBoard{
		issues: make(map[string]CivicIssue),
		reps:   make(map[string]Representative),
		events: make(map[string]CivicEvent),
	}
}

// Report stores (or replaces by IssueId) an issue. Ports Report.
func (b *InMemoryCivicBoard) Report(i CivicIssue) {
	b.mu.Lock()
	b.issues[i.IssueId] = i
	b.mu.Unlock()
}

// Resolve updates an issue's status. Ports Resolve (throws on unknown id ->
// error).
func (b *InMemoryCivicBoard) Resolve(issueId, status string) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	i, ok := b.issues[issueId]
	if !ok {
		return errors.New("Unknown issue " + issueId)
	}
	i.Status = status
	b.issues[issueId] = i
	return nil
}

// OpenIssues lists unresolved issues sorted by IssueId. Ports OpenIssues.
func (b *InMemoryCivicBoard) OpenIssues() []CivicIssue {
	b.mu.Lock()
	out := make([]CivicIssue, 0)
	for _, i := range b.issues {
		if !strings.EqualFold(i.Status, "Resolved") {
			out = append(out, i)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(a, c int) bool { return out[a].IssueId < out[c].IssueId })
	return out
}

// AddRep stores (or replaces by RepId) a representative. Ports AddRep.
func (b *InMemoryCivicBoard) AddRep(r Representative) {
	b.mu.Lock()
	b.reps[r.RepId] = r
	b.mu.Unlock()
}

// RepsForDistrict lists reps in a district sorted by RepId. Ports
// RepsForDistrict.
func (b *InMemoryCivicBoard) RepsForDistrict(district string) []Representative {
	b.mu.Lock()
	out := make([]Representative, 0)
	for _, r := range b.reps {
		if r.District != nil && strings.EqualFold(*r.District, district) {
			out = append(out, r)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].RepId < out[j].RepId })
	return out
}

// Schedule stores (or replaces by EventId) an event. Ports Schedule.
func (b *InMemoryCivicBoard) Schedule(e CivicEvent) {
	b.mu.Lock()
	b.events[e.EventId] = e
	b.mu.Unlock()
}

// UpcomingEvents lists future events ordered by AtUtc. Ports UpcomingEvents
// (future = AtUtc >= now UTC).
func (b *InMemoryCivicBoard) UpcomingEvents() []CivicEvent {
	now := time.Now().UTC()
	b.mu.Lock()
	out := make([]CivicEvent, 0)
	for _, e := range b.events {
		if !e.AtUtc.Before(now) {
			out = append(out, e)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.Before(out[j].AtUtc) })
	return out
}

// Interface guard.
var _ CivicBoard = (*InMemoryCivicBoard)(nil)
