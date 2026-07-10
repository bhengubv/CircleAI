// safety.go
//
// Ports the CircleAI.Safety module:
//   Enums:      IncidentSeverity (SafetyPrimitives.cs)
//   Records:    Incident, Hazard, EmergencyContact (SafetyPrimitives.cs)
//   Interfaces: ISafetyBoard (SafetyPrimitives.cs)
//   Impls:      InMemorySafetyBoard (SafetyPrimitives.cs)
//   Constants:  SafetyDomainContext (SafetyDomainContext.cs)
//
// The Safety vertical is personal safety and emergency preparedness: incidents,
// hazards, emergency contacts and severity routing, held in a thread-safe
// in-memory board.
//
// Records are value structs in Go and are therefore never nil; the C#
// ArgumentNullException.ThrowIfNull guards on the mutators are structurally
// unrepresentable and so are dropped (matching the value-struct board precedent
// in security_node_trust_registry.go). List accessors return snapshot copies
// ordered exactly as the C# LINQ queries specify.

package circleai

import (
	"sort"
	"sync"
	"time"
)

// IncidentSeverity is the severity band for a safety incident. Ports
// IncidentSeverity; ordinals are load-bearing and used by AtOrAboveSeverity
// (Info=0 < Warning=1 < Critical=2 < Emergency=3).
type IncidentSeverity int

const (
	// IncidentSeverityInfo is an informational note.
	IncidentSeverityInfo IncidentSeverity = 0
	// IncidentSeverityWarning is a non-urgent warning.
	IncidentSeverityWarning IncidentSeverity = 1
	// IncidentSeverityCritical is an urgent, serious incident.
	IncidentSeverityCritical IncidentSeverity = 2
	// IncidentSeverityEmergency is a life-threatening emergency.
	IncidentSeverityEmergency IncidentSeverity = 3
)

// String returns the C# enum member name for the severity.
func (s IncidentSeverity) String() string {
	switch s {
	case IncidentSeverityInfo:
		return "Info"
	case IncidentSeverityWarning:
		return "Warning"
	case IncidentSeverityCritical:
		return "Critical"
	case IncidentSeverityEmergency:
		return "Emergency"
	default:
		return "Info"
	}
}

// Incident is a logged safety incident. Ports Incident. Latitude / Longitude are
// pointers to model the C# nullable double? (nil == no coordinate).
type Incident struct {
	// IncidentID is the stable identifier.
	IncidentID string
	// Severity is the incident severity band.
	Severity IncidentSeverity
	// Description is a human-readable summary.
	Description string
	// Latitude is the optional incident latitude.
	Latitude *float64
	// Longitude is the optional incident longitude.
	Longitude *float64
	// AtUTC is when the incident occurred.
	AtUTC time.Time
}

// Hazard is a noted environmental hazard. Ports Hazard.
type Hazard struct {
	// HazardID is the stable identifier.
	HazardID string
	// Description is a human-readable summary.
	Description string
	// Category is the hazard class.
	Category string
	// NotedUTC is when the hazard was noted.
	NotedUTC time.Time
}

// EmergencyContact is a person to reach in an emergency. Ports EmergencyContact.
type EmergencyContact struct {
	// ContactID is the stable identifier.
	ContactID string
	// Name is the contact's name.
	Name string
	// Phone is the contact's phone number.
	Phone string
	// Relationship describes the relationship to the user.
	Relationship string
}

// ISafetyBoard is the personal-safety board contract. Ports ISafetyBoard.
type ISafetyBoard interface {
	// Log records an incident.
	Log(i Incident)
	// Active returns all incidents, newest-first.
	Active() []Incident
	// AtOrAboveSeverity returns incidents at or above minimum, newest-first.
	AtOrAboveSeverity(minimum IncidentSeverity) []Incident
	// NoteHazard records (or replaces, keyed by HazardID) a hazard.
	NoteHazard(h Hazard)
	// Hazards returns all hazards, newest-noted first.
	Hazards() []Hazard
	// AddContact adds an emergency contact.
	AddContact(c EmergencyContact)
	// FirstContact returns the first-added contact, or nil when there are none.
	FirstContact() *EmergencyContact
	// Contacts returns all emergency contacts in insertion order.
	Contacts() []EmergencyContact
}

// InMemorySafetyBoard is a thread-safe in-memory ISafetyBoard. Ports
// InMemorySafetyBoard.
type InMemorySafetyBoard struct {
	mu        sync.Mutex
	incidents []Incident
	hazards   map[string]Hazard
	contacts  []EmergencyContact
}

// NewInMemorySafetyBoard constructs an empty board.
func NewInMemorySafetyBoard() *InMemorySafetyBoard {
	return &InMemorySafetyBoard{hazards: make(map[string]Hazard)}
}

