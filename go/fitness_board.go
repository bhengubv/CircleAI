// fitness_board.go
//
// Ports the CircleAI.Fitness primitive vertical (FitnessPrimitives.cs):
//   Workout / FitnessGoal / ExerciseSet (records) -> value structs
//   IFitnessBoard            -> FitnessBoard interface (I-prefix dropped)
//   InMemoryFitnessBoard     -> InMemoryFitnessBoard
//
// The FitnessDomainContext / FitnessCompanionAdapter (LLM glue) are out of
// scope and are not ported.
//
// DETERMINISM: WorkoutsThisWeek orders by AtUtc ascending (week start = the
// Sunday of now's week, matching the C# now.Date.AddDays(-(int)now.DayOfWeek)).
// GoalsFor mirrors a ConcurrentDictionary in C# (no defined order); this port
// sorts by GoalId for stable output. SetsFor preserves insertion order, matching
// the C# backing List.

package circleai

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// Workout is a logged workout. Ports the Workout record.
type Workout struct {
	WorkoutId       string
	UserId          string
	Kind            string
	DurationMinutes int
	CaloriesBurned  float64
	AtUtc           time.Time
}

// FitnessGoal is a user fitness goal. Ports the FitnessGoal record. DueOn
// mirrors the C# DateTime.
type FitnessGoal struct {
	GoalId string
	UserId string
	Metric string
	Target float64
	DueOn  time.Time
}

// ExerciseSet is one set within a workout. Ports the ExerciseSet record.
type ExerciseSet struct {
	SetId      string
	WorkoutId  string
	Exercise   string
	Reps       int
	WeightKg   float64
}

// FitnessBoard is the workouts/goals/sets board. Ports IFitnessBoard.
type FitnessBoard interface {
	Log(w Workout)
	// WorkoutsThisWeek lists a user's workouts in now's calendar week, oldest-first.
	WorkoutsThisWeek(userId string, now time.Time) []Workout
	// TotalCaloriesSince sums CaloriesBurned for a user at/after since.
	TotalCaloriesSince(userId string, since time.Time) float64
	SetGoal(g FitnessGoal)
	// GoalsFor lists a user's goals (sorted by GoalId for determinism).
	GoalsFor(userId string) []FitnessGoal
	AddSet(s ExerciseSet)
	// SetsFor lists a workout's sets in insertion order.
	SetsFor(workoutId string) []ExerciseSet
	// WorkoutCount returns the total number of logged workouts.
	WorkoutCount() int
	// WorkoutsByKind lists a user's workouts of a kind (case-insensitive), newest-first.
	WorkoutsByKind(userId, kind string) []Workout
	// RemoveGoal drops a goal by id, returning true if present.
	RemoveGoal(goalId string) bool
	// GoalByMetric returns a user's earliest-due goal for a metric (case-insensitive).
	GoalByMetric(userId, metric string) (FitnessGoal, bool)
	// AvgDurationSince is the mean workout duration (minutes) for a user at/after since.
	AvgDurationSince(userId string, since time.Time) float64
	// TotalVolumeKg sums Reps*WeightKg across a workout's sets.
	TotalVolumeKg(workoutId string) float64
}

// InMemoryFitnessBoard is a concurrency-safe in-memory FitnessBoard. Ports
// InMemoryFitnessBoard.
type InMemoryFitnessBoard struct {
	mu       sync.Mutex
	workouts []Workout
	goals    map[string]FitnessGoal
	sets     []ExerciseSet
}

// NewInMemoryFitnessBoard constructs an empty board.
func NewInMemoryFitnessBoard() *InMemoryFitnessBoard {
	return &InMemoryFitnessBoard{goals: make(map[string]FitnessGoal)}
}

// Log appends a workout. Ports Log.
func (b *InMemoryFitnessBoard) Log(w Workout) {
	b.mu.Lock()
	b.workouts = append(b.workouts, w)
	b.mu.Unlock()
}

// WorkoutsThisWeek lists a user's workouts in now's week, oldest-first. Ports
// WorkoutsThisWeek.
func (b *InMemoryFitnessBoard) WorkoutsThisWeek(userId string, now time.Time) []Workout {
	weekStart := weekStartOf(now)
	b.mu.Lock()
	out := make([]Workout, 0)
	for _, w := range b.workouts {
		if w.UserId == userId && !w.AtUtc.Before(weekStart) {
			out = append(out, w)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.Before(out[j].AtUtc) })
	return out
}

// TotalCaloriesSince sums a user's calories at/after since. Ports
// TotalCaloriesSince.
func (b *InMemoryFitnessBoard) TotalCaloriesSince(userId string, since time.Time) float64 {
	b.mu.Lock()
	defer b.mu.Unlock()
	var sum float64
	for _, w := range b.workouts {
		if w.UserId == userId && !w.AtUtc.Before(since) {
			sum += w.CaloriesBurned
		}
	}
	return sum
}

