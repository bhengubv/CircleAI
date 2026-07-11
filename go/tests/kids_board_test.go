// kids_board_test.go
//
// Verifies the CircleAI.Kids port (kids_board.go): content by age band
// (ordered), limits set/get, usage-today totals per kind, and over-limit
// detection against screen/reading caps.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestKids_ContentAndLimits(t *testing.T) {
	b := circleai.NewInMemoryKidsBoard()
	b.AddContent(circleai.KidsContent{ContentId: "c2", Title: "Zoo Tales", AgeBand: circleai.AgeAppropriatenessPreschool, Kind: "video", Tags: []string{"animals"}})
	b.AddContent(circleai.KidsContent{ContentId: "c1", Title: "ABC Song", AgeBand: circleai.AgeAppropriatenessPreschool, Kind: "video", Tags: []string{"letters"}})
	b.AddContent(circleai.KidsContent{ContentId: "c3", Title: "Algebra", AgeBand: circleai.AgeAppropriatenessTeen, Kind: "course"})

	pre := b.ContentFor(circleai.AgeAppropriatenessPreschool)
	if len(pre) != 2 || pre[0].Title != "ABC Song" || pre[1].Title != "Zoo Tales" {
		t.Fatalf("content-for band (ordered by Title) failed: %+v", pre)
	}

	b.SetLimits(circleai.DailyTime{KidName: "Kid", ScreenLimit: 2 * time.Hour, ReadingLimit: time.Hour})
	if got, ok := b.LimitsFor("Kid"); !ok || got.ScreenLimit != 2*time.Hour {
		t.Fatalf("limits-for = %+v ok=%v", got, ok)
	}
	if _, ok := b.LimitsFor("Nobody"); ok {
		t.Fatalf("limits for unknown kid must be false")
	}
}

func TestKids_UsageAndOverLimit(t *testing.T) {
	b := circleai.NewInMemoryKidsBoard()
	now := time.Date(2026, 7, 10, 15, 0, 0, 0, time.UTC)
	b.SetLimits(circleai.DailyTime{KidName: "Kid", ScreenLimit: 2 * time.Hour, ReadingLimit: time.Hour})
	b.RecordTime(circleai.TimeLog{KidName: "Kid", Kind: "screen", Duration: 90 * time.Minute, AtUtc: now})
	b.RecordTime(circleai.TimeLog{KidName: "Kid", Kind: "screen", Duration: 45 * time.Minute, AtUtc: now.Add(time.Hour)})
	b.RecordTime(circleai.TimeLog{KidName: "Kid", Kind: "screen", Duration: 30 * time.Minute, AtUtc: now.AddDate(0, 0, -1)}) // yesterday

	if used := b.UsedToday("Kid", "screen", now); used != 135*time.Minute {
		t.Fatalf("used today = %v, want 2h15m", used)
	}
	// 135m > 120m screen cap -> over.
	if !b.OverLimit("Kid", "screen", now) {
		t.Fatalf("expected over screen limit")
	}
	// No reading logged -> under.
	if b.OverLimit("Kid", "reading", now) {
		t.Fatalf("expected under reading limit")
	}
	// Unknown kind -> never over (C# TimeSpan.MaxValue branch).
	if b.OverLimit("Kid", "music", now) {
		t.Fatalf("unknown kind must never be over limit")
	}
	// Unknown kid -> false.
	if b.OverLimit("Nobody", "screen", now) {
		t.Fatalf("unknown kid must not be over limit")
	}
}
