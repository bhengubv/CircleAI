// creative_board_test.go
//
// Verifies the CircleAI.Creative port (creative_board.go): work add/get, works
// by tag (case-insensitive, sorted), recent inspiration newest-first with limit,
// and average critique score (0 when none).

package circleai_test

import (
	"math"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestCreative_WorksAndTags(t *testing.T) {
	b := circleai.NewInMemoryCreativeBoard()
	now := time.Now().UTC()
	b.AddWork(circleai.CreativeWork{WorkId: "w2", Title: "Beta", Medium: "oil", Author: "A", CreatedUtc: now, Tags: []string{"portrait"}})
	b.AddWork(circleai.CreativeWork{WorkId: "w1", Title: "Alpha", Medium: "ink", Author: "A", CreatedUtc: now, Tags: []string{"Portrait"}})
	b.AddWork(circleai.CreativeWork{WorkId: "w3", Title: "Gamma", Medium: "oil", Author: "B", CreatedUtc: now, Tags: []string{"landscape"}})
	if got, ok := b.GetWork("w1"); !ok || got.Title != "Alpha" {
		t.Fatalf("get work = %+v ok=%v", got, ok)
	}
	tagged := b.WorksByTag("PORTRAIT")
	if len(tagged) != 2 || tagged[0].WorkId != "w1" || tagged[1].WorkId != "w2" {
		t.Fatalf("works-by-tag (case-insensitive, sorted) failed: %+v", tagged)
	}
}

func TestCreative_InspirationAndScore(t *testing.T) {
	b := circleai.NewInMemoryCreativeBoard()
	now := time.Now().UTC()
	b.RecordInspiration(circleai.Inspiration{InspirationId: "i1", PromptText: "old", SourceUrl: "u1", SeenUtc: now.Add(-time.Hour)})
	b.RecordInspiration(circleai.Inspiration{InspirationId: "i2", PromptText: "new", SourceUrl: "u2", SeenUtc: now})
	rec := b.RecentInspiration(20)
	if len(rec) != 2 || rec[0].InspirationId != "i2" {
		t.Fatalf("recent inspiration newest-first failed: %+v", rec)
	}
	if lim := b.RecentInspiration(1); len(lim) != 1 || lim[0].InspirationId != "i2" {
		t.Fatalf("recent inspiration limit failed: %+v", lim)
	}

	b.AddCritique(circleai.Critique{CritiqueId: "c1", WorkId: "w1", Reviewer: "R", Body: "good", Score: 8})
	b.AddCritique(circleai.Critique{CritiqueId: "c2", WorkId: "w1", Reviewer: "S", Body: "ok", Score: 6})
	if avg := b.AvgScore("w1"); math.Abs(avg-7.0) > 1e-9 {
		t.Fatalf("avg score = %v, want 7", avg)
	}
	if avg := b.AvgScore("none"); avg != 0.0 {
		t.Fatalf("avg score (none) = %v, want 0", avg)
	}
}
