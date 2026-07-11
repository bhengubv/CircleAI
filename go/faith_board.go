// faith_board.go
//
// Ports the CircleAI.Faith primitive vertical (FaithPrimitives.cs):
//   FaithService / PrayerRequest / ScriptureReference (records)
//                            -> value structs
//   IFaithBoard              -> FaithBoard interface (I-prefix dropped)
//   InMemoryFaithBoard       -> InMemoryFaithBoard
//
// The FaithDomainContext / FaithCompanionAdapter (LLM glue) are out of scope.
//
// DETERMINISM: ServicesBetween orders by StartUtc ascending. RecentPrayers
// orders by SubmittedUtc descending then caps at limit. Lookup matches Tradition
// and Book case-sensitively (the C# == over strings) plus exact chapter/verse,
// returning the first hit. ByTradition matches case-insensitively and, because
// it mirrors an unordered ConcurrentDictionary in C#, sorts by ReferenceId for
// stable output.

package circleai

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// FaithService is a scheduled faith service. Ports the FaithService record.
type FaithService struct {
	ServiceId     string
	CommunityName string
	Title         string
	StartUtc      time.Time
	Location      string
}

// PrayerRequest is a submitted prayer request. Ports the PrayerRequest record.
type PrayerRequest struct {
	RequestId    string
	Author       string
	Body         string
	SubmittedUtc time.Time
	IsAnonymous  bool
}

// ScriptureReference is a scripture reference. Ports the ScriptureReference
// record.
type ScriptureReference struct {
	ReferenceId string
	Tradition   string
	Book        string
	Chapter     int
	Verse       int
	Text        string
}

// FaithBoard is the services/prayers/scripture board. Ports IFaithBoard.
type FaithBoard interface {
	Schedule(s FaithService)
	// ServicesBetween lists services in [start,end], oldest-first.
	ServicesBetween(start, end time.Time) []FaithService
	SubmitPrayer(r PrayerRequest)
	// RecentPrayers lists prayers newest-first, capped at limit.
	RecentPrayers(limit int) []PrayerRequest
	AddScripture(r ScriptureReference)
	// Lookup finds a reference by tradition/book (case-sensitive) and
	// chapter/verse; returns (zero,false) if absent.
	Lookup(tradition, book string, chapter, verse int) (ScriptureReference, bool)
	// ByTradition lists references of a tradition (case-insensitive), sorted by
	// ReferenceId.
	ByTradition(tradition string) []ScriptureReference
}

// InMemoryFaithBoard is a concurrency-safe in-memory FaithBoard. Ports
// InMemoryFaithBoard.
type InMemoryFaithBoard struct {
	mu        sync.Mutex
	services  map[string]FaithService
	prayers   []PrayerRequest
	scripture map[string]ScriptureReference
}

// NewInMemoryFaithBoard constructs an empty board.
func NewInMemoryFaithBoard() *InMemoryFaithBoard {
	return &InMemoryFaithBoard{
		services:  make(map[string]FaithService),
		scripture: make(map[string]ScriptureReference),
	}
}

// Schedule stores (or replaces by ServiceId) a service. Ports Schedule.
func (b *InMemoryFaithBoard) Schedule(s FaithService) {
	b.mu.Lock()
	b.services[s.ServiceId] = s
	b.mu.Unlock()
}

// ServicesBetween lists services in [start,end], oldest-first. Ports
// ServicesBetween.
func (b *InMemoryFaithBoard) ServicesBetween(start, end time.Time) []FaithService {
	b.mu.Lock()
	out := make([]FaithService, 0)
	for _, s := range b.services {
		if !s.StartUtc.Before(start) && !s.StartUtc.After(end) {
			out = append(out, s)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].StartUtc.Before(out[j].StartUtc) })
	return out
}

// SubmitPrayer appends a prayer request. Ports SubmitPrayer.
func (b *InMemoryFaithBoard) SubmitPrayer(r PrayerRequest) {
	b.mu.Lock()
	b.prayers = append(b.prayers, r)
	b.mu.Unlock()
}

// RecentPrayers lists prayers newest-first, capped at limit. Ports
// RecentPrayers.
func (b *InMemoryFaithBoard) RecentPrayers(limit int) []PrayerRequest {
	b.mu.Lock()
	out := make([]PrayerRequest, len(b.prayers))
	copy(out, b.prayers)
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].SubmittedUtc.After(out[j].SubmittedUtc) })
	if limit >= 0 && len(out) > limit {
		out = out[:limit]
	}
	return out
}

// AddScripture stores (or replaces by ReferenceId) a scripture reference. Ports
// AddScripture.
func (b *InMemoryFaithBoard) AddScripture(r ScriptureReference) {
	b.mu.Lock()
	b.scripture[r.ReferenceId] = r
	b.mu.Unlock()
}

// Lookup finds a reference by tradition/book and chapter/verse. Ports Lookup
// (null -> (zero,false)). Iteration order over the map is nondeterministic, but
// matching references are unique in practice, mirroring the C# FirstOrDefault.
func (b *InMemoryFaithBoard) Lookup(tradition, book string, chapter, verse int) (ScriptureReference, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	for _, r := range b.scripture {
		if r.Tradition == tradition && r.Book == book && r.Chapter == chapter && r.Verse == verse {
			return r, true
		}
	}
	return ScriptureReference{}, false
}

// ByTradition lists references of a tradition sorted by ReferenceId. Ports
// ByTradition.
func (b *InMemoryFaithBoard) ByTradition(tradition string) []ScriptureReference {
	b.mu.Lock()
	out := make([]ScriptureReference, 0)
	for _, r := range b.scripture {
		if strings.EqualFold(r.Tradition, tradition) {
			out = append(out, r)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].ReferenceId < out[j].ReferenceId })
	return out
}

// Interface guard.
var _ FaithBoard = (*InMemoryFaithBoard)(nil)
