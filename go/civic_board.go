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
	IssueId     string
	Category    string
	Description string
	Lat         float64
	Lon         float64
	ReportedUtc time.Time
	Status      string
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

// CivicCategoryCount is one bucket of OpenIssueBreakdown: an issue category and
// how many open issues fall under it. Ports the C# value tuple
// (string Category, int Count).
type CivicCategoryCount struct {
	Category string
	Count    int
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
	// OpenIssueCount returns the number of unresolved issues.
	OpenIssueCount() int
	// IssuesByCategory lists issues in a category (case-insensitive), newest-first.
	IssuesByCategory(category string) []CivicIssue
	// RemoveRep drops a representative by id, returning true if present.
	RemoveRep(repId string) bool
	// RepsForOffice lists reps holding an office (case-insensitive), ordered by Name.
	RepsForOffice(office string) []Representative
	// EventsForAudience lists events for an audience (case-insensitive), ordered by AtUtc.
	EventsForAudience(audience string) []CivicEvent
	// OpenIssueBreakdown counts open issues per category, most-common first.
	OpenIssueBreakdown() []CivicCategoryCount
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

// OpenIssueCount returns the number of unresolved issues. Ports
// InMemoryCivicBoard.OpenIssueCount (OpenIssues().Count).
func (b *InMemoryCivicBoard) OpenIssueCount() int {
	b.mu.Lock()
	defer b.mu.Unlock()
	n := 0
	for _, i := range b.issues {
		if !strings.EqualFold(i.Status, "Resolved") {
			n++
		}
	}
	return n
}

// IssuesByCategory lists issues filed under a category (case-insensitive),
// ordered by ReportedUtc descending. Ports InMemoryCivicBoard.IssuesByCategory.
func (b *InMemoryCivicBoard) IssuesByCategory(category string) []CivicIssue {
	b.mu.Lock()
	out := make([]CivicIssue, 0)
	for _, i := range b.issues {
		if strings.EqualFold(i.Category, category) {
			out = append(out, i)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(a, c int) bool { return out[a].ReportedUtc.After(out[c].ReportedUtc) })
	return out
}

// RemoveRep drops a representative by id, returning true if present. Ports
// InMemoryCivicBoard.RemoveRep (TryRemove).
func (b *InMemoryCivicBoard) RemoveRep(repId string) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	_, ok := b.reps[repId]
	delete(b.reps, repId)
	return ok
}

// RepsForOffice lists reps holding an office (case-insensitive), ordered by Name
// (OrdinalIgnoreCase). Ports InMemoryCivicBoard.RepsForOffice.
func (b *InMemoryCivicBoard) RepsForOffice(office string) []Representative {
	b.mu.Lock()
	out := make([]Representative, 0)
	for _, r := range b.reps {
		if strings.EqualFold(r.Office, office) {
			out = append(out, r)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return ordinalIgnoreCaseLess(out[i].Name, out[j].Name) })
	return out
}

// EventsForAudience lists events for an audience (case-insensitive), ordered by
// AtUtc ascending. Ports InMemoryCivicBoard.EventsForAudience.
func (b *InMemoryCivicBoard) EventsForAudience(audience string) []CivicEvent {
	b.mu.Lock()
	out := make([]CivicEvent, 0)
	for _, e := range b.events {
		if strings.EqualFold(e.Audience, audience) {
			out = append(out, e)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.Before(out[j].AtUtc) })
	return out
}

// OpenIssueBreakdown counts open issues per category (case-insensitive grouping,
// keeping the first-encountered spelling), ordered by Count descending. It is
// built from the deterministic OpenIssues() ordering (IssueId-sorted) so ties
// resolve deterministically. Ports InMemoryCivicBoard.OpenIssueBreakdown.
func (b *InMemoryCivicBoard) OpenIssueBreakdown() []CivicCategoryCount {
	open := b.OpenIssues() // IssueId-sorted snapshot
	order := make([]string, 0)
	byKey := make(map[string]*CivicCategoryCount)
	for _, i := range open {
		key := strings.ToUpper(i.Category)
		g, ok := byKey[key]
		if !ok {
			g = &CivicCategoryCount{Category: i.Category, Count: 0}
			byKey[key] = g
			order = append(order, key)
		}
		g.Count++
	}
	out := make([]CivicCategoryCount, 0, len(order))
	for _, key := range order {
		out = append(out, *byKey[key])
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].Count > out[j].Count })
	return out
}

// Interface guard.
var _ CivicBoard = (*InMemoryCivicBoard)(nil)
