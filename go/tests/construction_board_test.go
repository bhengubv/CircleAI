// construction_board_test.go
//
// Verifies the CircleAI.Construction port (construction_board.go): project
// create/get, task add/complete with open-task ordering, cost recording, spend
// totalling, and remaining-budget.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestConstruction_TasksAndBudget(t *testing.T) {
	b := circleai.NewInMemoryConstructionBoard()
	start := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.Create(circleai.Project{ProjectId: "p1", Name: "House", StartOn: start, Budget: circleai.DecimalFromInt(100000), Currency: "ZAR"})
	if got, ok := b.GetProject("p1"); !ok || got.Name != "House" {
		t.Fatalf("get project = %+v ok=%v", got, ok)
	}

	b.Add(circleai.ConstructionTask{ConstructionTaskId: "t2", ProjectId: "p1", Description: "Roof", DueOn: start.AddDate(0, 0, 30)})
	b.Add(circleai.ConstructionTask{ConstructionTaskId: "t1", ProjectId: "p1", Description: "Foundation", DueOn: start.AddDate(0, 0, 10)})
	b.Add(circleai.ConstructionTask{ConstructionTaskId: "t3", ProjectId: "p1", Description: "Paint", DueOn: start.AddDate(0, 0, 60)})
	if err := b.Complete("t3"); err != nil {
		t.Fatalf("complete: %v", err)
	}
	if err := b.Complete("ghost"); err == nil {
		t.Fatalf("complete unknown must error")
	}
	open := b.OpenConstructionTasksFor("p1")
	if len(open) != 2 || open[0].ConstructionTaskId != "t1" || open[1].ConstructionTaskId != "t2" {
		t.Fatalf("open tasks ordered by DueOn failed: %+v", open)
	}

	now := time.Now().UTC()
	b.RecordCost(circleai.CostEntry{EntryId: "c1", ProjectId: "p1", Category: "materials", Amount: circleai.DecimalFromInt(30000), AtUtc: now})
	b.RecordCost(circleai.CostEntry{EntryId: "c2", ProjectId: "p1", Category: "labour", Amount: circleai.DecimalFromInt(20000), AtUtc: now})
	if spend := b.SpendFor("p1"); !spend.Equal(circleai.DecimalFromInt(50000)) {
		t.Fatalf("spend = %s, want 50000", spend.String())
	}
	rem, err := b.RemainingBudget("p1")
	if err != nil {
		t.Fatalf("remaining: %v", err)
	}
	if !rem.Equal(circleai.DecimalFromInt(50000)) {
		t.Fatalf("remaining budget = %s, want 50000", rem.String())
	}
	if _, err := b.RemainingBudget("ghost"); err == nil {
		t.Fatalf("remaining for unknown project must error")
	}
}
