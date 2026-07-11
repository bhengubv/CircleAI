// personal_mental_board_test.go
//
// Verifies the CircleAI.Personal.Mental port (personal_mental_board.go): Mood
// ordinals/names, mood logging + 7-day window + average (NaN when empty),
// journal add (blank-id error) + newest-first ordering, and coping-strategy tag
// lookup (case-insensitive, blank-tag error).

package circleai_test

import (
	"math"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestPersonalMental_MoodOrdinals(t *testing.T) {
	if circleai.MoodVeryLow != 0 || circleai.MoodGreat != 4 {
		t.Fatalf("ordinals: verylow=%d great=%d", circleai.MoodVeryLow, circleai.MoodGreat)
	}
	if circleai.MoodNeutral.String() != "Neutral" || circleai.MoodGreat.String() != "Great" {
		t.Fatalf("names wrong: %s / %s", circleai.MoodNeutral, circleai.MoodGreat)
	}
}

func TestPersonalMental_MoodWindowAndAverage(t *testing.T) {
	b := circleai.NewInMemoryMentalHealthBoard()
	// Empty window -> NaN.
	if !math.IsNaN(b.AvgMood7Day()) {
		t.Fatalf("empty avg should be NaN")
	}
	now := time.Now().UTC()
	b.LogMood(circleai.MoodLog{Mood: circleai.MoodGood, AtUtc: now.Add(-1 * time.Hour)})    // 3
	b.LogMood(circleai.MoodLog{Mood: circleai.MoodNeutral, AtUtc: now.Add(-2 * time.Hour)}) // 2
	b.LogMood(circleai.MoodLog{Mood: circleai.MoodGreat, AtUtc: now.Add(-3 * time.Hour)})   // 4
	// Older than 7 days -> excluded from window and average.
	b.LogMood(circleai.MoodLog{Mood: circleai.MoodVeryLow, AtUtc: now.Add(-8 * 24 * time.Hour)})

	last7 := b.Last7Days()
	if len(last7) != 3 {
		t.Fatalf("7-day window = %d, want 3", len(last7))
	}
	// Ascending by time: Great(-3h), Neutral(-2h), Good(-1h).
	if last7[0].Mood != circleai.MoodGreat || last7[2].Mood != circleai.MoodGood {
		t.Fatalf("window order failed: %+v", last7)
	}
	// Average of 3,2,4 = 3.0.
	if avg := b.AvgMood7Day(); math.Abs(avg-3.0) > 1e-9 {
		t.Fatalf("avg = %v, want 3.0", avg)
	}
}

func TestPersonalMental_JournalEntries(t *testing.T) {
	b := circleai.NewInMemoryMentalHealthBoard()
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	if err := b.AddEntry(circleai.JournalEntry{EntryId: "e1", Title: "Day 1", Body: "...", AtUtc: base}); err != nil {
		t.Fatalf("add entry: %v", err)
	}
	_ = b.AddEntry(circleai.JournalEntry{EntryId: "e2", Title: "Day 3", Body: "...", AtUtc: base.Add(48 * time.Hour)})
	_ = b.AddEntry(circleai.JournalEntry{EntryId: "e3", Title: "Day 2", Body: "...", AtUtc: base.Add(24 * time.Hour)})

	entries := b.Entries()
	if len(entries) != 3 || entries[0].EntryId != "e2" || entries[1].EntryId != "e3" || entries[2].EntryId != "e1" {
		t.Fatalf("entries newest-first failed: %+v", entries)
	}
	if err := b.AddEntry(circleai.JournalEntry{EntryId: "  ", Title: "blank"}); err == nil {
		t.Fatalf("blank entry id must error")
	}
}

func TestPersonalMental_StrategiesByTag(t *testing.T) {
	b := circleai.NewInMemoryMentalHealthBoard()
	b.RegisterStrategy(circleai.CopingStrategy{StrategyId: "s1", Title: "Box Breathing", Description: "...", Tags: []string{"Anxiety", "Breathing"}})
	b.RegisterStrategy(circleai.CopingStrategy{StrategyId: "s2", Title: "Walk", Description: "...", Tags: []string{"Exercise"}})
	b.RegisterStrategy(circleai.CopingStrategy{StrategyId: "s3", Title: "Grounding", Description: "...", Tags: []string{"anxiety"}})

	hits, err := b.StrategiesByTag("ANXIETY")
	if err != nil {
		t.Fatalf("by tag: %v", err)
	}
	if len(hits) != 2 || hits[0].StrategyId != "s1" || hits[1].StrategyId != "s3" {
		t.Fatalf("case-insensitive tag match failed: %+v", hits)
	}
	if _, err := b.StrategiesByTag(""); err == nil {
		t.Fatalf("blank tag must error")
	}
}

func TestPersonalMental_StrategyTagsCopied(t *testing.T) {
	b := circleai.NewInMemoryMentalHealthBoard()
	tags := []string{"Anxiety"}
	b.RegisterStrategy(circleai.CopingStrategy{StrategyId: "s1", Title: "X", Tags: tags})
	tags[0] = "MUTATED"
	hits, _ := b.StrategiesByTag("Anxiety")
	if len(hits) != 1 {
		t.Fatalf("strategy tags not defensively copied")
	}
}
