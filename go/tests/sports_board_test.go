// sports_board_test.go
//
// Verifies the CircleAI.Sports port (sports_board.go): logging + newest-first
// history with limit, weekly distance totals, best-time lookup, and session
// schedule/complete/upcoming.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestSports_HistoryAndWeek(t *testing.T) {
	b := circleai.NewInMemorySportsBoard()
	// A Wednesday so the week window (Sunday..) is unambiguous.
	now := time.Date(2026, 7, 8, 12, 0, 0, 0, time.UTC)
	b.Log(circleai.SportsActivity{ActivityId: "a1", UserId: "u1", Kind: circleai.DistanceKindRun, DistanceKm: 5, Duration: 25 * time.Minute, AtUtc: now})
	b.Log(circleai.SportsActivity{ActivityId: "a2", UserId: "u1", Kind: circleai.DistanceKindRun, DistanceKm: 10, Duration: 55 * time.Minute, AtUtc: now.Add(24 * time.Hour)})
	b.Log(circleai.SportsActivity{ActivityId: "a3", UserId: "u1", Kind: circleai.DistanceKindRun, DistanceKm: 3, Duration: 15 * time.Minute, AtUtc: now.Add(-14 * 24 * time.Hour)}) // last fortnight
	b.Log(circleai.SportsActivity{ActivityId: "b1", UserId: "u2", Kind: circleai.DistanceKindRun, DistanceKm: 99, Duration: time.Hour, AtUtc: now})

	hist := b.History("u1", 50)
	if len(hist) != 3 || hist[0].ActivityId != "a2" {
		t.Fatalf("history newest-first failed: %+v", hist)
	}
	if lim := b.History("u1", 1); len(lim) != 1 || lim[0].ActivityId != "a2" {
		t.Fatalf("history limit failed: %+v", lim)
	}
	if km := b.TotalKmThisWeek("u1", circleai.DistanceKindRun, now); km != 15 {
		t.Fatalf("weekly km = %v, want 15", km)
	}
}

func TestSports_BestAndSessions(t *testing.T) {
	b := circleai.NewInMemorySportsBoard()
	now := time.Date(2026, 7, 8, 12, 0, 0, 0, time.UTC)
	b.Log(circleai.SportsActivity{ActivityId: "a1", UserId: "u1", Kind: circleai.DistanceKindRun, DistanceKm: 5, Duration: 30 * time.Minute, AtUtc: now})
	b.Log(circleai.SportsActivity{ActivityId: "a2", UserId: "u1", Kind: circleai.DistanceKindRun, DistanceKm: 5, Duration: 22 * time.Minute, AtUtc: now})

	best, ok := b.Best("u1", circleai.DistanceKindRun, 5)
	if !ok || best.Time != 22*time.Minute {
		t.Fatalf("best = %+v ok=%v, want 22m", best, ok)
	}
	if _, ok := b.Best("u1", circleai.DistanceKindSwim, 5); ok {
		t.Fatalf("best for absent kind must be false")
	}

	future := time.Now().UTC().Add(48 * time.Hour)
	b.Schedule(circleai.TrainingSession{SessionId: "s1", UserId: "u1", Plan: "Intervals", ScheduledUtc: future})
	if up := b.Upcoming("u1"); len(up) != 1 || up[0].SessionId != "s1" {
		t.Fatalf("upcoming failed: %+v", up)
	}
	if err := b.Complete("s1"); err != nil {
		t.Fatalf("complete: %v", err)
	}
	if up := b.Upcoming("u1"); len(up) != 0 {
		t.Fatalf("completed session should not be upcoming: %+v", up)
	}
	if err := b.Complete("ghost"); err == nil {
		t.Fatalf("completing unknown session must error")
	}
}
