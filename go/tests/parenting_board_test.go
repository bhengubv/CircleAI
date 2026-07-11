// parenting_board_test.go
//
// Verifies the CircleAI.Parenting port (parenting_board.go): child add/get + name
// ordering, milestone record (blank-ChildId error) + MilestonesFor (newest-first,
// empty for unknown), routine set/get (weekday key, defensive Entries copy), and
// AgeAsOf (at - DOB; unknown-child error).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestParenting_ChildrenAndMilestones(t *testing.T) {
	b := circleai.NewInMemoryParentingBoard()
	dob := time.Date(2018, 3, 15, 0, 0, 0, 0, time.UTC)
	b.AddChild(circleai.Child{ChildId: "c1", Name: "Sipho", DateOfBirth: dob})
	b.AddChild(circleai.Child{ChildId: "c2", Name: "Ayanda", DateOfBirth: dob})

	if c, ok := b.GetChild("c1"); !ok || c.Name != "Sipho" {
		t.Fatalf("get child = %+v ok=%v", c, ok)
	}
	kids := b.Children()
	if len(kids) != 2 || kids[0].Name != "Ayanda" || kids[1].Name != "Sipho" {
		t.Fatalf("children name order wrong: %+v", kids)
	}

	t0 := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	_ = b.RecordMilestone(circleai.Milestone{MilestoneId: "m1", ChildId: "c1", Category: "Motor", Description: "First steps", AchievedAtUtc: t0})
	_ = b.RecordMilestone(circleai.Milestone{MilestoneId: "m2", ChildId: "c1", Category: "Language", Description: "First word", AchievedAtUtc: t0.Add(24 * time.Hour)})
	if err := b.RecordMilestone(circleai.Milestone{MilestoneId: "bad", ChildId: " "}); err == nil {
		t.Fatalf("blank ChildId must error")
	}

	ms := b.MilestonesFor("c1")
	// Newest first: m2 then m1.
	if len(ms) != 2 || ms[0].MilestoneId != "m2" || ms[1].MilestoneId != "m1" {
		t.Fatalf("milestones order wrong: %+v", ms)
	}
	if unknown := b.MilestonesFor("ghost"); len(unknown) != 0 {
		t.Fatalf("unknown child milestones must be empty")
	}
}

func TestParenting_RoutinesAndAge(t *testing.T) {
	b := circleai.NewInMemoryParentingBoard()
	dob := time.Date(2020, 1, 1, 0, 0, 0, 0, time.UTC)
	b.AddChild(circleai.Child{ChildId: "c1", Name: "Lerato", DateOfBirth: dob})

	entries := []circleai.RoutineEntry{{Time: "07:00", Activity: "Wake"}, {Time: "08:00", Activity: "School"}}
	b.SetRoutine(circleai.Routine{ChildId: "c1", DayOfWeek: time.Monday, Entries: entries})
	entries[0].Activity = "MUTATED" // must not affect stored routine

	r, ok := b.GetRoutine("c1", time.Monday)
	if !ok || len(r.Entries) != 2 || r.Entries[0].Activity != "Wake" {
		t.Fatalf("get routine wrong (defensive copy?): %+v ok=%v", r, ok)
	}
	// Different weekday -> absent.
	if _, ok := b.GetRoutine("c1", time.Tuesday); ok {
		t.Fatalf("tuesday routine must be absent")
	}

	at := time.Date(2021, 1, 1, 0, 0, 0, 0, time.UTC)
	age, err := b.AgeAsOf("c1", at)
	if err != nil {
		t.Fatalf("age: %v", err)
	}
	if age != at.Sub(dob) {
		t.Fatalf("age = %v, want %v", age, at.Sub(dob))
	}
	if _, err := b.AgeAsOf("ghost", at); err == nil {
		t.Fatalf("unknown child age must error")
	}
}
