// relationships_board_test.go
//
// Verifies the CircleAI.Relationships port (relationships_board.go): contact
// add/get + sorted listing, important dates this month (ordered by day),
// touchpoint tracking with last-contact, and not-contacted-since filtering.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestRelationships_ContactsAndDates(t *testing.T) {
	b := circleai.NewInMemoryRelationshipsBoard()
	b.AddContact(circleai.PersonContact{ContactId: "c1", Name: "Zara", Relationship: "friend"})
	b.AddContact(circleai.PersonContact{ContactId: "c2", Name: "Alex", Relationship: "family"})
	if got, ok := b.GetContact("c1"); !ok || got.Name != "Zara" {
		t.Fatalf("get contact = %+v ok=%v", got, ok)
	}
	all := b.Contacts()
	if len(all) != 2 || all[0].Name != "Alex" || all[1].Name != "Zara" {
		t.Fatalf("contacts sorted by Name failed: %+v", all)
	}

	// Two dates in the current month, added out of day order.
	now := time.Now().UTC()
	d20 := time.Date(now.Year(), now.Month(), 20, 0, 0, 0, 0, time.UTC)
	d05 := time.Date(now.Year(), now.Month(), 5, 0, 0, 0, 0, time.UTC)
	// A date in a different month, which must be excluded.
	other := time.Date(now.Year(), now.Month(), 5, 0, 0, 0, 0, time.UTC).AddDate(0, 6, 0)
	b.AddImportantDate(circleai.ImportantDate{DateId: "x1", ContactId: "c1", Kind: "birthday", Date: d20})
	b.AddImportantDate(circleai.ImportantDate{DateId: "x2", ContactId: "c2", Kind: "anniversary", Date: d05})
	b.AddImportantDate(circleai.ImportantDate{DateId: "x3", ContactId: "c1", Kind: "birthday", Date: other})

	up := b.UpcomingThisMonth()
	if len(up) != 2 || up[0].DateId != "x2" || up[1].DateId != "x1" {
		t.Fatalf("upcoming-this-month ordered by day failed: %+v", up)
	}
}

func TestRelationships_TouchpointsAndStale(t *testing.T) {
	b := circleai.NewInMemoryRelationshipsBoard()
	b.AddContact(circleai.PersonContact{ContactId: "c1", Name: "Zara"})
	b.AddContact(circleai.PersonContact{ContactId: "c2", Name: "Alex"})
	now := time.Now().UTC()
	b.RecordTouchpoint(circleai.ContactEvent{ContactId: "c1", Kind: "call", AtUtc: now.Add(-2 * time.Hour)})
	b.RecordTouchpoint(circleai.ContactEvent{ContactId: "c1", Kind: "text", AtUtc: now.Add(-30 * time.Minute)})

	last, ok := b.LastContact("c1")
	if !ok || !last.Equal(now.Add(-30*time.Minute)) {
		t.Fatalf("last contact = %v ok=%v", last, ok)
	}
	if _, ok := b.LastContact("c2"); ok {
		t.Fatalf("last contact for never-touched must be false")
	}

	// Cutoff one hour ago: c1's last touch (30m ago) is recent, c2 never -> stale.
	stale := b.NotContactedSince(now.Add(-time.Hour))
	if len(stale) != 1 || stale[0].ContactId != "c2" {
		t.Fatalf("not-contacted-since failed: %+v", stale)
	}
}
