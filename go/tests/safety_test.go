// safety_test.go
//
// Verifies the CircleAI.Safety port (safety.go):
//   - IncidentSeverity ordinals (Info=0 < Warning=1 < Critical=2 < Emergency=3).
//   - InMemorySafetyBoard: Log + Active newest-first; AtOrAboveSeverity filters
//     by ordinal and orders newest-first; NoteHazard replaces by id and orders
//     newest-noted first; contacts preserve insertion order; FirstContact.
//   - SafetyDomainContext constants.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestIncidentSeverity_Ordinals(t *testing.T) {
	if circleai.IncidentSeverityInfo != 0 || circleai.IncidentSeverityWarning != 1 ||
		circleai.IncidentSeverityCritical != 2 || circleai.IncidentSeverityEmergency != 3 {
		t.Fatalf("ordinals drifted: info=%d warn=%d crit=%d emerg=%d",
			circleai.IncidentSeverityInfo, circleai.IncidentSeverityWarning,
			circleai.IncidentSeverityCritical, circleai.IncidentSeverityEmergency)
	}
	if circleai.IncidentSeverityEmergency.String() != "Emergency" ||
		circleai.IncidentSeverityInfo.String() != "Info" {
		t.Errorf("severity names drifted")
	}
}

func TestSafetyBoard_ActiveNewestFirst(t *testing.T) {
	b := circleai.NewInMemorySafetyBoard()
	base := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	b.Log(circleai.Incident{IncidentID: "old", Severity: circleai.IncidentSeverityInfo, AtUTC: base})
	b.Log(circleai.Incident{IncidentID: "new", Severity: circleai.IncidentSeverityWarning, AtUTC: base.Add(time.Hour)})
	b.Log(circleai.Incident{IncidentID: "mid", Severity: circleai.IncidentSeverityInfo, AtUTC: base.Add(30 * time.Minute)})

	active := b.Active()
	if len(active) != 3 {
		t.Fatalf("active count: got %d, want 3", len(active))
	}
	if active[0].IncidentID != "new" || active[1].IncidentID != "mid" || active[2].IncidentID != "old" {
		t.Errorf("order: got %s,%s,%s want new,mid,old",
			active[0].IncidentID, active[1].IncidentID, active[2].IncidentID)
	}
}

func TestSafetyBoard_AtOrAboveSeverity(t *testing.T) {
	b := circleai.NewInMemorySafetyBoard()
	base := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	b.Log(circleai.Incident{IncidentID: "info", Severity: circleai.IncidentSeverityInfo, AtUTC: base})
	b.Log(circleai.Incident{IncidentID: "warn", Severity: circleai.IncidentSeverityWarning, AtUTC: base.Add(time.Minute)})
	b.Log(circleai.Incident{IncidentID: "crit", Severity: circleai.IncidentSeverityCritical, AtUTC: base.Add(2 * time.Minute)})
	b.Log(circleai.Incident{IncidentID: "emerg", Severity: circleai.IncidentSeverityEmergency, AtUTC: base.Add(3 * time.Minute)})

	got := b.AtOrAboveSeverity(circleai.IncidentSeverityCritical)
	if len(got) != 2 {
		t.Fatalf("at-or-above Critical: got %d, want 2", len(got))
	}
	// newest-first: emerg then crit.
	if got[0].IncidentID != "emerg" || got[1].IncidentID != "crit" {
		t.Errorf("order: got %s,%s want emerg,crit", got[0].IncidentID, got[1].IncidentID)
	}

	if all := b.AtOrAboveSeverity(circleai.IncidentSeverityInfo); len(all) != 4 {
		t.Errorf("at-or-above Info should be all 4, got %d", len(all))
	}
	if none := b.AtOrAboveSeverity(circleai.IncidentSeverityEmergency); len(none) != 1 {
		t.Errorf("at-or-above Emergency should be 1, got %d", len(none))
	}
}

func TestSafetyBoard_Coordinates(t *testing.T) {
	b := circleai.NewInMemorySafetyBoard()
	lat, lon := -26.2041, 28.0473
	b.Log(circleai.Incident{IncidentID: "geo", Severity: circleai.IncidentSeverityInfo, Latitude: &lat, Longitude: &lon, AtUTC: time.Now().UTC()})
	got := b.Active()
	if got[0].Latitude == nil || *got[0].Latitude != lat || got[0].Longitude == nil || *got[0].Longitude != lon {
		t.Error("coordinates not preserved")
	}
	// No-coordinate incident keeps nil pointers.
	b2 := circleai.NewInMemorySafetyBoard()
	b2.Log(circleai.Incident{IncidentID: "nogeo", AtUTC: time.Now().UTC()})
	if b2.Active()[0].Latitude != nil {
		t.Error("absent latitude should be nil")
	}
}

func TestSafetyBoard_HazardsReplaceAndOrder(t *testing.T) {
	b := circleai.NewInMemorySafetyBoard()
	base := time.Date(2026, 7, 10, 8, 0, 0, 0, time.UTC)
	b.NoteHazard(circleai.Hazard{HazardID: "h1", Description: "first", Category: "fire", NotedUTC: base})
	b.NoteHazard(circleai.Hazard{HazardID: "h2", Description: "second", Category: "flood", NotedUTC: base.Add(time.Hour)})
	// Replace h1 with a newer note.
	b.NoteHazard(circleai.Hazard{HazardID: "h1", Description: "updated", Category: "fire", NotedUTC: base.Add(2 * time.Hour)})

	hz := b.Hazards()
	if len(hz) != 2 {
		t.Fatalf("hazards: got %d, want 2 (h1 replaced not duplicated)", len(hz))
	}
	// newest-noted first: updated h1 (base+2h) then h2 (base+1h).
	if hz[0].HazardID != "h1" || hz[0].Description != "updated" || hz[1].HazardID != "h2" {
		t.Errorf("hazard order/replace: got %+v", hz)
	}
}

func TestSafetyBoard_Contacts(t *testing.T) {
	b := circleai.NewInMemorySafetyBoard()
	if b.FirstContact() != nil {
		t.Error("empty board FirstContact should be nil")
	}
	b.AddContact(circleai.EmergencyContact{ContactID: "c1", Name: "Alice", Phone: "111", Relationship: "sister"})
	b.AddContact(circleai.EmergencyContact{ContactID: "c2", Name: "Bob", Phone: "222", Relationship: "friend"})

	first := b.FirstContact()
	if first == nil || first.ContactID != "c1" {
		t.Errorf("first contact: got %+v, want c1", first)
	}
	all := b.Contacts()
	if len(all) != 2 || all[0].ContactID != "c1" || all[1].ContactID != "c2" {
		t.Errorf("contacts order: got %+v", all)
	}
}

func TestSafetyDomainContext(t *testing.T) {
	if circleai.SafetyDomainContext.SystemPromptSnippet() == "" {
		t.Error("system prompt snippet empty")
	}
	flags := circleai.SafetyDomainContext.ComplianceFlags()
	if len(flags) != 3 || flags[0] != "POPIA" || flags[2] != "Emergency_Protocol_10111" {
		t.Errorf("compliance flags: %v", flags)
	}
	tools := circleai.SafetyDomainContext.SuggestedTools()
	if len(tools) != 4 || tools[0] != "emergency_contacts" {
		t.Errorf("suggested tools: %v", tools)
	}
}
