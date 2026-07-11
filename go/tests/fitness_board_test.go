// fitness_board_test.go
//
// Verifies the CircleAI.Fitness port (fitness_board.go): this-week workouts,
// calories-since totals, goals-for filtering (sorted), and sets-for insertion
// order.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestFitness_WorkoutsAndCalories(t *testing.T) {
	b := circleai.NewInMemoryFitnessBoard()
	now := time.Date(2026, 7, 8, 12, 0, 0, 0, time.UTC)
	b.Log(circleai.Workout{WorkoutId: "w1", UserId: "u1", Kind: "run", DurationMinutes: 30, CaloriesBurned: 300, AtUtc: now})
	b.Log(circleai.Workout{WorkoutId: "w2", UserId: "u1", Kind: "bike", DurationMinutes: 45, CaloriesBurned: 400, AtUtc: now.Add(-24 * time.Hour)})
	b.Log(circleai.Workout{WorkoutId: "w3", UserId: "u1", Kind: "swim", DurationMinutes: 20, CaloriesBurned: 200, AtUtc: now.Add(-14 * 24 * time.Hour)})

	wk := b.WorkoutsThisWeek("u1", now)
	if len(wk) != 2 || wk[0].WorkoutId != "w2" || wk[1].WorkoutId != "w1" {
		t.Fatalf("this-week workouts oldest-first failed: %+v", wk)
	}
	if cal := b.TotalCaloriesSince("u1", now.Add(-48*time.Hour)); cal != 700 {
		t.Fatalf("calories since = %v, want 700", cal)
	}
}

func TestFitness_GoalsAndSets(t *testing.T) {
	b := circleai.NewInMemoryFitnessBoard()
	b.SetGoal(circleai.FitnessGoal{GoalId: "g2", UserId: "u1", Metric: "weight", Target: 80})
	b.SetGoal(circleai.FitnessGoal{GoalId: "g1", UserId: "u1", Metric: "5k", Target: 25})
	b.SetGoal(circleai.FitnessGoal{GoalId: "g3", UserId: "u2", Metric: "steps", Target: 10000})

	goals := b.GoalsFor("u1")
	if len(goals) != 2 || goals[0].GoalId != "g1" || goals[1].GoalId != "g2" {
		t.Fatalf("goals-for sorted failed: %+v", goals)
	}

	b.AddSet(circleai.ExerciseSet{SetId: "s1", WorkoutId: "w1", Exercise: "squat", Reps: 5, WeightKg: 100})
	b.AddSet(circleai.ExerciseSet{SetId: "s2", WorkoutId: "w1", Exercise: "squat", Reps: 5, WeightKg: 105})
	b.AddSet(circleai.ExerciseSet{SetId: "s3", WorkoutId: "w2", Exercise: "bench", Reps: 8, WeightKg: 60})

	sets := b.SetsFor("w1")
	if len(sets) != 2 || sets[0].SetId != "s1" || sets[1].SetId != "s2" {
		t.Fatalf("sets-for insertion order failed: %+v", sets)
	}
}