// Log records an incident. Ports InMemorySafetyBoard.Log.
func (b *InMemorySafetyBoard) Log(i Incident) {
	b.mu.Lock()
	b.incidents = append(b.incidents, i)
	b.mu.Unlock()
}

// Active returns all incidents ordered newest-first. Ports
// InMemorySafetyBoard.Active (OrderByDescending(i => i.AtUtc)).
func (b *InMemorySafetyBoard) Active() []Incident {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]Incident, len(b.incidents))
	copy(out, b.incidents)
	sortIncidentsByAtDesc(out)
	return out
}

// AtOrAboveSeverity returns incidents whose severity ordinal is >= minimum,
// newest-first. Ports InMemorySafetyBoard.AtOrAboveSeverity.
func (b *InMemorySafetyBoard) AtOrAboveSeverity(minimum IncidentSeverity) []Incident {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]Incident, 0, len(b.incidents))
	for _, i := range b.incidents {
		if int(i.Severity) >= int(minimum) {
			out = append(out, i)
		}
	}
	sortIncidentsByAtDesc(out)
	return out
}

// NoteHazard records or replaces a hazard keyed by HazardID. Ports
// InMemorySafetyBoard.NoteHazard.
func (b *InMemorySafetyBoard) NoteHazard(h Hazard) {
	b.mu.Lock()
	b.hazards[h.HazardID] = h
	b.mu.Unlock()
}

// Hazards returns all hazards ordered newest-noted first. Ports
// InMemorySafetyBoard.Hazards (OrderByDescending(h => h.NotedUtc)).
func (b *InMemorySafetyBoard) Hazards() []Hazard {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]Hazard, 0, len(b.hazards))
	for _, h := range b.hazards {
		out = append(out, h)
	}
	sort.SliceStable(out, func(x, y int) bool { return out[x].NotedUTC.After(out[y].NotedUTC) })
	return out
}

// AddContact appends an emergency contact. Ports InMemorySafetyBoard.AddContact.
func (b *InMemorySafetyBoard) AddContact(c EmergencyContact) {
	b.mu.Lock()
	b.contacts = append(b.contacts, c)
	b.mu.Unlock()
}

// FirstContact returns the first-added contact, or nil when there are none.
// Ports InMemorySafetyBoard.FirstContact (_contacts.FirstOrDefault()).
func (b *InMemorySafetyBoard) FirstContact() *EmergencyContact {
	b.mu.Lock()
	defer b.mu.Unlock()
	if len(b.contacts) == 0 {
		return nil
	}
	c := b.contacts[0]
	return &c
}

// Contacts returns all emergency contacts in insertion order. Ports
// InMemorySafetyBoard.Contacts.
func (b *InMemorySafetyBoard) Contacts() []EmergencyContact {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]EmergencyContact, len(b.contacts))
	copy(out, b.contacts)
	return out
}

// sortIncidentsByAtDesc orders incidents newest-first, mirroring the C#
// OrderByDescending(i => i.AtUtc). A stable sort preserves the relative order of
// equal timestamps.
func sortIncidentsByAtDesc(xs []Incident) {
	sort.SliceStable(xs, func(i, j int) bool { return xs[i].AtUTC.After(xs[j].AtUTC) })
}

// ─── SafetyDomainContext ───────────────────────────────────────────────────

// SafetyDomainContext holds the static domain descriptor for the Safety
// vertical. Ports the static class SafetyDomainContext.
type safetyDomainContext struct{}

// SafetyDomainContext is the singleton domain descriptor accessor. Ports
// SafetyDomainContext.
var SafetyDomainContext = safetyDomainContext{}

// SystemPromptSnippet returns the domain system-prompt preamble. Ports
// SafetyDomainContext.SystemPromptSnippet.
func (safetyDomainContext) SystemPromptSnippet() string {
	return "[DOMAIN: Safety] Personal safety and emergency preparedness assistant. Help with home security assessments, emergency response plans, first aid guidance (always recommend professional training), situational awareness tips, and crisis communication. IMPORTANT: For life-threatening emergencies, direct immediately to 10111 (SAPS) or 10177 (ambulance). Compliance: POPIA, OHS Act."
}

// ComplianceFlags returns the compliance flags for the Safety vertical. Ports
// SafetyDomainContext.ComplianceFlags.
func (safetyDomainContext) ComplianceFlags() []string {
	return []string{"POPIA", "OHS_Act", "Emergency_Protocol_10111"}
}

// SuggestedTools returns the suggested tool ids for the Safety vertical. Ports
// SafetyDomainContext.SuggestedTools.
func (safetyDomainContext) SuggestedTools() []string {
	return []string{"emergency_contacts", "document_editor", "map", "web_search"}
}

// Compile-time assertion that the implementation satisfies the contract.
var _ ISafetyBoard = (*InMemorySafetyBoard)(nil)
