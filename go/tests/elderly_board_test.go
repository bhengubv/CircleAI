// elderly_board_test.go
//
// Verifies the CircleAI.Elderly port (elderly_board.go): care-plan set/get
// (defensive list copy), reminder add/deactivate (unknown-id error) +
// ActiveRemindersFor (resident + active filter), check-in record + LatestCheckIn
// (most recent; absent when none), and MissedCheckIn (no check-in or latest before
// `since`).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestElderly_PlanAndReminders(t *testing.T) {
	b := circleai.NewInMemoryElderlyCareBoard()
	conditions := []string{"Diabetes"}
	b.SetPlan(circleai.CarePlan{PlanId: "cp1", ResidentName: "Gogo", MedicalConditions: conditions, Allergies: []string{"Penicillin"}, CarerNotes: "Gentle"})
	conditions[0] = "MUTATED" // must not affect stored plan

	plan, ok := b.GetPlan("Gogo")
	if !ok || len(plan.MedicalConditions) != 1 || plan.MedicalConditions[0] != "Diabetes" {
		t.Fatalf("get plan wrong (defensive copy?): %+v ok=%v", plan, ok)
	}
	if _, ok := b.GetPlan("Nobody"); ok {
		t.Fatalf("unknown resident plan must be absent")
	}

	b.AddReminder(circleai.MedReminder{ReminderId: "r1", ResidentName: "Gogo", Medication: "Metformin", DailyAt: 8 * time.Hour, Active: true})
	b.AddReminder(circleai.MedReminder{ReminderId: "r2", ResidentName: "Gogo", Medication: "Aspirin", DailyAt: 20 * time.Hour, Active: true})
	b.AddReminder(circleai.MedReminder{ReminderId: "r3", ResidentName: "Mkhulu", Medication: "Statin", DailyAt: 21 * time.Hour, Active: true})

	if err := b.DeactivateReminder("r2"); err != nil {
		t.Fatalf("deactivate: %v", err)
	}
	if err := b.DeactivateReminder("ghost"); err == nil {
		t.Fatalf("unknown reminder deactivate must error")
	}
	active := b.ActiveRemindersFor("Gogo")
	// r1 active (r2 deactivated, r3 is a different resident). Sorted by ReminderId.
	if len(active) != 1 || active[0].ReminderId != "r1" {
		t.Fatalf("active reminders wrong: %+v", active)
	}
}

func TestElderly_CheckIns(t *testing.T) {
	b := circleai.NewInMemoryElderlyCareBoard()
	t0 := time.Date(2026, 7, 1, 8, 0, 0, 0, time.UTC)
	b.RecordCheckIn(circleai.ElderlyCheckIn{CheckInId: "k1", ResidentName: "Gogo", AtUtc: t0, Status: "OK"})
	b.RecordCheckIn(circleai.ElderlyCheckIn{CheckInId: "k2", ResidentName: "Gogo", AtUtc: t0.Add(12 * time.Hour), Status: "OK"})

	latest, ok := b.LatestCheckIn("Gogo")
	if !ok || latest.CheckInId != "k2" {
		t.Fatalf("latest check-in = %+v ok=%v, want k2", latest, ok)
	}
	if _, ok := b.LatestCheckIn("Nobody"); ok {
		t.Fatalf("unknown resident latest check-in must be absent")
	}

	// Not missed: latest (t0+12h) is at/after `since` = t0.
	if b.MissedCheckIn("Gogo", t0) {
		t.Fatalf("should not be missed when latest >= since")
	}
	// Missed: `since` is after the latest check-in.
	if !b.MissedCheckIn("Gogo", t0.Add(24*time.Hour)) {
		t.Fatalf("should be missed when latest < since")
	}
	// Missed: no check-in at all.
	if !b.MissedCheckIn("Nobody", t0) {
		t.Fatalf("should be missed when no check-in exists")
	}
}