// SetGoal stores (or replaces by GoalId) a goal. Ports SetGoal.
func (b *InMemoryFitnessBoard) SetGoal(g FitnessGoal) {
	b.mu.Lock()
	b.goals[g.GoalId] = g
	b.mu.Unlock()
}

// GoalsFor lists a user's goals sorted by GoalId. Ports GoalsFor.
func (b *InMemoryFitnessBoard) GoalsFor(userId string) []FitnessGoal {
	b.mu.Lock()
	out := make([]FitnessGoal, 0)
	for _, g := range b.goals {
		if g.UserId == userId {
			out = append(out, g)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].GoalId < out[j].GoalId })
	return out
}

// AddSet appends an exercise set. Ports AddSet.
func (b *InMemoryFitnessBoard) AddSet(s ExerciseSet) {
	b.mu.Lock()
	b.sets = append(b.sets, s)
	b.mu.Unlock()
}

// SetsFor lists a workout's sets in insertion order. Ports SetsFor.
func (b *InMemoryFitnessBoard) SetsFor(workoutId string) []ExerciseSet {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]ExerciseSet, 0)
	for _, s := range b.sets {
		if s.WorkoutId == workoutId {
			out = append(out, s)
		}
	}
	return out
}

// WorkoutCount returns the total number of logged workouts. Ports
// InMemoryFitnessBoard.WorkoutCount.
func (b *InMemoryFitnessBoard) WorkoutCount() int {
	b.mu.Lock()
	defer b.mu.Unlock()
	return len(b.workouts)
}

// WorkoutsByKind lists a user's workouts of a kind (Kind matched
// case-insensitively), ordered by AtUtc descending. Ports
// InMemoryFitnessBoard.WorkoutsByKind.
func (b *InMemoryFitnessBoard) WorkoutsByKind(userId, kind string) []Workout {
	b.mu.Lock()
	out := make([]Workout, 0)
	for _, w := range b.workouts {
		if w.UserId == userId && strings.EqualFold(w.Kind, kind) {
			out = append(out, w)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.After(out[j].AtUtc) })
	return out
}

// RemoveGoal drops a goal by id, returning true if present. Ports
// InMemoryFitnessBoard.RemoveGoal (TryRemove).
func (b *InMemoryFitnessBoard) RemoveGoal(goalId string) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	_, ok := b.goals[goalId]
	delete(b.goals, goalId)
	return ok
}

// GoalByMetric returns a user's earliest-due goal for a metric (Metric matched
// case-insensitively), or (zero,false) when none. Ties on DueOn resolve
// deterministically by GoalId (the goals map is unordered; the port pre-sorts for
// stable output). Ports InMemoryFitnessBoard.GoalByMetric
// (OrderBy(DueOn).FirstOrDefault()).
func (b *InMemoryFitnessBoard) GoalByMetric(userId, metric string) (FitnessGoal, bool) {
	b.mu.Lock()
	out := make([]FitnessGoal, 0)
	for _, g := range b.goals {
		if g.UserId == userId && strings.EqualFold(g.Metric, metric) {
			out = append(out, g)
		}
	}
	b.mu.Unlock()
	if len(out) == 0 {
		return FitnessGoal{}, false
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].GoalId < out[j].GoalId })
	sort.SliceStable(out, func(i, j int) bool { return out[i].DueOn.Before(out[j].DueOn) })
	return out[0], true
}

// AvgDurationSince is the mean DurationMinutes across a user's workouts at/after
// since, or 0 when there are none (mirrors DefaultIfEmpty(0).Average()). Ports
// InMemoryFitnessBoard.AvgDurationSince.
func (b *InMemoryFitnessBoard) AvgDurationSince(userId string, since time.Time) float64 {
	b.mu.Lock()
	defer b.mu.Unlock()
	var sum float64
	var n int
	for _, w := range b.workouts {
		if w.UserId == userId && !w.AtUtc.Before(since) {
			sum += float64(w.DurationMinutes)
			n++
		}
	}
	if n == 0 {
		return 0
	}
	return sum / float64(n)
}

// TotalVolumeKg sums Reps*WeightKg across a workout's sets. Ports
// InMemoryFitnessBoard.TotalVolumeKg (Sum(s => s.Reps * s.WeightKg)).
func (b *InMemoryFitnessBoard) TotalVolumeKg(workoutId string) float64 {
	b.mu.Lock()
	defer b.mu.Unlock()
	var total float64
	for _, s := range b.sets {
		if s.WorkoutId == workoutId {
			total += float64(s.Reps) * s.WeightKg
		}
	}
	return total
}

// Interface guard.
var _ FitnessBoard = (*InMemoryFitnessBoard)(nil)
