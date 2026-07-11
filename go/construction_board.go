// construction_board.go
//
// Ports the CircleAI.Construction primitive vertical (ConstructionPrimitives.cs):
//   Project / ConstructionTask / CostEntry (records) -> value structs
//   IConstructionBoard       -> ConstructionBoard interface (I-prefix dropped)
//   InMemoryConstructionBoard -> InMemoryConstructionBoard
//
// The ConstructionDomainContext / ConstructionCompanionAdapter (LLM glue) are
// out of scope.
//
// DETERMINISM: OpenConstructionTasksFor orders by DueOn ascending (ties by
// ConstructionTaskId for stable output). SpendFor sums this project's cost
// entries; RemainingBudget is Budget - SpendFor. Money uses the shared exact
// Decimal (C# decimal). Project.EndOn is a *time.Time for the C# nullable.

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// Project is a construction project. Ports the Project record. Budget uses the
// shared exact Decimal; EndOn is a *time.Time for the C# nullable DateTime.
type Project struct {
	ProjectId string
	Name      string
	StartOn   time.Time
	EndOn     *time.Time
	Budget    Decimal
	Currency  string
}

// ConstructionTask is a task within a project. Ports the ConstructionTask record
// (renamed from Task in C# to avoid System.Threading.Task).
type ConstructionTask struct {
	ConstructionTaskId string
	ProjectId          string
	Description        string
	DueOn              time.Time
	Completed          bool
}

// CostEntry is a logged project cost. Ports the CostEntry record. Amount uses
// the shared exact Decimal.
type CostEntry struct {
	EntryId   string
	ProjectId string
	Category  string
	Amount    Decimal
	AtUtc     time.Time
}

// ConstructionBoard is the projects/tasks/costs board. Ports IConstructionBoard.
type ConstructionBoard interface {
	Create(p Project)
	GetProject(id string) (Project, bool)
	Add(t ConstructionTask)
	// Complete marks a task done; errors on unknown id.
	Complete(taskId string) error
	// OpenConstructionTasksFor lists a project's incomplete tasks by DueOn.
	OpenConstructionTasksFor(projectId string) []ConstructionTask
	RecordCost(c CostEntry)
	// SpendFor totals a project's cost entries.
	SpendFor(projectId string) Decimal
	// RemainingBudget is Budget - SpendFor; errors on unknown project.
	RemainingBudget(projectId string) (Decimal, error)
}

// InMemoryConstructionBoard is a concurrency-safe in-memory ConstructionBoard.
// Ports InMemoryConstructionBoard.
type InMemoryConstructionBoard struct {
	mu       sync.Mutex
	projects map[string]Project
	tasks    map[string]ConstructionTask
	costs    []CostEntry
}

// NewInMemoryConstructionBoard constructs an empty board.
func NewInMemoryConstructionBoard() *InMemoryConstructionBoard {
	return &InMemoryConstructionBoard{
		projects: make(map[string]Project),
		tasks:    make(map[string]ConstructionTask),
	}
}

// Create stores (or replaces by ProjectId) a project. Ports Create.
func (b *InMemoryConstructionBoard) Create(p Project) {
	b.mu.Lock()
	b.projects[p.ProjectId] = p
	b.mu.Unlock()
}

// GetProject returns the project for id, or (zero,false). Ports GetProject.
func (b *InMemoryConstructionBoard) GetProject(id string) (Project, bool) {
	b.mu.Lock()
	p, ok := b.projects[id]
	b.mu.Unlock()
	return p, ok
}

// Add stores (or replaces by ConstructionTaskId) a task. Ports Add.
func (b *InMemoryConstructionBoard) Add(t ConstructionTask) {
	b.mu.Lock()
	b.tasks[t.ConstructionTaskId] = t
	b.mu.Unlock()
}

// Complete marks a task done. Ports Complete (throws on unknown id -> error).
func (b *InMemoryConstructionBoard) Complete(taskId string) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	t, ok := b.tasks[taskId]
	if !ok {
		return errors.New("Unknown task " + taskId)
	}
	t.Completed = true
	b.tasks[taskId] = t
	return nil
}

// OpenConstructionTasksFor lists a project's incomplete tasks by DueOn. Ports
// OpenConstructionTasksFor.
func (b *InMemoryConstructionBoard) OpenConstructionTasksFor(projectId string) []ConstructionTask {
	b.mu.Lock()
	out := make([]ConstructionTask, 0)
	for _, t := range b.tasks {
		if t.ProjectId == projectId && !t.Completed {
			out = append(out, t)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].DueOn.Equal(out[j].DueOn) {
			return out[i].DueOn.Before(out[j].DueOn)
		}
		return out[i].ConstructionTaskId < out[j].ConstructionTaskId
	})
	return out
}

// RecordCost appends a cost entry. Ports RecordCost.
func (b *InMemoryConstructionBoard) RecordCost(c CostEntry) {
	b.mu.Lock()
	b.costs = append(b.costs, c)
	b.mu.Unlock()
}

// SpendFor totals a project's cost entries. Ports SpendFor.
func (b *InMemoryConstructionBoard) SpendFor(projectId string) Decimal {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.spendForLocked(projectId)
}

// spendForLocked totals a project's costs. The caller must hold b.mu.
func (b *InMemoryConstructionBoard) spendForLocked(projectId string) Decimal {
	var total Decimal
	for _, c := range b.costs {
		if c.ProjectId == projectId {
			total = total.Add(c.Amount)
		}
	}
	return total
}

// RemainingBudget is Budget - SpendFor. Ports RemainingBudget (throws on unknown
// project -> error).
func (b *InMemoryConstructionBoard) RemainingBudget(projectId string) (Decimal, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	p, ok := b.projects[projectId]
	if !ok {
		return Decimal{}, errors.New("Unknown project " + projectId)
	}
	return p.Budget.Sub(b.spendForLocked(projectId)), nil
}

// Interface guard.
var _ ConstructionBoard = (*InMemoryConstructionBoard)(nil)
