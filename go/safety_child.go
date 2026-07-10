// safety_child.go
//
// Ports the CircleAI.Safety.Child module (namespace CircleAI.SafetyChild):
//   Records:    TrustedAdult, Geofence, CheckIn (ChildSafetyPrimitives.cs)
//   Interfaces: IChildSafetyBoard (ChildSafetyPrimitives.cs)
//   Impls:      InMemoryChildSafetyBoard (ChildSafetyPrimitives.cs)
//   Constants:  SafetyChildDomainContext (SafetyChildDomainContext.cs)
//
// The Child-Safety vertical is safeguarding: a priority-ordered trusted-adult
// ring, named geofences with a Haversine containment test, and per-child
// check-in history, held in a thread-safe in-memory board.
//
// Records are value structs in Go and are therefore never nil; the C#
// ArgumentNullException.ThrowIfNull guards on the mutators are structurally
// unrepresentable and so are dropped. RecentCheckIns preserves the C#
// ArgumentOutOfRangeException on a non-positive limit by panicking (a contract
// violation, matching the thrown exception). The Haversine implementation is a
// faithful translation of the C# private HaversineMeters (R = 6_371_000 m).

package circleai

import (
	"fmt"
	"math"
	"sort"
	"sync"
	"time"
)

// TrustedAdult is a member of a child's trusted-adult ring. Ports TrustedAdult.
type TrustedAdult struct {
	// AdultID is the stable identifier.
	AdultID string
	// Name is the adult's name.
	Name string
	// Phone is the adult's phone number.
	Phone string
	// Relationship describes the relationship to the child.
	Relationship string
	// RingPriority orders the ring ascending (lower == contacted first).
	RingPriority int
}

// Geofence is a named circular geofence. Ports Geofence.
type Geofence struct {
	// FenceID is the stable identifier.
	FenceID string
	// Name is the fence's name.
	Name string
	// CentreLat is the fence centre latitude in degrees.
	CentreLat float64
	// CentreLon is the fence centre longitude in degrees.
	CentreLon float64
	// RadiusMeters is the fence radius in metres.
	RadiusMeters float64
}

// CheckIn is a child check-in event. Ports CheckIn. Lat / Lon are pointers to
// model the C# nullable double? (nil == no coordinate).
type CheckIn struct {
	// ChildID is the child the check-in belongs to.
	ChildID string
	// Status is a free-form status label.
	Status string
	// Lat is the optional check-in latitude.
	Lat *float64
	// Lon is the optional check-in longitude.
	Lon *float64
	// AtUTC is when the check-in occurred.
	AtUTC time.Time
}

// IChildSafetyBoard is the child-safeguarding board contract. Ports
// IChildSafetyBoard.
type IChildSafetyBoard interface {
	// AddAdult adds (or replaces, keyed by AdultID) a trusted adult.
	AddAdult(a TrustedAdult)
	// RingOrdered returns the trusted-adult ring ordered by ascending priority.
	RingOrdered() []TrustedAdult
	// DefineGeofence adds (or replaces, keyed by FenceID) a geofence.
	DefineGeofence(g Geofence)
	// GetGeofence returns the geofence with id, or nil when unknown.
	GetGeofence(id string) *Geofence
	// IsInsideAnyFence reports whether (lat, lon) falls within any geofence.
	IsInsideAnyFence(lat, lon float64) bool
	// RecordCheckIn records a check-in event.
	RecordCheckIn(c CheckIn)
	// RecentCheckIns returns up to limit of childID's check-ins, newest-first.
	RecentCheckIns(childID string, limit int) []CheckIn
}

// InMemoryChildSafetyBoard is a thread-safe in-memory IChildSafetyBoard. Ports
// InMemoryChildSafetyBoard.
type InMemoryChildSafetyBoard struct {
	mu       sync.Mutex
	adults   map[string]TrustedAdult
	fences   map[string]Geofence
	checkIns []CheckIn
}

// NewInMemoryChildSafetyBoard constructs an empty board.
func NewInMemoryChildSafetyBoard() *InMemoryChildSafetyBoard {
	return &InMemoryChildSafetyBoard{
		adults: make(map[string]TrustedAdult),
		fences: make(map[string]Geofence),
	}
}

// AddAdult records or replaces a trusted adult keyed by AdultID. Ports
// InMemoryChildSafetyBoard.AddAdult.
func (b *InMemoryChildSafetyBoard) AddAdult(a TrustedAdult) {
	b.mu.Lock()
	b.adults[a.AdultID] = a
	b.mu.Unlock()
}

// RingOrdered returns the ring ordered by ascending RingPriority. Ports
// InMemoryChildSafetyBoard.RingOrdered (OrderBy(a => a.RingPriority)).
func (b *InMemoryChildSafetyBoard) RingOrdered() []TrustedAdult {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]TrustedAdult, 0, len(b.adults))
	for _, a := range b.adults {
		out = append(out, a)
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].RingPriority < out[j].RingPriority })
	return out
}

