// sync_goal_store.go
//
// Ports CircleAI.Memory.InMemoryGoalStore (InMemoryGoalStore.cs) — the
// thread-safe in-memory IGoalStore. The Goal type, its enums, and the IGoalStore
// interface are defined in memory.go. All data is lost when the process exits.

package circleai

import (
	"context"
	"errors"
	"sync"
)

// InMemoryGoalStore is a thread-safe in-memory IGoalStore keyed by goal id.
type InMemoryGoalStore struct {
	mu    sync.Mutex
	goals map[string]Goal
}

// NewInMemoryGoalStore returns an empty goal store.
func NewInMemoryGoalStore() *InMemoryGoalStore {
	return &InMemoryGoalStore{goals: make(map[string]Goal)}
}

// List returns all goals for userID, in any order.
func (s *InMemoryGoalStore) List(_ context.Context, userID string) ([]Goal, error) {
	if isBlank(userID) {
		return nil, errors.New("userId required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	list := make([]Goal, 0)
	for _, g := range s.goals {
		if g.UserID == userID {
			list = append(list, g)
		}
	}
	return list, nil
}

// Get returns the goal with the given id, or nil when absent.
func (s *InMemoryGoalStore) Get(_ context.Context, id string) (*Goal, error) {
	if isBlank(id) {
		return nil, errors.New("id required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if g, ok := s.goals[id]; ok {
		cp := g
		return &cp, nil
	}
	return nil, nil
}

// Upsert inserts or replaces the goal (Id is the natural key). Returns it.
func (s *InMemoryGoalStore) Upsert(_ context.Context, goal Goal) (Goal, error) {
	if isBlank(goal.ID) {
		return Goal{}, errors.New("goal.Id required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.goals[goal.ID] = goal
	return goal, nil
}

// Delete removes the goal with the given id. No-op if not found.
func (s *InMemoryGoalStore) Delete(_ context.Context, id string) error {
	if isBlank(id) {
		return errors.New("id required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.goals, id)
	return nil
}

// GetActive returns all goals for userID whose Status is GoalActive.
func (s *InMemoryGoalStore) GetActive(_ context.Context, userID string) ([]Goal, error) {
	if isBlank(userID) {
		return nil, errors.New("userId required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	active := make([]Goal, 0)
	for _, g := range s.goals {
		if g.UserID == userID && g.Status == GoalActive {
			active = append(active, g)
		}
	}
	return active, nil
}

var _ IGoalStore = (*InMemoryGoalStore)(nil)
