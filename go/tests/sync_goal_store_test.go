// sync_goal_store_test.go
//
// Verifies InMemoryGoalStore (ported from InMemoryGoalStore.cs): List/Get/Upsert/
// Delete/GetActive semantics, per-user scoping, and Id-as-natural-key upsert.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func mkGoal(id, user string, status circleai.GoalStatus) circleai.Goal {
	return circleai.Goal{
		ID:         id,
		UserID:     user,
		Title:      "t-" + id,
		Status:     status,
		Priority:   circleai.GoalPriorityNormal,
		CreatedUTC: time.Unix(0, 0).UTC(),
	}
}

func TestGoalStore_UpsertGetDelete(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryGoalStore()

	if got, _ := store.Get(ctx, "g1"); got != nil {
		t.Error("missing goal should be nil")
	}

	if _, err := store.Upsert(ctx, mkGoal("g1", "u1", circleai.GoalActive)); err != nil {
		t.Fatalf("Upsert: %v", err)
	}
	got, _ := store.Get(ctx, "g1")
	if got == nil || got.Title != "t-g1" {
		t.Fatalf("get after upsert: %+v", got)
	}

	// Upsert replaces by Id.
	replaced := mkGoal("g1", "u1", circleai.GoalCompleted)
	replaced.Title = "renamed"
	if _, err := store.Upsert(ctx, replaced); err != nil {
		t.Fatalf("Upsert replace: %v", err)
	}
	got, _ = store.Get(ctx, "g1")
	if got.Title != "renamed" || got.Status != circleai.GoalCompleted {
		t.Errorf("replace failed: %+v", got)
	}

	if err := store.Delete(ctx, "g1"); err != nil {
		t.Fatalf("Delete: %v", err)
	}
	if got, _ := store.Get(ctx, "g1"); got != nil {
		t.Error("goal should be gone after delete")
	}
	// Delete missing is a no-op.
	if err := store.Delete(ctx, "nope"); err != nil {
		t.Errorf("delete missing should be no-op: %v", err)
	}
}

func TestGoalStore_ListScopedByUser(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryGoalStore()
	_, _ = store.Upsert(ctx, mkGoal("a", "u1", circleai.GoalActive))
	_, _ = store.Upsert(ctx, mkGoal("b", "u1", circleai.GoalCompleted))
	_, _ = store.Upsert(ctx, mkGoal("c", "u2", circleai.GoalActive))

	u1, _ := store.List(ctx, "u1")
	if len(u1) != 2 {
		t.Errorf("u1 list: got %d want 2", len(u1))
	}
	u2, _ := store.List(ctx, "u2")
	if len(u2) != 1 {
		t.Errorf("u2 list: got %d want 1", len(u2))
	}
}

func TestGoalStore_GetActiveFiltersStatus(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryGoalStore()
	_, _ = store.Upsert(ctx, mkGoal("a", "u1", circleai.GoalActive))
	_, _ = store.Upsert(ctx, mkGoal("b", "u1", circleai.GoalCompleted))
	_, _ = store.Upsert(ctx, mkGoal("c", "u1", circleai.GoalAbandoned))
	_, _ = store.Upsert(ctx, mkGoal("d", "u1", circleai.GoalActive))

	active, _ := store.GetActive(ctx, "u1")
	if len(active) != 2 {
		t.Fatalf("active: got %d want 2", len(active))
	}
	for _, g := range active {
		if g.Status != circleai.GoalActive {
			t.Errorf("non-active goal in GetActive: %+v", g)
		}
	}
}

func TestGoalStore_Validation(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryGoalStore()
	if _, err := store.List(ctx, ""); err == nil {
		t.Error("blank userId should error")
	}
	if _, err := store.Upsert(ctx, circleai.Goal{}); err == nil {
		t.Error("blank goal id should error")
	}
}