// DefineGeofence records or replaces a geofence keyed by FenceID. Ports
// InMemoryChildSafetyBoard.DefineGeofence.
func (b *InMemoryChildSafetyBoard) DefineGeofence(g Geofence) {
	b.mu.Lock()
	b.fences[g.FenceID] = g
	b.mu.Unlock()
}

// GetGeofence returns the geofence with id, or nil when unknown. Ports
// InMemoryChildSafetyBoard.GetGeofence.
func (b *InMemoryChildSafetyBoard) GetGeofence(id string) *Geofence {
	b.mu.Lock()
	defer b.mu.Unlock()
	g, ok := b.fences[id]
	if !ok {
		return nil
	}
	return &g
}

// IsInsideAnyFence reports whether (lat, lon) is within any defined geofence,
// by Haversine distance. Ports InMemoryChildSafetyBoard.IsInsideAnyFence.
func (b *InMemoryChildSafetyBoard) IsInsideAnyFence(lat, lon float64) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	for _, g := range b.fences {
		if haversineMeters(g.CentreLat, g.CentreLon, lat, lon) <= g.RadiusMeters {
			return true
		}
	}
	return false
}

// RecordCheckIn appends a check-in event. Ports
// InMemoryChildSafetyBoard.RecordCheckIn.
func (b *InMemoryChildSafetyBoard) RecordCheckIn(c CheckIn) {
	b.mu.Lock()
	b.checkIns = append(b.checkIns, c)
	b.mu.Unlock()
}

// RecentCheckIns returns up to limit of childID's check-ins, newest-first. It
// panics when limit <= 0, mirroring the C# ArgumentOutOfRangeException. Ports
// InMemoryChildSafetyBoard.RecentCheckIns.
func (b *InMemoryChildSafetyBoard) RecentCheckIns(childID string, limit int) []CheckIn {
	if limit <= 0 {
		panic(fmt.Sprintf("limit out of range: %d (must be positive)", limit))
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	matched := make([]CheckIn, 0)
	for _, c := range b.checkIns {
		if c.ChildID == childID {
			matched = append(matched, c)
		}
	}
	sort.SliceStable(matched, func(i, j int) bool { return matched[i].AtUTC.After(matched[j].AtUTC) })
	if len(matched) > limit {
		matched = matched[:limit]
	}
	return matched
}

// haversineMeters returns the great-circle distance in metres between two
// lat/lon points. Faithful translation of the C# private HaversineMeters
// (R = 6_371_000 m; degrees→radians; atan2 of the half-angle sines).
func haversineMeters(aLat, aLon, bLat, bLon float64) float64 {
	const r = 6_371_000.0
	degToRad := func(d float64) float64 { return d * math.Pi / 180.0 }
	dLat := degToRad(bLat - aLat)
	dLon := degToRad(bLon - aLon)
	s1 := math.Sin(dLat / 2)
	s2 := math.Sin(dLon / 2)
	a := s1*s1 + math.Cos(degToRad(aLat))*math.Cos(degToRad(bLat))*s2*s2
	c := 2 * math.Atan2(math.Sqrt(a), math.Sqrt(1-a))
	return r * c
}

// ─── SafetyChildDomainContext ──────────────────────────────────────────────

// safetyChildDomainContext holds the static domain descriptor for the
// Child-Safety vertical. Ports the static class SafetyChildDomainContext.
type safetyChildDomainContext struct{}

// SafetyChildDomainContext is the singleton domain descriptor accessor. Ports
// SafetyChildDomainContext.
var SafetyChildDomainContext = safetyChildDomainContext{}

// SystemPromptSnippet returns the domain system-prompt preamble. Ports
// SafetyChildDomainContext.SystemPromptSnippet.
func (safetyChildDomainContext) SystemPromptSnippet() string {
	return "[DOMAIN: Safety.Child] Child safety and safeguarding assistant for parents and educators. Help with online safety education, age-appropriate device rules, recognising grooming signs, reporting abuse, and digital literacy. Always prioritise child welfare. IMPORTANT: For immediate child safety concerns, contact SAPS (10111) or Childline (116). Compliance: Children's Act 38/2005, POPIA (children's data), FILMS_PUBLICATIONS_ACT, Cybercrimes Act."
}

// ComplianceFlags returns the compliance flags for the Child-Safety vertical.
// Ports SafetyChildDomainContext.ComplianceFlags.
func (safetyChildDomainContext) ComplianceFlags() []string {
	return []string{"Childrens_Act_38_2005", "POPIA_Children", "Films_Publications_Act", "Cybercrimes_Act", "Emergency_116"}
}

// SuggestedTools returns the suggested tool ids for the Child-Safety vertical.
// Ports SafetyChildDomainContext.SuggestedTools.
func (safetyChildDomainContext) SuggestedTools() []string {
	return []string{"parental_controls", "web_search", "document_editor", "reporting_tools"}
}

// Compile-time assertion that the implementation satisfies the contract.
var _ IChildSafetyBoard = (*InMemoryChildSafetyBoard)(nil)
