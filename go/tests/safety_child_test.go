// safety_child_test.go
//
// Verifies the CircleAI.Safety.Child port (safety_child.go):
//   - InMemoryChildSafetyBoard: AddAdult replaces by id; RingOrdered sorts by
//     ascending RingPriority; DefineGeofence + GetGeofence; IsInsideAnyFence via
//     Haversine (inside / outside / boundary); RecordCheckIn + RecentCheckIns
//     filters by child, orders newest-first, honours limit; non-positive limit
//     panics (ArgumentOutOfRangeException parity).
//   - SafetyChildDomainContext constants.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestChildBoard_RingOrderedByPriority(t *testing.T) {
	b := circleai.NewInMemoryChildSafetyBoard()
	b.AddAdult(circleai.TrustedAdult{AdultID: "a", Name: "Aunt", RingPriority: 3})
	b.AddAdult(circleai.TrustedAdult{AdultID: "b", Name: "Mum", RingPriority: 1})
	b.AddAdult(circleai.TrustedAdult{AdultID: "c", Name: "Teacher", RingPriority: 2})

	ring := b.RingOrdered()
	if len(ring) != 3 {
		t.Fatalf("ring size: got %d, want 3", len(ring))
	}
	if ring[0].AdultID != "b" || ring[1].AdultID != "c" || ring[2].AdultID != "a" {
		t.Errorf("ring order: got %s,%s,%s want b,c,a", ring[0].AdultID, ring[1].AdultID, ring[2].AdultID)
	}
}

func TestChildBoard_AddAdultReplacesById(t *testing.T) {
	b := circleai.NewInMemoryChildSafetyBoard()
	b.AddAdult(circleai.TrustedAdult{AdultID: "a", Name: "Old", RingPriority: 5})
	b.AddAdult(circleai.TrustedAdult{AdultID: "a", Name: "New", RingPriority: 1})
	ring := b.RingOrdered()
	if len(ring) != 1 || ring[0].Name != "New" || ring[0].RingPriority != 1 {
		t.Errorf("replace-by-id failed: %+v", ring)
	}
}

func TestChildBoard_Geofence(t *testing.T) {
	b := circleai.NewInMemoryChildSafetyBoard()
	// Johannesburg CBD-ish centre, 500 m radius.
	b.DefineGeofence(circleai.Geofence{FenceID: "school", Name: "School", CentreLat: -26.2041, CentreLon: 28.0473, RadiusMeters: 500})

	if g := b.GetGeofence("school"); g == nil || g.Name != "School" {
		t.Errorf("get geofence: got %+v", g)
	}
	if b.GetGeofence("nope") != nil {
		t.Error("unknown geofence should be nil")
	}

	// A point ~50 m away (small delta) is inside.
	if !b.IsInsideAnyFence(-26.2045, 28.0475) {
		t.Error("nearby point should be inside the 500 m fence")
	}
	// A point ~10 km away is outside.
	if b.IsInsideAnyFence(-26.30, 28.15) {
		t.Error("far point should be outside the fence")
	}
}

func TestChildBoard_IsInsideAnyFence_MultipleAndEmpty(t *testing.T) {
	b := circleai.NewInMemoryChildSafetyBoard()
	// No fences → never inside.
	if b.IsInsideAnyFence(0, 0) {
		t.Error("no fences should mean not inside")
	}
	b.DefineGeofence(circleai.Geofence{FenceID: "f1", CentreLat: 0, CentreLon: 0, RadiusMeters: 100})
	b.DefineGeofence(circleai.Geofence{FenceID: "f2", CentreLat: 51.5, CentreLon: -0.12, RadiusMeters: 1000})
	// Inside the London fence, outside the equator fence → still true (any).
	if !b.IsInsideAnyFence(51.5005, -0.12) {
		t.Error("point inside second fence should return true")
	}
}

func TestChildBoard_RecentCheckIns(t *testing.T) {
	b := circleai.NewInMemoryChildSafetyBoard()
	base := time.Date(2026, 7, 10, 9, 0, 0, 0, time.UTC)
	b.RecordCheckIn(circleai.CheckIn{ChildID: "kid1", Status: "home", AtUTC: base})
	b.RecordCheckIn(circleai.CheckIn{ChildID: "kid1", Status: "school", AtUTC: base.Add(time.Hour)})
	b.RecordCheckIn(circleai.CheckIn{ChildID: "kid2", Status: "park", AtUTC: base.Add(2 * time.Hour)})
	b.RecordCheckIn(circleai.CheckIn{ChildID: "kid1", Status: "park", AtUTC: base.Add(3 * time.Hour)})

	got := b.RecentCheckIns("kid1", 20)
	if len(got) != 3 {
		t.Fatalf("kid1 check-ins: got %d, want 3 (kid2 excluded)", len(got))
	}
	// newest-first.
	if got[0].Status != "park" || got[1].Status != "school" || got[2].Status != "home" {
		t.Errorf("order: got %s,%s,%s want park,school,home", got[0].Status, got[1].Status, got[2].Status)
	}

	// Limit truncates to the newest N.
	limited := b.RecentCheckIns("kid1", 2)
	if len(limited) != 2 || limited[0].Status != "park" || limited[1].Status != "school" {
		t.Errorf("limited: %+v", limited)
	}

	// Unknown child → empty.
	if len(b.RecentCheckIns("ghost", 5)) != 0 {
		t.Error("unknown child should have no check-ins")
	}
}

func TestChildBoard_RecentCheckInsNonPositiveLimitPanics(t *testing.T) {
	b := circleai.NewInMemoryChildSafetyBoard()
	defer func() {
		if r := recover(); r == nil {
			t.Error("limit <= 0 should panic (ArgumentOutOfRangeException parity)")
		}
	}()
	b.RecentCheckIns("kid1", 0)
}

func TestSafetyChildDomainContext(t *testing.T) {
	if circleai.SafetyChildDomainContext.SystemPromptSnippet() == "" {
		t.Error("system prompt snippet empty")
	}
	flags := circleai.SafetyChildDomainContext.ComplianceFlags()
	if len(flags) != 5 || flags[0] != "Childrens_Act_38_2005" || flags[4] != "Emergency_116" {
		t.Errorf("compliance flags: %v", flags)
	}
	tools := circleai.SafetyChildDomainContext.SuggestedTools()
	if len(tools) != 4 || tools[0] != "parental_controls" {
		t.Errorf("suggested tools: %v", tools)
	}
}
