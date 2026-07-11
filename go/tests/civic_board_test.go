// civic_board_test.go
//
// Verifies the CircleAI.Civic port (civic_board.go): report/resolve with
// open-issue filtering (sorted), reps-for-district (case-insensitive, nil-safe),
// and upcoming events ordering.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestCivic_IssuesAndReps(t *testing.T) {
	b := circleai.NewInMemoryCivicBoard()
	b.Report(circleai.CivicIssue{IssueId: "i2", Category: "roads", Description: "pothole", Status: "Open"})
	b.Report(circleai.CivicIssue{IssueId: "i1", Category: "water", Description: "leak", Status: "Open"})
	b.Report(circleai.CivicIssue{IssueId: "i3", Category: "power", Description: "outage", Status: "Open"})
	if err := b.Resolve("i3", "Resolved"); err != nil {
		t.Fatalf("resolve: %v", err)
	}
	if err := b.Resolve("ghost", "Resolved"); err == nil {
		t.Fatalf("resolve unknown must error")
	}
	open := b.OpenIssues()
	if len(open) != 2 || open[0].IssueId != "i1" || open[1].IssueId != "i2" {
		t.Fatalf("open issues sorted failed: %+v", open)
	}

	d := "Ward 5"
	b.AddRep(circleai.Representative{RepId: "r1", Name: "A", Office: "Councillor", District: &d})
	b.AddRep(circleai.Representative{RepId: "r2", Name: "B", Office: "MP"}) // nil district
	reps := b.RepsForDistrict("ward 5")
	if len(reps) != 1 || reps[0].RepId != "r1" {
		t.Fatalf("reps-for-district failed: %+v", reps)
	}
}

func TestCivic_UpcomingEvents(t *testing.T) {
	b := circleai.NewInMemoryCivicBoard()
	now := time.Now().UTC()
	b.Schedule(circleai.CivicEvent{EventId: "e1", Title: "Later", AtUtc: now.Add(48 * time.Hour), Location: "Hall", Audience: "public"})
	b.Schedule(circleai.CivicEvent{EventId: "e2", Title: "Soon", AtUtc: now.Add(24 * time.Hour), Location: "Hall", Audience: "public"})
	b.Schedule(circleai.CivicEvent{EventId: "e3", Title: "Past", AtUtc: now.Add(-24 * time.Hour), Location: "Hall", Audience: "public"})

	up := b.UpcomingEvents()
	if len(up) != 2 || up[0].EventId != "e2" || up[1].EventId != "e1" {
		t.Fatalf("upcoming events ordered failed: %+v", up)
	}
}
