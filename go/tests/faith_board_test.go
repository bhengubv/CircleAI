// faith_board_test.go
//
// Verifies the CircleAI.Faith port (faith_board.go): services in a window
// (ordered), recent prayers newest-first with limit, scripture lookup
// (case-sensitive tradition/book) and by-tradition listing (case-insensitive).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestFaith_ServicesAndPrayers(t *testing.T) {
	b := circleai.NewInMemoryFaithBoard()
	base := time.Date(2026, 7, 5, 8, 0, 0, 0, time.UTC)
	b.Schedule(circleai.FaithService{ServiceId: "s2", CommunityName: "Grace", Title: "Evening", StartUtc: base.Add(12 * time.Hour), Location: "Hall"})
	b.Schedule(circleai.FaithService{ServiceId: "s1", CommunityName: "Grace", Title: "Morning", StartUtc: base, Location: "Hall"})
	b.Schedule(circleai.FaithService{ServiceId: "s3", CommunityName: "Grace", Title: "NextWeek", StartUtc: base.AddDate(0, 0, 7), Location: "Hall"})

	svc := b.ServicesBetween(base, base.Add(24*time.Hour))
	if len(svc) != 2 || svc[0].ServiceId != "s1" || svc[1].ServiceId != "s2" {
		t.Fatalf("services-between ordered failed: %+v", svc)
	}

	now := time.Now().UTC()
	b.SubmitPrayer(circleai.PrayerRequest{RequestId: "p1", Author: "A", Body: "old", SubmittedUtc: now.Add(-time.Hour)})
	b.SubmitPrayer(circleai.PrayerRequest{RequestId: "p2", Author: "B", Body: "new", SubmittedUtc: now, IsAnonymous: true})
	rec := b.RecentPrayers(20)
	if len(rec) != 2 || rec[0].RequestId != "p2" {
		t.Fatalf("recent prayers newest-first failed: %+v", rec)
	}
	if lim := b.RecentPrayers(1); len(lim) != 1 || lim[0].RequestId != "p2" {
		t.Fatalf("recent prayers limit failed: %+v", lim)
	}
}

func TestFaith_Scripture(t *testing.T) {
	b := circleai.NewInMemoryFaithBoard()
	b.AddScripture(circleai.ScriptureReference{ReferenceId: "r2", Tradition: "Christian", Book: "John", Chapter: 3, Verse: 16, Text: "For God..."})
	b.AddScripture(circleai.ScriptureReference{ReferenceId: "r1", Tradition: "Christian", Book: "Psalms", Chapter: 23, Verse: 1, Text: "The Lord..."})

	if got, ok := b.Lookup("Christian", "John", 3, 16); !ok || got.ReferenceId != "r2" {
		t.Fatalf("scripture lookup = %+v ok=%v", got, ok)
	}
	// Tradition is case-sensitive per the C# == comparison.
	if _, ok := b.Lookup("christian", "John", 3, 16); ok {
		t.Fatalf("case-mismatched tradition must not match")
	}
	byTrad := b.ByTradition("CHRISTIAN")
	if len(byTrad) != 2 || byTrad[0].ReferenceId != "r1" || byTrad[1].ReferenceId != "r2" {
		t.Fatalf("by-tradition (case-insensitive, sorted) failed: %+v", byTrad)
	}
}
